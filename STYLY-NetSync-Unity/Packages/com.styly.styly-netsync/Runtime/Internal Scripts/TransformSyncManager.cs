// TransformSyncManager.cs - Handles transform synchronization
// Note: This class now reuses MemoryStream/BinaryWriter/NetMQMessage and a pooled byte[] buffer
// to reduce per-send allocations and GC pressure.
using System;
using System.Buffers;
using System.IO;
using System.Text;
using NetMQ;
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

        public bool SendLocalTransform(NetSyncAvatar localAvatar, string roomId)
        {
            if (localAvatar == null || _connectionManager.DealerSocket == null)
                return false;

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

                // Build a fresh message per send to ensure proper frame lifetime.
                var msg = new NetMQMessage();
                try
                {
                    msg.Append(roomId);
                    // Copy the exact payload length into a fresh array owned by NetMQ (avoid sharing pooled buffer).
                    var payload = new byte[length];
                    Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                    msg.Append(payload);

                    var ok = _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                    if (ok) _messagesSent++;
                    return ok;
                }
                finally
                {
                    // NetMQMessage is not IDisposable; clear to release frames promptly.
                    msg.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendLocalTransform: {ex.Message}");
                return false;
            }
        }

        internal bool SendStealthHandshake(string roomId)
        {
            if (_connectionManager.DealerSocket == null)
                return false;

            try
            {
                // Estimate size for handshake and ensure capacity
                var required = EstimateStealthHandshakeSize(_deviceId);
                _buf.EnsureCapacity(required);

                _buf.Stream.Position = 0;
                BinarySerializer.SerializeStealthHandshakeInto(_buf.Writer, _deviceId);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;

                var msg = new NetMQMessage();
                try
                {
                    msg.Append(roomId);
                    var payload = new byte[length];
                    Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                    msg.Append(payload);

                    var ok = _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                    if (ok) _messagesSent++;
                    return ok;
                }
                finally
                {
                    msg.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendStealthHandshake: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send a heartbeat message to keep the client alive on the server.
        /// This is independent of transform flow and prevents false timeouts
        /// under bandwidth pressure.
        /// </summary>
        internal bool SendHeartbeat(string roomId, int clientNo)
        {
            if (_connectionManager.DealerSocket == null)
                return false;

            try
            {
                // Heartbeat size: 1 (type) + 1 (deviceIdLen) + deviceId + 2 (clientNo) + 8 (timestamp)
                var required = EstimateHeartbeatSize(_deviceId);
                _buf.EnsureCapacity(required);

                _buf.Stream.Position = 0;
                var timestamp = NetSyncClock.NowSeconds();
                BinarySerializer.SerializeHeartbeatInto(_buf.Writer, _deviceId, clientNo, timestamp);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;

                var msg = new NetMQMessage();
                try
                {
                    msg.Append(roomId);
                    var payload = new byte[length];
                    Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                    msg.Append(payload);

                    return _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                }
                finally
                {
                    msg.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendHeartbeat: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Estimate byte size for heartbeat payload.
        /// </summary>
        private static int EstimateHeartbeatSize(string deviceId)
        {
            var deviceIdBytes = deviceId != null ? Encoding.UTF8.GetByteCount(deviceId) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255;
            // 1 (type) + 1 (deviceIdLen) + deviceId + 2 (clientNo) + 8 (timestamp)
            return 1 + 1 + deviceIdBytes + 2 + 8;
        }

        /// <summary>
        /// Send an RPC acknowledgment to the server for reliable RPC delivery.
        /// </summary>
        internal bool SendRpcAck(string roomId, ulong rpcId, string deviceId)
        {
            if (_connectionManager.DealerSocket == null)
                return false;

            try
            {
                // RPC ACK size: 1 (type) + 8 (rpcId) + 1 (deviceIdLen) + deviceId + 8 (timestamp)
                var required = EstimateRpcAckSize(deviceId);
                _buf.EnsureCapacity(required);

                _buf.Stream.Position = 0;
                var timestamp = NetSyncClock.NowSeconds();
                BinarySerializer.SerializeRPCAckInto(_buf.Writer, rpcId, deviceId, timestamp);
                _buf.Writer.Flush();

                var length = (int)_buf.Stream.Position;

                var msg = new NetMQMessage();
                try
                {
                    msg.Append(roomId);
                    var payload = new byte[length];
                    Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
                    msg.Append(payload);

                    return _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                }
                finally
                {
                    msg.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendRpcAck: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Estimate byte size for RPC ACK payload.
        /// </summary>
        private static int EstimateRpcAckSize(string deviceId)
        {
            var deviceIdBytes = deviceId != null ? Encoding.UTF8.GetByteCount(deviceId) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255;
            // 1 (type) + 8 (rpcId) + 1 (deviceIdLen) + deviceId + 8 (timestamp)
            return 1 + 8 + 1 + deviceIdBytes + 8;
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
