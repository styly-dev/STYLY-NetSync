// ObjectSyncManager.cs - Manages NetSyncObject transform sync and ownership
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Styly.NetSync
{
    internal class ObjectSyncManager
    {
        private readonly IConnectionManager _connectionManager;
        private readonly MessageProcessor _messageProcessor;
        private readonly NetSyncTimeEstimator _timeEstimator;
        private readonly string _deviceId;
        private readonly bool _enableDebugLogs;

        private readonly Dictionary<uint, NetSyncObject> _registeredObjects = new();
        private readonly ReusableBufferWriter _buf = new ReusableBufferWriter(256);

        // Per-object send state
        private readonly Dictionary<uint, ObjectSendState> _sendStates = new();

        private const float HEARTBEAT_INTERVAL_SECONDS = 1f;

        private struct ObjectSendState
        {
            public float LastSendTime;
            public ushort PoseSeq;
            public Vector3 LastPosition;
            public Quaternion LastRotation;
            public bool HasLastPose;
        }

        public ObjectSyncManager(
            IConnectionManager connectionManager,
            MessageProcessor messageProcessor,
            NetSyncTimeEstimator timeEstimator,
            string deviceId,
            bool enableDebugLogs)
        {
            _connectionManager = connectionManager;
            _messageProcessor = messageProcessor;
            _timeEstimator = timeEstimator;
            _deviceId = deviceId;
            _enableDebugLogs = enableDebugLogs;

            _messageProcessor.OnRoomObjectsReceived += HandleRoomObjects;
            _messageProcessor.OnOwnershipChanged += HandleOwnershipChanged;
            _messageProcessor.OnOwnershipRejected += HandleOwnershipRejected;
        }

        public void Register(NetSyncObject obj)
        {
            if (obj == null) return;
            if (obj.ObjectId == 0u)
            {
                // Setup mistake, not runtime noise — always surface it so the user
                // notices the NetSyncObject will not participate in sync.
                Debug.LogWarning($"[ObjectSyncManager] NetSyncObject on '{obj.name}' has no ObjectId assigned; skipping registration.", obj);
                return;
            }
            _registeredObjects[obj.ObjectId] = obj;
        }

        public void Unregister(NetSyncObject obj)
        {
            if (obj == null || obj.ObjectId == 0u) return;
            _registeredObjects.Remove(obj.ObjectId);
            _sendStates.Remove(obj.ObjectId);
        }

        public void SendOwnershipRequest(string roomId, byte operationType, uint objectId)
        {
            if (objectId == 0u) return;
            _buf.EnsureCapacity(128);
            _buf.Stream.Position = 0;
            BinarySerializer.SerializeObjectOwnershipRequestInto(_buf.Writer, _deviceId, operationType, objectId);
            _buf.Writer.Flush();

            var length = (int)_buf.Stream.Position;
            var payload = new byte[length];
            Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
            _connectionManager.TryEnqueueControl(roomId, payload);
        }

        public void Tick(string roomId, float currentTime, int localClientNo, string localDeviceId, float transformSendRate)
        {
            float sendInterval = 1f / Mathf.Max(0.5f, transformSendRate);

            // Send owned object transforms
            foreach (var kvp in _registeredObjects)
            {
                var objectId = kvp.Key;
                var obj = kvp.Value;
                if (obj == null) continue;
                if (obj.OwnerClientNo != localClientNo || localClientNo == 0) continue;

                // Rate limiting
                if (!_sendStates.TryGetValue(objectId, out var state))
                {
                    state = new ObjectSendState();
                }

                if (currentTime - state.LastSendTime < sendInterval) continue;

                var t = obj.transform;
                var pos = t.position;
                var rot = t.rotation;

                // Only-on-change check (with heartbeat)
                if (state.HasLastPose &&
                    pos == state.LastPosition &&
                    rot == state.LastRotation &&
                    currentTime - state.LastSendTime < HEARTBEAT_INTERVAL_SECONDS)
                {
                    continue;
                }

                state.PoseSeq++;
                state.LastSendTime = currentTime;
                state.LastPosition = pos;
                state.LastRotation = rot;
                state.HasLastPose = true;
                _sendStates[objectId] = state;

                // Serialize and send
                _buf.EnsureCapacity(128);
                _buf.Stream.Position = 0;
                BinarySerializer.SerializeObjectPoseInto(_buf.Writer, localDeviceId, objectId, state.PoseSeq, pos, rot);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;
                var payload = new byte[length];
                Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                // Per-objectId latest-wins: avoids clobbering avatar slot and
                // other owned objects' slots.
                _connectionManager.SetLatestObjectTransform(roomId, objectId, payload);
            }
        }

        private void HandleRoomObjects(RoomObjectsData data)
        {
            if (data == null || data.objects == null) return;

            foreach (var objState in data.objects)
            {
                if (!_registeredObjects.TryGetValue(objState.objectId, out var obj)) continue;
                if (obj == null) continue;

                // Update ownership info. Fire OnOwnershipChanged when the value
                // actually changes so listeners (e.g. GrabbableNetSyncObject)
                // see scene-load ownership and any room-broadcast transitions,
                // not just out-of-band OwnershipChanged control messages.
                int previousOwner = obj.OwnerClientNo;
                int newOwner = objState.ownerClientNo;
                obj.SetOwnerClientNoInternal(newOwner);
                if (previousOwner != newOwner)
                {
                    obj.InvokeOwnershipChanged(newOwner, previousOwner);
                }

                // Apply transform for non-owned objects
                if (!obj.IsOwnedByMe)
                {
                    var applier = obj.TransformApplier;
                    if (applier != null)
                    {
                        applier.AddSingleSnapshot(objState.poseTime, objState.poseSeq, objState.position, objState.rotation);
                    }
                }
            }
        }

        private void HandleOwnershipChanged(OwnershipChangedData data)
        {
            if (data == null) return;
            if (!_registeredObjects.TryGetValue(data.objectId, out var obj)) return;
            if (obj == null) return;

            obj.SetOwnerClientNoInternal(data.newOwnerClientNo);
            obj.InvokeOwnershipChanged(data.newOwnerClientNo, data.previousOwnerClientNo);

            if (_enableDebugLogs)
            {
                Debug.Log($"[ObjectSyncManager] Ownership changed: 0x{data.objectId:X8} {data.previousOwnerClientNo} → {data.newOwnerClientNo}");
            }
        }

        private void HandleOwnershipRejected(OwnershipRejectedData data)
        {
            if (data == null) return;
            if (_enableDebugLogs)
            {
                Debug.LogWarning($"[ObjectSyncManager] Ownership rejected: 0x{data.objectId:X8} (owner={data.currentOwnerClientNo}, reason={data.reasonCode})");
            }
        }

        public void TickTransformAppliers(float deltaTime, double localNow)
        {
            foreach (var kvp in _registeredObjects)
            {
                var obj = kvp.Value;
                if (obj == null) continue;
                if (obj.IsOwnedByMe) continue;
                // Skip unowned objects so local physics, not stale snapshots
                // from a prior owner, drives the transform.
                if (obj.OwnerClientNo == 0) continue;

                // Tick the applier even when the object is unowned so the final
                // snapshot from the previous owner (delivered via HandleRoomObjects)
                // actually gets applied — otherwise the object would freeze at its
                // pre-release position on every remote client.
                // However, skip while no snapshot has arrived yet: PoseChannel
                // samples an empty buffer as default(PoseSampleData), which would
                // teleport the transform to origin on the very first Tick.
                var applier = obj.TransformApplier;
                if (applier != null && applier.HasAnySnapshot)
                {
                    applier.Tick(deltaTime, localNow);
                }
            }
        }

        public void ClearRoomScopedState()
        {
            _sendStates.Clear();
            // Reset ownership on all registered objects
            foreach (var obj in _registeredObjects.Values)
            {
                if (obj != null)
                {
                    obj.SetOwnerClientNoInternal(0);
                }
            }
        }

        public void Dispose()
        {
            _messageProcessor.OnRoomObjectsReceived -= HandleRoomObjects;
            _messageProcessor.OnOwnershipChanged -= HandleOwnershipChanged;
            _messageProcessor.OnOwnershipRejected -= HandleOwnershipRejected;
            _buf.Dispose();
        }
    }
}
