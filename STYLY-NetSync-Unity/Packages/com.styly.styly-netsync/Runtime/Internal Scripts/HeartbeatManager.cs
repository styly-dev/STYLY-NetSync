using System;
using NetMQ;
using UnityEngine;

namespace Styly.NetSync
{
    internal class HeartbeatManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly NetSyncManager _netSyncManager;
        private readonly string _deviceId;
        private readonly double _intervalSeconds;
        private double _nextSendTime;

        public HeartbeatManager(ConnectionManager connectionManager, NetSyncManager netSyncManager, string deviceId, double intervalSeconds)
        {
            _connectionManager = connectionManager;
            _netSyncManager = netSyncManager;
            _deviceId = deviceId;
            _intervalSeconds = intervalSeconds;
            _nextSendTime = 0.0;
        }

        public void Tick(string roomId)
        {
            if (_connectionManager.DealerSocket == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextSendTime)
            {
                return;
            }
            _nextSendTime = now + _intervalSeconds;

            var payload = BinarySerializer.SerializeHeartbeat(_deviceId, _netSyncManager.ClientNo, now);
            try
            {
                var msg = new NetMQMessage();
                try
                {
                    msg.Append(roomId);
                    msg.Append(payload);
                    _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                }
                finally
                {
                    msg.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetSync/Heartbeat] Failed to send heartbeat: {ex.Message}");
            }
        }
    }
}
