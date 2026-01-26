using System;
using NetMQ;

namespace Styly.NetSync
{
    internal class HeartbeatManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private readonly double _intervalSeconds;
        private double _lastSentAt;

        public HeartbeatManager(ConnectionManager connectionManager, string deviceId, double intervalSeconds = 1.0)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            _intervalSeconds = Math.Max(0.1, intervalSeconds);
            _lastSentAt = 0.0;
        }

        public void Tick(string roomId, int clientNo)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (now - _lastSentAt < _intervalSeconds)
            {
                return;
            }

            var dealer = _connectionManager.DealerSocket;
            if (dealer == null)
            {
                return;
            }

            var payload = BinarySerializer.SerializeHeartbeat(_deviceId, clientNo, now);
            var msg = new NetMQMessage();
            msg.Append(roomId);
            msg.Append(payload);
            dealer.TrySendMultipartMessage(msg);
            msg.Clear();

            _lastSentAt = now;
        }
    }
}
