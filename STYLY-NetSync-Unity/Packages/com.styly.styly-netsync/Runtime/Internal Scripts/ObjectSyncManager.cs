// ObjectSyncManager.cs - Manages high-frequency transform synchronization for non-avatar objects
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Internal manager responsible for collecting, sending, and receiving object transforms.
    /// Analogous to TransformSyncManager for avatars.
    /// </summary>
    internal class ObjectSyncManager
    {
        private readonly IConnectionManager _connectionManager;
        private readonly ReusableBufferWriter _buf;

        // Object registry: objectId -> NetSyncObject
        private readonly Dictionary<ushort, NetSyncObject> _registry = new Dictionary<ushort, NetSyncObject>();

        // Reusable list for collecting dirty objects each frame
        private readonly List<ObjectTransformEntry> _dirtyEntries = new List<ObjectTransformEntry>(64);

        // Send rate control
        private float _lastSendTime;
        private ushort _poseSeq;
        private bool _hasLastSignature;
        private ulong _lastSignature;
        private float _lastSentTime;
        private int _messagesSent;

        private const int INITIAL_BUFFER_CAPACITY = 1024;
        private const float HEARTBEAT_INTERVAL_SECONDS = 1f;

        // 5 bytes header + 15 bytes per object
        private const int HEADER_SIZE = 5;
        private const int BYTES_PER_OBJECT = 15;

        public float SendRate { get; set; } = 10f;
        public int MessagesSent => _messagesSent;

        /// <summary>
        /// Read-only access to the object registry for iteration.
        /// </summary>
        public IReadOnlyDictionary<ushort, NetSyncObject> Registry => _registry;

        public ObjectSyncManager(IConnectionManager connectionManager, float sendRate)
        {
            _connectionManager = connectionManager;
            SendRate = sendRate;
            _buf = new ReusableBufferWriter(INITIAL_BUFFER_CAPACITY);
        }

        /// <summary>
        /// Register a NetSyncObject. Called by NetSyncObject.OnEnable.
        /// </summary>
        public void Register(NetSyncObject obj)
        {
            if (obj == null) return;
            var id = obj.ObjectId;
            if (id == 0)
            {
                Debug.LogWarning("[ObjectSyncManager] Cannot register object with ObjectId=0 (unassigned).");
                return;
            }
            if (_registry.ContainsKey(id))
            {
                Debug.LogWarning($"[ObjectSyncManager] Duplicate ObjectId={id}. Overwriting.");
            }
            _registry[id] = obj;
        }

        /// <summary>
        /// Unregister a NetSyncObject. Called by NetSyncObject.OnDisable.
        /// </summary>
        public void Unregister(NetSyncObject obj)
        {
            if (obj == null) return;
            _registry.Remove(obj.ObjectId);
        }

        /// <summary>
        /// Get a registered object by ID.
        /// </summary>
        public NetSyncObject GetObject(ushort objectId)
        {
            _registry.TryGetValue(objectId, out var obj);
            return obj;
        }

        /// <summary>
        /// Collect locally-owned dirty objects, serialize, and send to server.
        /// Called from NetSyncManager.Update() at the configured send rate.
        /// </summary>
        public SendOutcome SendOwnedObjects(string roomId, int localClientNo)
        {
            if (localClientNo <= 0) return SendOutcome.Sent();

            try
            {
                _dirtyEntries.Clear();
                foreach (var kv in _registry)
                {
                    var obj = kv.Value;
                    if (obj == null) continue;
                    if (obj.OwnerClientNo != localClientNo) continue;

                    var t = obj.SyncSpace == NetSyncTransformApplier.SpaceMode.World
                        ? obj.transform.position
                        : obj.transform.localPosition;
                    var r = obj.SyncSpace == NetSyncTransformApplier.SpaceMode.World
                        ? obj.transform.rotation
                        : obj.transform.localRotation;

                    _dirtyEntries.Add(new ObjectTransformEntry(obj.ObjectId, (ushort)localClientNo, t, r));
                }

                if (_dirtyEntries.Count == 0) return SendOutcome.Sent();

                // Only-on-change filtering with heartbeat
                var signature = BinarySerializer.ComputeObjectPoseSignature(_dirtyEntries);
                var now = Time.time;
                if (_hasLastSignature &&
                    signature == _lastSignature &&
                    now - _lastSentTime < HEARTBEAT_INTERVAL_SECONDS)
                {
                    return SendOutcome.Sent();
                }

                _poseSeq++;

                // Serialize
                var required = HEADER_SIZE + _dirtyEntries.Count * BYTES_PER_OBJECT;
                _buf.EnsureCapacity(required);
                _buf.Stream.Position = 0;

                BinarySerializer.SerializeClientObjectsInto(_buf.Writer, _poseSeq, _dirtyEntries);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;
                var payload = new byte[length];
                Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);

                _connectionManager.SetLatestObjectTransform(roomId, payload);
                _hasLastSignature = true;
                _lastSignature = signature;
                _lastSentTime = now;
                _messagesSent++;
                return SendOutcome.Sent();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ObjectSyncManager] SendOwnedObjects: {ex.Message}");
                return SendOutcome.Fatal(ex.Message);
            }
        }

        /// <summary>
        /// Process incoming room object data from server.
        /// Routes transforms to matching NetSyncObject instances.
        /// </summary>
        public void ProcessRoomObjects(RoomObjectData roomData)
        {
            if (roomData == null || roomData.objects == null) return;

            for (int i = 0; i < roomData.objects.Count; i++)
            {
                var entry = roomData.objects[i];
                if (!_registry.TryGetValue(entry.objectId, out var obj)) continue;
                if (obj == null) continue;

                // Update ownership from server broadcast
                obj.ApplyOwnershipFromServer(entry.ownerClientNo);

                // Only apply transform for remote objects (not locally owned)
                if (!obj.IsLocallyOwned)
                {
                    obj.ApplyRemoteTransform(roomData.broadcastTime, entry.position, entry.rotation);
                }
            }
        }

        /// <summary>
        /// Process incoming ownership change from server.
        /// </summary>
        public void ProcessOwnershipChange(OwnershipChangeData data)
        {
            if (!_registry.TryGetValue(data.objectId, out var obj)) return;
            if (obj == null) return;
            obj.ApplyOwnershipChange(data.newOwnerClientNo, data.seq);
        }

        /// <summary>
        /// Send an ownership change request to the server via control queue.
        /// </summary>
        public bool SendOwnershipChange(string roomId, ushort objectId, ushort newOwnerClientNo, ushort seq)
        {
            var payload = BinarySerializer.SerializeOwnershipChange(objectId, newOwnerClientNo, seq);
            return _connectionManager.TryEnqueueControl(roomId, payload);
        }

        /// <summary>
        /// Release all objects owned by a disconnected client.
        /// Called by the host (lowest alive clientNo) when a peer disconnects.
        /// </summary>
        public void HandleClientDisconnect(int disconnectedClientNo, string roomId, int localClientNo, int[] aliveClients)
        {
            if (aliveClients == null || aliveClients.Length == 0) return;

            // Only the host (lowest alive clientNo) performs cleanup
            int hostClientNo = aliveClients[0];
            for (int i = 1; i < aliveClients.Length; i++)
            {
                if (aliveClients[i] < hostClientNo)
                    hostClientNo = aliveClients[i];
            }
            if (localClientNo != hostClientNo) return;

            foreach (var kv in _registry)
            {
                var obj = kv.Value;
                if (obj == null) continue;
                if (obj.OwnerClientNo == disconnectedClientNo)
                {
                    var newSeq = (ushort)(obj.OwnershipSeq + 1);
                    obj.ApplyOwnershipChange(0, newSeq);
                    SendOwnershipChange(roomId, obj.ObjectId, 0, newSeq);
                }
            }
        }

        public void Dispose()
        {
            _buf?.Dispose();
        }
    }
}
