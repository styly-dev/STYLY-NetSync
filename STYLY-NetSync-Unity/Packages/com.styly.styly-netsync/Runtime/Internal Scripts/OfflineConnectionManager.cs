// OfflineConnectionManager.cs - No-op IConnectionManager for offline testing without a server
using System;

namespace Styly.NetSync
{
    internal class OfflineConnectionManager : IConnectionManager
    {
        private bool _isConnected;

        // Connection state
        public bool IsConnected => _isConnected;
        public bool IsConnectionError => false;
        public Exception LastException => null;
        public long LastExceptionAtUnixMs => 0;

        // Events
        public event Action<string> OnConnectionError;
        public event Action OnConnectionEstablished;

        public void Connect(string serverAddress, int dealerPort, int subPort, string roomId)
        {
            if (_isConnected) return;
            _isConnected = true;
            OnConnectionEstablished?.Invoke();
        }

        public void Disconnect()
        {
            _isConnected = false;
        }

        public void ClearConnectionError() { }
        public bool TryEnqueueControl(string roomId, byte[] payload) => true;
        public void SetLatestTransform(string roomId, byte[] payload) { }
        public void StartDiscovery(ServerDiscoveryManager discoveryManager, string roomId) { }
        public void ProcessDiscoveredServer(string serverAddress, int dealerPort, int subPort) { }
    }
}
