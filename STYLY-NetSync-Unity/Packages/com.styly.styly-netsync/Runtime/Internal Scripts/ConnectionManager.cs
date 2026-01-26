// ConnectionManager.cs - Handles network connection management
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using NetMQ;
using NetMQ.Sockets;
using UnityEngine;

namespace Styly.NetSync
{
    internal class ConnectionManager
    {
        private DealerSocket _dealerSocket;
        private SubscriberSocket _transformSubSocket;
        private SubscriberSocket _stateSubSocket;
        private Thread _receiveThread;
        private volatile bool _shouldStop;
        private bool _enableDebugLogs;
        private bool _logNetworkTraffic;
        private bool _connectionError;
        private ServerDiscoveryManager _discoveryManager;
        private string _currentRoomId;
        
        // Thread-safe exception state (written on receive thread, read on main thread)
        private volatile Exception _lastException;
        private long _lastExceptionAtUnixMs;

        public DealerSocket DealerSocket => _dealerSocket;
        public SubscriberSocket TransformSubSocket => _transformSubSocket;
        public SubscriberSocket StateSubSocket => _stateSubSocket;
        public bool IsConnected => _dealerSocket != null && _transformSubSocket != null && _stateSubSocket != null && !_connectionError;
        public bool IsConnectionError => _connectionError;
        
        // Thread-safe accessors for exception state
        public Exception LastException => _lastException;
        public long LastExceptionAtUnixMs => Volatile.Read(ref _lastExceptionAtUnixMs);

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

        public void Connect(string serverAddress, int dealerPort, int transformSubPort, int stateSubPort, string roomId)
        {
            if (_receiveThread != null)
            {
                return; // Already connected
            }

            _currentRoomId = roomId;
            _connectionError = false;
            _shouldStop = false;
            _receiveThread = new Thread(() => NetworkLoop(serverAddress, dealerPort, transformSubPort, stateSubPort, roomId))
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
            if (_transformSubSocket != null) { _transformSubSocket.Dispose(); }
            if (_stateSubSocket != null) { _stateSubSocket.Dispose(); }
            if (_dealerSocket != null)
            {
                _dealerSocket.Dispose();
            }

            _transformSubSocket = null;
            _stateSubSocket = null;
            _dealerSocket = null;

            // Wait for receive thread to exit (unless we're on the receive thread)
            if (Thread.CurrentThread != _receiveThread)
            {
                WaitThreadExit(_receiveThread, 1000);
            }
            _receiveThread = null;

            // OS-specific cleanup
            SafeNetMQCleanup();
        }

        private void NetworkLoop(string serverAddress, int dealerPort, int transformSubPort, int stateSubPort, string roomId)
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

                // Subscriber (transform downlink)
                using var transformSub = new SubscriberSocket();
                transformSub.Options.Linger = TimeSpan.Zero;
                transformSub.Options.ReceiveHighWatermark = 10;
                transformSub.Connect($"{serverAddress}:{transformSubPort}");
                transformSub.Subscribe(roomId);
                _transformSubSocket = transformSub;

                DebugLog($"[Thread] Transform SUB connected → {serverAddress}:{transformSubPort}");

                // Subscriber (state downlink)
                using var stateSub = new SubscriberSocket();
                stateSub.Options.Linger = TimeSpan.Zero;
                stateSub.Options.ReceiveHighWatermark = 10;
                stateSub.Connect($"{serverAddress}:{stateSubPort}");
                stateSub.Subscribe(roomId);
                _stateSubSocket = stateSub;

                DebugLog($"[Thread] State SUB connected → {serverAddress}:{stateSubPort}");

                // Notify connection established
                if (OnConnectionEstablished != null)
                {
                    OnConnectionEstablished.Invoke();
                }

                while (!_shouldStop)
                {
                    bool receivedAny = false;
                    receivedAny |= TryReceiveFromSubSocket(transformSub, roomId);
                    receivedAny |= TryReceiveFromSubSocket(stateSub, roomId);
                    receivedAny |= TryReceiveFromDealer(dealer, roomId);

                    if (!receivedAny)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_shouldStop)
                {
                    // Store exception details for diagnostics (thread-safe handoff to main thread)
                    var ex_local = ex;
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    
                    // Write timestamp first, then exception (helps with ordering)
                    Volatile.Write(ref _lastExceptionAtUnixMs, timestamp);
                    _lastException = ex_local;
                    
                    // Log detailed exception context
                    var threadId = Thread.CurrentThread.ManagedThreadId;
                    var endpoint = $"{serverAddress}:{dealerPort}/{transformSubPort}/{stateSubPort}";
                    Debug.LogError($"[ConnectionManager] Network thread error. " +
                                   $"Type={ex_local.GetType().Name} Message={ex_local.Message} " +
                                   $"Endpoint={endpoint} ThreadId={threadId} " +
                                   $"Time={timestamp}");
                    
#if NETSYNC_DEBUG_CONNECTION
                    // Verbose logging: include stack trace
                    Debug.LogError($"[ConnectionManager] Stack trace: {ex_local.StackTrace}");
#endif
                    
                    _connectionError = true;
                    if (OnConnectionError != null)
                    {
                        OnConnectionError.Invoke(ex_local.Message);
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
            // Always attempt cleanup on all platforms to ensure background threads exit.
            const int timeoutMs = 500;
            try
            {
                var cts = new CancellationTokenSource();
                var task = Task.Run(() => NetMQConfig.Cleanup(false), cts.Token);

                if (!task.Wait(timeoutMs))
                {
                    cts.Cancel();
                    Debug.LogWarning("[NetMQ] Cleanup timeout – forced skip");
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

        public void ProcessDiscoveredServer(string serverAddress, int dealerPort, int transformSubPort, int stateSubPort)
        {
            if (_discoveryManager != null)
            {
                _discoveryManager.StopDiscovery();
            }
            Connect(serverAddress, dealerPort, transformSubPort, stateSubPort, _currentRoomId);
        }

        internal bool TrySendDealerMessage(string roomId, byte[] payload)
        {
            if (_dealerSocket == null || string.IsNullOrEmpty(roomId) || payload == null)
            {
                return false;
            }

            var msg = new NetMQMessage();
            try
            {
                msg.Append(roomId);
                msg.Append(payload);
                return _dealerSocket.TrySendMultipartMessage(msg);
            }
            finally
            {
                msg.Clear();
            }
        }

        private bool TryReceiveFromSubSocket(SubscriberSocket socket, string roomId)
        {
            if (socket == null) { return false; }
            if (!socket.TryReceiveFrameString(TimeSpan.FromMilliseconds(1), out var topic))
            {
                return false;
            }
            if (!socket.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(1), out var payload))
            {
                return false;
            }

            if (topic != roomId)
            {
                return true;
            }

            try
            {
                _messageProcessor.ProcessIncomingMessage(payload);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Binary parse error: {ex.Message}");
            }
            return true;
        }

        private bool TryReceiveFromDealer(DealerSocket dealer, string roomId)
        {
            if (dealer == null)
            {
                return false;
            }

            if (!dealer.TryReceiveMultipartMessage(TimeSpan.FromMilliseconds(1), out var msg))
            {
                return false;
            }

            try
            {
                if (msg.FrameCount < 2)
                {
                    return true;
                }

                var topic = msg[0].ConvertToString();
                if (topic != roomId)
                {
                    return true;
                }

                var payload = msg[1].ToByteArray();
                _messageProcessor.ProcessIncomingMessage(payload);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Dealer receive error: {ex.Message}");
            }
            finally
            {
                msg.Clear();
            }

            return true;
        }

        public void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[ConnectionManager] {msg}"); }
        }

        /// <summary>
        /// Clears the connection error state. Should be called after StopNetworking() 
        /// and before attempting reconnection.
        /// Thread-safe: must be called from main thread when receive thread is not running.
        /// </summary>
        public void ClearConnectionError()
        {
            _connectionError = false;
            // Clear exception reference to prevent memory retention
            // (NetSyncManager has already copied it to _pendingConnectionException)
            _lastException = null;
            // Keep timestamp for basic diagnostics
        }
    }
}
