// TransformSyncManager.cs - Handles transform synchronization
// Note: This class now reuses MemoryStream/BinaryWriter/NetMQMessage and a pooled byte[] buffer
// to reduce per-send allocations and GC pressure.
using System;
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
        private bool _hasLastPoseSignature;
        private ulong _lastPoseSignature;
        private float _lastPoseSentTime;

        // Reusable pooled buffer + stream + writer
        private readonly ReusableBufferWriter _buf;

        // Keep in sync with BinarySerializer's internal maximum
        private const int MAX_VIRTUAL_TRANSFORMS = 50;
        // Initial buffer size chosen to cover typical payloads without resizing
        private const int INITIAL_BUFFER_CAPACITY = 2048;
        private const float HEARTBEAT_INTERVAL_SECONDS = 1f;
        private const int ABS_POS_BYTES_PER_AXIS = 3;

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
                var poseSignature = BinarySerializer.ComputePoseSignature(tx);
                var now = Time.time;

                // Only send on change or heartbeat to reduce traffic while preserving liveness.
                if (_hasLastPoseSignature &&
                    poseSignature == _lastPoseSignature &&
                    now - _lastPoseSentTime < HEARTBEAT_INTERVAL_SECONDS)
                {
                    return SendOutcome.Sent();
                }

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

                // Use SetLatestTransform for latest-wins semantics
                // This always succeeds (overwrites previous) - actual send is async in network thread
                _connectionManager.SetLatestTransform(roomId, payload);
                _hasLastPoseSignature = true;
                _lastPoseSignature = poseSignature;
                _lastPoseSentTime = now;
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
                var ok = _connectionManager.TryEnqueueControl(roomId, payload);
                if (ok)
                {
                    _messagesSent++;
                    return SendOutcome.Sent();
                }
                // TryEnqueueControl returns false when queue full - this is backpressure
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
            // 1(type) + 1(protocol) + 1(deviceLen) + deviceId + 2(poseSeq) + 1(flags) + 1(encodingFlags) + 1(virtualCount)
            var deviceIdBytes = data != null ? Encoding.UTF8.GetByteCount(data.deviceId ?? string.Empty) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255; // Length prefix is 1 byte

            var baseSize = 1 + 1 + 1 + deviceIdBytes + 2 + 1 + 1 + 1;
            var bodySize = 0;

            var flags = data != null ? data.flags : PoseFlags.None;
            bool physicalValid = (flags & PoseFlags.PhysicalValid) != 0;
            bool headValid = (flags & PoseFlags.HeadValid) != 0;
            bool rightValid = headValid && ((flags & PoseFlags.RightValid) != 0);
            bool leftValid = headValid && ((flags & PoseFlags.LeftValid) != 0);
            bool virtualValid = headValid && ((flags & PoseFlags.VirtualsValid) != 0);

            if (physicalValid)
            {
                bodySize += (3 * ABS_POS_BYTES_PER_AXIS) + sizeof(short); // pos i24x3 + yaw i16
            }

            if (headValid)
            {
                bodySize += (3 * ABS_POS_BYTES_PER_AXIS) + sizeof(uint); // pos i24x3 + compressed rot
            }

            if (rightValid)
            {
                bodySize += (3 * sizeof(short)) + sizeof(uint); // relative pos + relative rot
            }

            if (leftValid)
            {
                bodySize += (3 * sizeof(short)) + sizeof(uint); // relative pos + relative rot
            }

            var virtualCount = 0;
            if (virtualValid && data != null && data.virtuals != null)
            {
                virtualCount = data.virtuals.Count;
                if (virtualCount > MAX_VIRTUAL_TRANSFORMS) virtualCount = MAX_VIRTUAL_TRANSFORMS;
            }
            var virtualSize = virtualCount * ((3 * sizeof(short)) + sizeof(uint)); // relative pos + relative rot
            return baseSize + bodySize + virtualSize;
        }

        /// <summary>
        /// Estimate byte size for stealth handshake payload to pre-size buffers.
        /// </summary>
        private static int EstimateStealthHandshakeSize(string deviceId)
        {
            var deviceIdBytes = deviceId != null ? Encoding.UTF8.GetByteCount(deviceId) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255; // Length prefix is 1 byte
            // 1(type) + 1(protocol) + 1(deviceIdLen) + deviceId + 2(poseSeq) + 1(flags) + 1(encodingFlags) + 1(virtualCount)
            return 1 + 1 + 1 + deviceIdBytes + 2 + 1 + 1 + 1;
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
