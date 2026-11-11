// FakeConnectionManager.cs - Test double for IConnectionManager.
// Records outbound payloads and lets tests script send results and raise events.
// Modeled on OfflineConnectionManager (the production no-op implementation).
using System;
using System.Collections.Generic;

namespace Styly.NetSync.Tests
{
    internal sealed class FakeConnectionManager : IConnectionManager
    {
        public readonly List<(string roomId, byte[] payload)> ControlSends = new();
        public readonly List<(string roomId, byte[] payload)> TransformSends = new();
        public readonly List<(string roomId, uint objectId, byte[] payload)> ObjectTransformSends = new();

        /// <summary>Return value for TryEnqueueControl; set false to simulate backpressure.</summary>
        public bool ControlEnqueueResult = true;

        public bool IsConnected { get; set; } = true;
        public bool IsConnectionError { get; private set; }
        public Exception LastException { get; private set; }
        public long LastExceptionAtUnixMs { get; private set; }

        public int DrainMainThreadActionsCallCount { get; private set; }

        public event Action<string> OnConnectionError;
        public event Action OnConnectionEstablished;

        public void Connect(string serverAddress, int controlPort, int transformPort, int subPort, string roomId) { }
        public void Disconnect() { }

        public void ClearConnectionError() => IsConnectionError = false;

        public void DrainMainThreadActions() => DrainMainThreadActionsCallCount++;

        public bool TryEnqueueControl(string roomId, byte[] payload)
        {
            if (ControlEnqueueResult)
            {
                ControlSends.Add((roomId, payload));
            }
            return ControlEnqueueResult;
        }

        public void SetLatestTransform(string roomId, byte[] payload)
        {
            TransformSends.Add((roomId, payload));
        }

        public void SetLatestObjectTransform(string roomId, uint objectId, byte[] payload)
        {
            ObjectTransformSends.Add((roomId, objectId, payload));
        }

        public void StartDiscovery(ServerDiscoveryManager discoveryManager, string roomId) { }
        public void ProcessDiscoveredServer(string serverAddress, int controlPort, int transformPort, int subPort) { }

        // Test helpers to drive events
        public void RaiseConnectionError(string message)
        {
            IsConnectionError = true;
            LastException = new Exception(message);
            LastExceptionAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            OnConnectionError?.Invoke(message);
        }

        public void RaiseConnectionEstablished() => OnConnectionEstablished?.Invoke();
    }
}
