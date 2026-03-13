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
        private readonly bool _enableDebugLogs;

        private readonly Dictionary<string, NetSyncObject> _registeredObjects = new();
        private readonly ReusableBufferWriter _buf = new ReusableBufferWriter(256);

        // Per-object send state
        private readonly Dictionary<string, ObjectSendState> _sendStates = new();

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
            bool enableDebugLogs)
        {
            _connectionManager = connectionManager;
            _messageProcessor = messageProcessor;
            _timeEstimator = timeEstimator;
            _enableDebugLogs = enableDebugLogs;

            _messageProcessor.OnRoomObjectsReceived += HandleRoomObjects;
            _messageProcessor.OnOwnershipChanged += HandleOwnershipChanged;
            _messageProcessor.OnOwnershipRejected += HandleOwnershipRejected;
        }

        public void Register(NetSyncObject obj)
        {
            if (obj == null || string.IsNullOrEmpty(obj.ObjectId)) return;
            _registeredObjects[obj.ObjectId] = obj;
        }

        public void Unregister(NetSyncObject obj)
        {
            if (obj == null || string.IsNullOrEmpty(obj.ObjectId)) return;
            _registeredObjects.Remove(obj.ObjectId);
            _sendStates.Remove(obj.ObjectId);
        }

        public void SendOwnershipRequest(string roomId, byte operationType, string objectId)
        {
            _buf.EnsureCapacity(128);
            _buf.Stream.Position = 0;
            BinarySerializer.SerializeObjectOwnershipRequestInto(_buf.Writer, operationType, objectId);
            _buf.Writer.Flush();

            var length = (int)_buf.Stream.Position;
            var payload = new byte[length];
            Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
            _connectionManager.TryEnqueueControl(roomId, payload);
        }

        public void Tick(string roomId, float currentTime, int localClientNo)
        {
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

                float sendInterval = 1f / Mathf.Max(0.5f, obj.SendRate);
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
                BinarySerializer.SerializeObjectPoseInto(_buf.Writer, objectId, state.PoseSeq, pos, rot);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;
                var payload = new byte[length];
                Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                _connectionManager.SetLatestTransform(roomId, payload);
            }
        }

        private void HandleRoomObjects(RoomObjectsData data)
        {
            if (data == null || data.objects == null) return;

            foreach (var objState in data.objects)
            {
                if (!_registeredObjects.TryGetValue(objState.objectId, out var obj)) continue;
                if (obj == null) continue;

                // Update ownership info
                obj.SetOwnerClientNoInternal(objState.ownerClientNo);

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
                Debug.Log($"[ObjectSyncManager] Ownership changed: {data.objectId} {data.previousOwnerClientNo} → {data.newOwnerClientNo}");
            }
        }

        private void HandleOwnershipRejected(OwnershipRejectedData data)
        {
            if (data == null) return;
            if (_enableDebugLogs)
            {
                Debug.LogWarning($"[ObjectSyncManager] Ownership rejected: {data.objectId} (owner={data.currentOwnerClientNo}, reason={data.reasonCode})");
            }
        }

        public void TickTransformAppliers(float deltaTime, double localNow)
        {
            foreach (var kvp in _registeredObjects)
            {
                var obj = kvp.Value;
                if (obj == null) continue;
                if (obj.IsOwnedByMe) continue;
                if (!obj.HasOwner) continue;

                var applier = obj.TransformApplier;
                if (applier != null)
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
