// TransformSyncManager.cs - Handles transform synchronization
using System;
using NetMQ;
using UnityEngine;

namespace Styly.NetSync
{
    public class TransformSyncManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private int _messagesSent;
        private float _lastSendTime;
        
        public float SendRate { get; set; } = 10f;
        public int MessagesSent => _messagesSent;
        
        public TransformSyncManager(ConnectionManager connectionManager, string deviceId, float sendRate)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            SendRate = sendRate;
        }
        
        public bool SendLocalTransform(NetSyncAvatar localPlayerAvatar, string groupId)
        {
            if (localPlayerAvatar == null || _connectionManager.DealerSocket == null)
                return true;
            
            try
            {
                var tx = localPlayerAvatar.GetTransformData();
                var binaryData = BinarySerializer.SerializeClientTransform(tx);
                
                var msg = new NetMQMessage();
                msg.Append(groupId);
                msg.Append(binaryData);
                
                var ok = _connectionManager.DealerSocket.TrySendMultipartMessage(msg);
                if (ok) _messagesSent++;
                return ok;
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendLocalTransform: {ex.Message}");
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
    }
}