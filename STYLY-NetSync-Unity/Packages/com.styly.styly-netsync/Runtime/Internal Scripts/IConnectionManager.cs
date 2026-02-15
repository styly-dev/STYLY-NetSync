// IConnectionManager.cs - Interface for network connection management
using System;

namespace Styly.NetSync
{
    internal interface IConnectionManager
    {
        // Connection lifecycle
        void Connect(string serverAddress, int dealerPort, int subPort, string roomId);
        void Disconnect();

        // Connection state
        bool IsConnected { get; }
        bool IsConnectionError { get; }
        void ClearConnectionError();

        // Exception diagnostics
        Exception LastException { get; }
        long LastExceptionAtUnixMs { get; }

        // Sending
        bool TryEnqueueControl(string roomId, byte[] payload);
        void SetLatestTransform(string roomId, byte[] payload);

        // Discovery
        void StartDiscovery(ServerDiscoveryManager discoveryManager, string roomId);
        void ProcessDiscoveredServer(string serverAddress, int dealerPort, int subPort);

        // Events
        event Action<string> OnConnectionError;
        event Action OnConnectionEstablished;
    }
}
