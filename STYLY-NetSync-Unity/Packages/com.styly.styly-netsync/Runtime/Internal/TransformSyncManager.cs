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

        // Pooled serialization resources
        private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
        private byte[] _buffer;
        private int _bufferCapacity;
        private MemoryStream _memoryStream;
        private BinaryWriter _writer;
        private NetMQMessage _reusableMessage;

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

            // Allocate and prepare reusable serialization resources
            _bufferCapacity = INITIAL_BUFFER_CAPACITY;
            _buffer = _bufferPool.Rent(_bufferCapacity);
            // MemoryStream over pooled buffer; publiclyVisible allows fast access via GetBuffer/TryGetBuffer
            _memoryStream = new MemoryStream(_buffer, 0, _buffer.Length, true, true);
            _writer = new BinaryWriter(_memoryStream);
            _reusableMessage = new NetMQMessage();
        }

        public bool SendLocalTransform(NetSyncAvatar localAvatar, string roomId)
        {
            if (localAvatar == null || _connectionManager.DealerSocket == null)
                return false;

            try
            {
                var tx = localAvatar.GetTransformData();

                // Ensure buffer is large enough for the upcoming payload
                var required = EstimateClientTransformSize(tx);
                EnsureBufferCapacity(required);

                // Reset writer/stream for reuse
                _memoryStream.Position = 0;
                // Write directly into the pooled stream
                BinarySerializer.SerializeClientTransformInto(_writer, tx);
                _writer.Flush();

                var length = (int)_memoryStream.Position;

                // Reuse NetMQMessage and append frames
                _reusableMessage.Clear();
                _reusableMessage.Append(roomId);
                // Copy the exact payload length into a fresh array owned by NetMQ.
                // This avoids sharing our pooled buffer with the socket internals.
                var payload = new byte[length];
                Buffer.BlockCopy(_buffer, 0, payload, 0, length);
                _reusableMessage.Append(payload);

                var ok = _connectionManager.DealerSocket.TrySendMultipartMessage(_reusableMessage);
                if (ok) _messagesSent++;
                return ok;
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
                EnsureBufferCapacity(required);

                _memoryStream.Position = 0;
                BinarySerializer.SerializeStealthHandshakeInto(_writer, _deviceId);
                _writer.Flush();

                var length = (int)_memoryStream.Position;

                _reusableMessage.Clear();
                _reusableMessage.Append(roomId);
                var payload = new byte[length];
                Buffer.BlockCopy(_buffer, 0, payload, 0, length);
                _reusableMessage.Append(payload);

                var ok = _connectionManager.DealerSocket.TrySendMultipartMessage(_reusableMessage);
                if (ok) _messagesSent++;
                return ok;
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendStealthHandshake: {ex.Message}");
                return false;
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

        /// <summary>
        /// Ensure the pooled buffer has at least the requested capacity.
        /// Resizes by renting a larger buffer and rebuilding the stream/writer if needed.
        /// </summary>
        private void EnsureBufferCapacity(int required)
        {
            if (required <= _bufferCapacity)
            {
                return;
            }

            // Rent a larger buffer (double strategy)
            var newCapacity = _bufferCapacity * 2;
            if (newCapacity < required) newCapacity = required;

            var newBuffer = _bufferPool.Rent(newCapacity);

            // Dispose old stream/writer and replace with new ones over the new buffer
            // Note: We do not copy old contents because we always reset before writing
            _writer?.Dispose();
            _memoryStream?.Dispose();

            _bufferPool.Return(_buffer);
            _buffer = newBuffer;
            _bufferCapacity = newCapacity;

            _memoryStream = new MemoryStream(_buffer, 0, _buffer.Length, true, true);
            _writer = new BinaryWriter(_memoryStream);
        }

        /// <summary>
        /// Estimate byte size for client transform payload to pre-size buffers.
        /// </summary>
        private static int EstimateClientTransformSize(ClientTransformData data)
        {
            // 1 (type) + 1 (deviceIdLen) + deviceIdBytes + 4x TransformData (6 floats each) + 1 (virtual count) + N * 24
            var deviceIdBytes = data != null ? Encoding.UTF8.GetByteCount(data.deviceId ?? string.Empty) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255; // Length prefix is 1 byte

            var baseSize = 1 + 1 + deviceIdBytes + (4 * 6 * sizeof(float)) + 1;
            var virtualCount = 0;
            if (data != null && data.virtuals != null)
            {
                virtualCount = data.virtuals.Count;
                if (virtualCount > MAX_VIRTUAL_TRANSFORMS) virtualCount = MAX_VIRTUAL_TRANSFORMS;
            }
            var virtualSize = virtualCount * (6 * sizeof(float));
            return baseSize + virtualSize;
        }

        /// <summary>
        /// Estimate byte size for stealth handshake payload to pre-size buffers.
        /// </summary>
        private static int EstimateStealthHandshakeSize(string deviceId)
        {
            var deviceIdBytes = deviceId != null ? Encoding.UTF8.GetByteCount(deviceId) : 0;
            if (deviceIdBytes > 255) deviceIdBytes = 255; // Length prefix is 1 byte
            // 1 (type) + 1 (deviceIdLen) + deviceId + 4 * 6 floats + 1 (virtual count 0)
            return 1 + 1 + deviceIdBytes + (4 * 6 * sizeof(float)) + 1;
        }
    }
}
