// HeartbeatManager.cs - Handles heartbeat messages for liveness
using System;
using System.IO;

namespace Styly.NetSync
{
    internal class HeartbeatManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private readonly double _intervalSeconds;
        private double _lastSentAt;

        private readonly ReusableBufferWriter _buf;
        private const int InitialBufferCapacity = 128;

        public HeartbeatManager(ConnectionManager connectionManager, string deviceId, double intervalSeconds)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            _intervalSeconds = Math.Max(0.1, intervalSeconds);
            _buf = new ReusableBufferWriter(InitialBufferCapacity);
        }

        public void Tick(string roomId, int clientNo)
        {
            if (_connectionManager == null || !_connectionManager.IsConnected)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (now - _lastSentAt < _intervalSeconds)
            {
                return;
            }

            _lastSentAt = now;

            var required = 1 + 1 + (_deviceId?.Length ?? 0) + 2 + 8;
            _buf.EnsureCapacity(required);
            _buf.Stream.Position = 0;

            BinarySerializer.SerializeHeartbeatInto(_buf.Writer, _deviceId, (ushort)Math.Max(0, clientNo), now);
            _buf.Writer.Flush();

            var length = (int)_buf.Stream.Position;
            var payload = new byte[length];
            Buffer.BlockCopy(_buf.GetBufferUnsafe(), 0, payload, 0, length);
            _connectionManager.TrySendDealerMessage(roomId, payload);
        }

        public void Dispose()
        {
            _buf.Dispose();
        }
    }
}
