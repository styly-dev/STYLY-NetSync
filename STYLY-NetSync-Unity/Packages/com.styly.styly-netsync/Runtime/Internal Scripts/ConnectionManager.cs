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
        private SubscriberSocket _subSocket;  // Legacy single SUB socket
        private SubscriberSocket _transformSubSocket;  // SUB for transforms (dual mode)
        private SubscriberSocket _stateSubSocket;  // SUB for state (NV, device ID mapping) (dual mode)
        private bool _dualSubMode;  // True if using separate transform/state sockets
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
        public SubscriberSocket SubSocket => _subSocket;
        public bool IsConnected => _dealerSocket != null && _subSocket != null && !_connectionError;
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

        /// <summary>
        /// Connect to the server with optional dual SUB ports.
        /// </summary>
        /// <param name="serverAddress">Server address (e.g., tcp://192.168.1.100)</param>
        /// <param name="dealerPort">DEALER port for sending</param>
        /// <param name="subPort">Legacy SUB port (used if transformSubPort/stateSubPort are 0)</param>
        /// <param name="roomId">Room ID to subscribe to</param>
        /// <param name="transformSubPort">Transform SUB port (0 to use legacy mode)</param>
        /// <param name="stateSubPort">State SUB port (0 to use legacy mode)</param>
        public void Connect(string serverAddress, int dealerPort, int subPort, string roomId, int transformSubPort = 0, int stateSubPort = 0)
        {
            if (_receiveThread != null)
            {
                return; // Already connected
            }

            _currentRoomId = roomId;
            _connectionError = false;
            _shouldStop = false;
            _dualSubMode = transformSubPort > 0 && stateSubPort > 0;
            _receiveThread = new Thread(() => NetworkLoop(serverAddress, dealerPort, subPort, roomId, transformSubPort, stateSubPort))
            {
                IsBackground = true,
                Name = "STYLY_NetworkThread"
            };
            _receiveThread.Start();
            DebugLog(_dualSubMode
                ? $"Network thread started (dual SUB: transform={transformSubPort}, state={stateSubPort})"
                : "Network thread started (legacy single SUB)");
        }

        public void Disconnect()
        {
            if (_receiveThread == null) { return; }

            _shouldStop = true;

            // Dispose all sockets
            if (_subSocket != null)
            {
                _subSocket.Dispose();
                _subSocket = null;
            }
            if (_transformSubSocket != null)
            {
                _transformSubSocket.Dispose();
                _transformSubSocket = null;
            }
            if (_stateSubSocket != null)
            {
                _stateSubSocket.Dispose();
                _stateSubSocket = null;
            }
            if (_dealerSocket != null)
            {
                _dealerSocket.Dispose();
                _dealerSocket = null;
            }

            // Wait for receive thread to exit (unless we're on the receive thread)
            if (Thread.CurrentThread != _receiveThread)
            {
                WaitThreadExit(_receiveThread, 1000);
            }
            _receiveThread = null;
            _dualSubMode = false;

            // OS-specific cleanup
            SafeNetMQCleanup();
        }

        private void NetworkLoop(string serverAddress, int dealerPort, int subPort, string roomId, int transformSubPort = 0, int stateSubPort = 0)
        {
            bool dualMode = transformSubPort > 0 && stateSubPort > 0;
            try
            {
                // Dealer (for sending)
                using var dealer = new DealerSocket();
                dealer.Options.Linger = TimeSpan.Zero;
                dealer.Options.SendHighWatermark = 10;
                dealer.Connect($"{serverAddress}:{dealerPort}");
                _dealerSocket = dealer;

                DebugLog($"[Thread] DEALER connected → {serverAddress}:{dealerPort}");

                if (dualMode)
                {
                    // Dual SUB mode: separate sockets for transforms and state
                    using var transformSub = new SubscriberSocket();
                    transformSub.Options.Linger = TimeSpan.Zero;
                    transformSub.Options.ReceiveHighWatermark = 10;
                    transformSub.Connect($"{serverAddress}:{transformSubPort}");
                    transformSub.Subscribe(roomId);
                    _transformSubSocket = transformSub;

                    using var stateSub = new SubscriberSocket();
                    stateSub.Options.Linger = TimeSpan.Zero;
                    stateSub.Options.ReceiveHighWatermark = 10;
                    stateSub.Connect($"{serverAddress}:{stateSubPort}");
                    stateSub.Subscribe(roomId);
                    _stateSubSocket = stateSub;

                    DebugLog($"[Thread] Dual SUB connected → transform:{transformSubPort}, state:{stateSubPort}");

                    // Notify connection established
                    if (OnConnectionEstablished != null)
                    {
                        OnConnectionEstablished.Invoke();
                    }

                    // Dual mode receive loop
                    while (!_shouldStop)
                    {
                        // Try to receive from transform socket
                        if (transformSub.TryReceiveFrameString(TimeSpan.FromMilliseconds(5), out var transformTopic))
                        {
                            if (transformSub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(5), out var transformPayload))
                            {
                                if (transformTopic == roomId)
                                {
                                    try { _messageProcessor.ProcessIncomingMessage(transformPayload); }
                                    catch (Exception ex) { Debug.LogError($"Binary parse error (transform): {ex.Message}"); }
                                }
                            }
                        }

                        // Try to receive from state socket
                        if (stateSub.TryReceiveFrameString(TimeSpan.FromMilliseconds(5), out var stateTopic))
                        {
                            if (stateSub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(5), out var statePayload))
                            {
                                if (stateTopic == roomId)
                                {
                                    try { _messageProcessor.ProcessIncomingMessage(statePayload); }
                                    catch (Exception ex) { Debug.LogError($"Binary parse error (state): {ex.Message}"); }
                                }
                            }
                        }

                        // Try to receive from dealer socket (for reliable RPC deliveries)
                        if (dealer.TryReceiveFrameString(TimeSpan.FromMilliseconds(5), out var dealerTopic))
                        {
                            if (dealer.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(5), out var dealerPayload))
                            {
                                if (dealerTopic == roomId)
                                {
                                    try { _messageProcessor.ProcessIncomingMessage(dealerPayload); }
                                    catch (Exception ex) { Debug.LogError($"Binary parse error (dealer): {ex.Message}"); }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Legacy single SUB mode
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
                        // Receive two frames: [topic][payload]. Use string topic and direct comparison.
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
                    var endpoint = dualMode
                        ? $"{serverAddress}:{dealerPort}/transform:{transformSubPort}/state:{stateSubPort}"
                        : $"{serverAddress}:{dealerPort}/{subPort}";
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

        public void ProcessDiscoveredServer(string serverAddress, int dealerPort, int subPort, int transformSubPort = 0, int stateSubPort = 0)
        {
            if (_discoveryManager != null)
            {
                _discoveryManager.StopDiscovery();
            }
            Connect(serverAddress, dealerPort, subPort, _currentRoomId, transformSubPort, stateSubPort);
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
