// ConnectionManager.cs - Handles network connection management
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NetMQ;
using NetMQ.Sockets;
using UnityEngine;

namespace Styly.NetSync
{
    internal class ConnectionManager
    {
        private DealerSocket _dealerSocket;
        private SubscriberSocket _subSocket;
        private Thread _receiveThread;
        private volatile bool _shouldStop;
        private bool _enableDebugLogs;
        private bool _logNetworkTraffic;
        private bool _connectionError;
        private float _reconnectDelay = 10f;
        private ServerDiscoveryManager _discoveryManager;
        private string _currentRoomId;

        public DealerSocket DealerSocket => _dealerSocket;
        public SubscriberSocket SubSocket => _subSocket;
        public bool IsConnected => _dealerSocket != null && _subSocket != null && !_connectionError;
        public bool IsConnectionError => _connectionError;

        public event Action<string> OnConnectionError;
        public event Action OnConnectionEstablished;

        private readonly NetSyncManager _netSyncManager;
        private readonly MessageProcessor _messageProcessor;

        public ConnectionManager(NetSyncManager netSyncManager, MessageProcessor messageProcessor, bool enableDebugLogs, bool logNetworkTraffic)
        {
            _netSyncManager = netSyncManager;
            _messageProcessor = messageProcessor;
            _enableDebugLogs = enableDebugLogs;
            _logNetworkTraffic = logNetworkTraffic;
        }

        public void Connect(string serverAddress, int dealerPort, int subPort, string roomId)
        {
            if (_receiveThread != null)
            {
                return; // Already connected
            }

            _currentRoomId = roomId;
            _connectionError = false;
            _shouldStop = false;
            _receiveThread = new Thread(() => NetworkLoop(serverAddress, dealerPort, subPort, roomId))
            {
                IsBackground = true,
                Name = "STYLY_NetworkThread"
            };
            _receiveThread.Start();
            DebugLog("Network thread started");
        }

        public void Disconnect()
        {
            if (_receiveThread == null) { return; }

            _shouldStop = true;

            // Dispose sockets
            if (_subSocket != null)
            {
                _subSocket.Dispose();
            }
            if (_dealerSocket != null)
            {
                _dealerSocket.Dispose();
            }

            _subSocket = null;
            _dealerSocket = null;

            // Wait for receive thread to exit
            WaitThreadExit(_receiveThread, 1000);
            _receiveThread = null;

            // OS-specific cleanup
            SafeNetMQCleanup();
        }

        private void NetworkLoop(string serverAddress, int dealerPort, int subPort, string roomId)
        {
            try
            {
                // Dealer (for sending)
                using var dealer = new DealerSocket();
                dealer.Options.Linger = TimeSpan.Zero;
                dealer.Options.SendHighWatermark = 10;
                dealer.Connect($"{serverAddress}:{dealerPort}");
                _dealerSocket = dealer;

                DebugLog($"[Thread] DEALER connected → {serverAddress}:{dealerPort}");

                // Subscriber (for receiving)
                using var sub = new SubscriberSocket();
                sub.Options.Linger = TimeSpan.Zero;
                sub.Options.ReceiveHighWatermark = 10;
                sub.Connect($"{serverAddress}:{subPort}");
                sub.Subscribe(roomId);
                _subSocket = sub;

                DebugLog($"[Thread] SUB connected    → {serverAddress}:{subPort}");

                // Notify connection established
                if (OnConnectionEstablished != null)
                {
                    OnConnectionEstablished.Invoke();
                }

                while (!_shouldStop)
                {
                    if (!sub.TryReceiveFrameString(TimeSpan.FromMilliseconds(10), out var topic)) { continue; }
                    if (!sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(10), out var payload)) { continue; }
                    if (topic != roomId) { continue; }

                    try
                    {
                        _messageProcessor.ProcessIncomingMessage(payload);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Binary parse error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_shouldStop)
                {
                    Debug.LogError($"Network thread error: {ex.Message}");
                    _connectionError = true;
                    if (OnConnectionError != null)
                    {
                        OnConnectionError.Invoke(ex.Message);
                    }
                }
            }
        }

        private static void WaitThreadExit(Thread t, int ms)
        {
#if UNITY_WEBGL
            return; // WebGL doesn't support threads
#else
            if (t == null) { return; }
            if (!t.Join(ms))
            {
#if !UNITY_IOS && !UNITY_TVOS && !UNITY_VISIONOS
                try { t.Interrupt(); } catch { /* IL2CPP Unsupported */ }
#endif
                t.Join();
            }
#endif
        }

        private static void SafeNetMQCleanup()
        {
#if UNITY_WEBGL
            return;
#endif
            // macOS: Cleanup tends to hang
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { return; }

            const int timeoutMs = 500;
            try
            {
                var cts = new CancellationTokenSource();
                var task = Task.Run(() => NetMQConfig.Cleanup(false), cts.Token);

                if (!task.Wait(timeoutMs))
                {
                    cts.Cancel();
                    Debug.LogWarning("[NetMQ] Cleanup timeout – skipped");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetMQ] Cleanup error: {ex.Message}");
            }
        }

        public void StartDiscovery(ServerDiscoveryManager discoveryManager, string roomId)
        {
            _discoveryManager = discoveryManager;
            _currentRoomId = roomId;
            if (_discoveryManager != null)
            {
                _discoveryManager.StartDiscovery();
                DebugLog("Server discovery started");
            }
        }

        public void ProcessDiscoveredServer(string serverAddress, int dealerPort, int subPort)
        {
            if (_discoveryManager != null)
            {
                _discoveryManager.StopDiscovery();
            }
            Connect(serverAddress, dealerPort, subPort, _currentRoomId);
        }

        public void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[ConnectionManager] {msg}"); }
        }
    }
}