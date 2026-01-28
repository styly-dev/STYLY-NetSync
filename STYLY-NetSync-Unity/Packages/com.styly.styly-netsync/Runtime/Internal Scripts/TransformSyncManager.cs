// TransformSyncManager.cs - Handles transform synchronization
// Note: This class now reuses MemoryStream/BinaryWriter/NetMQMessage and a pooled byte[] buffer
// to reduce per-send allocations and GC pressure.
using System;
using System.Buffers;
using System.IO;
using System.Text;
using UnityEngine;

namespace Styly.NetSync
{
    internal class TransformSyncManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private int _messagesSent;
        private float _lastSendTime;
        private ushort _poseSeq;

        // Reusable pooled buffer + stream + writer
        private readonly ReusableBufferWriter _buf;

        // Keep in sync with BinarySerializer's internal maximum
        private const int MAX_VIRTUAL_TRANSFORMS = 50;
        // Initial buffer size chosen to cover typical payloads without resizing
        private const int INITIAL_BUFFER_CAPACITY = 2048;

        public float SendRate { get; set; } = 10f;
        public int MessagesSent => _messagesSent;

        public TransformSyncManager(ConnectionManager connectionManager, string deviceId, float sendRate)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            SendRate = sendRate;

            // Allocate reusable pooled buffer for serialization
            _buf = new ReusableBufferWriter(INITIAL_BUFFER_CAPACITY);
        }

        public SendOutcome SendLocalTransform(NetSyncAvatar localAvatar, string roomId)
        {
            if (localAvatar == null)
                return SendOutcome.Fatal("localAvatar is null");

            try
            {
                var tx = localAvatar.GetTransformData();
                _poseSeq++;
                tx.poseSeq = _poseSeq;

                // Ensure buffer is large enough for the upcoming payload
                var required = EstimateClientTransformSize(tx);
                _buf.EnsureCapacity(required);

                // Reset writer/stream for reuse
                _buf.Stream.Position = 0;
                // Write directly into the pooled stream
                BinarySerializer.SerializeClientTransformInto(_buf.Writer, tx);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;

                // Copy the exact payload length into a fresh array (avoid sharing pooled buffer).
                var payload = new byte[length];
                Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);

                // Phase 2: Use SetLatestTransform for latest-wins semantics
                // This always succeeds (overwrites previous) - actual send is async in network thread
                _connectionManager.SetLatestTransform(roomId, payload);
                _messagesSent++;
                return SendOutcome.Sent();
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendLocalTransform: {ex.Message}");
                return SendOutcome.Fatal(ex.Message);
            }
        }

        internal SendOutcome SendStealthHandshake(string roomId)
        {
            try
            {
                // Estimate size for handshake and ensure capacity
                var required = EstimateStealthHandshakeSize(_deviceId);
                _buf.EnsureCapacity(required);

                _buf.Stream.Position = 0;
                BinarySerializer.SerializeStealthHandshakeInto(_buf.Writer, _deviceId);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;

                var payload = new byte[length];
                Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                var ok = _connectionManager.EnqueueReliableSend(roomId, payload);
                if (ok)
                {
                    _messagesSent++;
                    return SendOutcome.Sent();
                }
                // EnqueueReliableSend returns false when queue full - this is backpressure
                return SendOutcome.Backpressure();
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendStealthHandshake: {ex.Message}");
                return SendOutcome.Fatal(ex.Message);
            }
        }

        public void IncrementMessagesSent()
        {
            _messagesSent++;
        }

        public bool ShouldSendTransform(float currentTime)
        {
            return currentTime - _lastSendTime >= 1f / SendRate;
        }

        public void UpdateLastSendTime(float currentTime)
        {
            _lastSendTime = currentTime;
        }

        // Buffer growth handled by ReusableBufferWriter

        /// <summary>
        /// Estimate byte size for client transform payload to pre-size buffers.
        /// </summary>
        private static int EstimateClientTransformSize(ClientTransformData data)
        {
            // 1 (type) + 1 (protocol) + 1 (deviceIdLen) + deviceIdBytes + 2 (poseSeq) + 1 (flags)
            // + 4x TransformData (7 floats each) + 1 (virtual count) + N * 28
            var deviceIdBytes = data != null ? Encoding.UTF8.GetByteCount(data.deviceId ?? string.Empty) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255; // Length prefix is 1 byte

            var baseSize = 1 + 1 + 1 + deviceIdBytes + 2 + 1 + (4 * 7 * sizeof(float)) + 1;
            var virtualCount = 0;
            if (data != null && data.virtuals != null)
            {
                virtualCount = data.virtuals.Count;
                if (virtualCount > MAX_VIRTUAL_TRANSFORMS) virtualCount = MAX_VIRTUAL_TRANSFORMS;
            }
            var virtualSize = virtualCount * (7 * sizeof(float));
            return baseSize + virtualSize;
        }

        /// <summary>
        /// Estimate byte size for stealth handshake payload to pre-size buffers.
        /// </summary>
        private static int EstimateStealthHandshakeSize(string deviceId)
        {
            var deviceIdBytes = deviceId != null ? Encoding.UTF8.GetByteCount(deviceId) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255; // Length prefix is 1 byte
            // 1 (type) + 1 (protocol) + 1 (deviceIdLen) + deviceId + 2 (poseSeq) + 1 (flags)
            // + 4 * 7 floats + 1 (virtual count 0)
            return 1 + 1 + 1 + deviceIdBytes + 2 + 1 + (4 * 7 * sizeof(float)) + 1;
        }

        /// <summary>
        /// Dispose pooled buffer resources.
        /// </summary>
        public void Dispose()
        {
            _buf.Dispose();
        }
    }
}
