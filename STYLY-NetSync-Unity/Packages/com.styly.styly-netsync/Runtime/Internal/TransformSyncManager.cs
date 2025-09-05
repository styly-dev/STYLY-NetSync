// TransformSyncManager.cs - Handles transform synchronization
using System;
using NetMQ;
using UnityEngine;
using System.IO;

namespace Styly.NetSync
{
    internal class TransformSyncManager
    {
        private readonly ConnectionManager _connectionManager;
        private readonly string _deviceId;
        private int _messagesSent;
        private float _lastSendTime;

        private readonly MemoryStream _sendStream = new MemoryStream(1024);
        private readonly BinaryWriter _writer;
        private readonly NetMQMessage _reusableMsg = new NetMQMessage();

        public float SendRate { get; set; } = 10f;
        public int MessagesSent => _messagesSent;

        public TransformSyncManager(ConnectionManager connectionManager, string deviceId, float sendRate)
        {
            _connectionManager = connectionManager;
            _deviceId = deviceId;
            SendRate = sendRate;
            _writer = new BinaryWriter(_sendStream, System.Text.Encoding.UTF8, true);
        }

        public bool SendLocalTransform(NetSyncAvatar localAvatar, string roomId)
        {
            if (localAvatar == null || _connectionManager.DealerSocket == null)
                return false;

            try
            {
                var tx = localAvatar.GetTransformData();

                _sendStream.Position = 0;
                _sendStream.SetLength(0);
                BinarySerializer.SerializeClientTransformInto(_writer, tx);
                _writer.Flush();

                _reusableMsg.Clear();
                _reusableMsg.Append(roomId);
                var buf = _sendStream.GetBuffer();
                _reusableMsg.Append(buf, 0, (int)_sendStream.Length);

                var ok = _connectionManager.DealerSocket.TrySendMultipartMessage(_reusableMsg);
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
                _sendStream.Position = 0;
                _sendStream.SetLength(0);
                BinarySerializer.SerializeStealthHandshakeInto(_writer, _deviceId);
                _writer.Flush();

                _reusableMsg.Clear();
                _reusableMsg.Append(roomId);
                var buf = _sendStream.GetBuffer();
                _reusableMsg.Append(buf, 0, (int)_sendStream.Length);

                var ok = _connectionManager.DealerSocket.TrySendMultipartMessage(_reusableMsg);
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
    }
}