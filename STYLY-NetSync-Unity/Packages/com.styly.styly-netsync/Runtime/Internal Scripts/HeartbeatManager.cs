// HeartbeatManager.cs - Sends periodic heartbeat messages
using System;
using NetMQ;

namespace Styly.NetSync
{
    internal class HeartbeatManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private readonly ReusableBufferWriter _buf;
        private float _lastSendTime;

        public float IntervalSeconds { get; set; }

        private const int InitialBufferCapacity = 128;

        public HeartbeatManager(ConnectionManager connectionManager, string deviceId, float intervalSeconds)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            IntervalSeconds = intervalSeconds;
            _buf = new ReusableBufferWriter(InitialBufferCapacity);
        }

        public void Tick(float currentTime, string roomId, int clientNo)
        {
            if (_connectionManager.DealerSocket == null)
            {
                return;
            }

            if (currentTime - _lastSendTime < IntervalSeconds)
            {
                return;
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            _buf.Stream.Position = 0;
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
                _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                _lastSendTime = currentTime;
            }
            finally
            {
                msg.Clear();
            }
        }

        public void Dispose()
        {
            _buf.Dispose();
        }
    }
}
