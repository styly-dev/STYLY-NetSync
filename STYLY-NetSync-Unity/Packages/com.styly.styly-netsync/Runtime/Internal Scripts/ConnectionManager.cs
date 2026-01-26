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
        private SubscriberSocket _subSocket;           // Transform channel
        private SubscriberSocket _stateSubSocket;      // State channel (NV, ID mappings, RPC)
        private Thread _receiveThread;
        private volatile bool _shouldStop;
        private bool _enableDebugLogs;
        private bool _logNetworkTraffic;
        private bool _connectionError;
        private ServerDiscoveryManager _discoveryManager;
        private string _currentRoomId;
        private int _stateSubPort;                     // State port (0 = use subPort for all)

        // Thread-safe exception state (written on receive thread, read on main thread)
        private volatile Exception _lastException;
        private long _lastExceptionAtUnixMs;

        public DealerSocket DealerSocket => _dealerSocket;
        public SubscriberSocket SubSocket => _subSocket;
        public SubscriberSocket StateSubSocket => _stateSubSocket;
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

        public void Connect(string serverAddress, int dealerPort, int subPort, string roomId)
        {
            // Default: no separate state port (uses subPort for all traffic)
            Connect(serverAddress, dealerPort, subPort, 0, roomId);
        }

        /// <summary>
        /// Connect to the server with optional separate state channel.
        /// </summary>
        /// <param name="serverAddress">Server address (e.g., tcp://localhost)</param>
        /// <param name="dealerPort">DEALER port for client-to-server messages</param>
        /// <param name="subPort">SUB port for transform broadcasts</param>
        /// <param name="stateSubPort">SUB port for state traffic (0 = use subPort for all)</param>
        /// <param name="roomId">Room ID to subscribe to</param>
        public void Connect(string serverAddress, int dealerPort, int subPort, int stateSubPort, string roomId)
        {
            if (_receiveThread != null)
            {
                return; // Already connected
            }

            _currentRoomId = roomId;
            _stateSubPort = stateSubPort;
            _connectionError = false;
            _shouldStop = false;
            _receiveThread = new Thread(() => NetworkLoop(serverAddress, dealerPort, subPort, stateSubPort, roomId))
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
            if (_stateSubSocket != null)
            {
                _stateSubSocket.Dispose();
            }
            if (_dealerSocket != null)
            {
                _dealerSocket.Dispose();
            }

            _subSocket = null;
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

        private void NetworkLoop(string serverAddress, int dealerPort, int subPort, int stateSubPort, string roomId)
        {
            SubscriberSocket stateSub = null;
            try
            {
                // Dealer (for sending)
                using var dealer = new DealerSocket();
                dealer.Options.Linger = TimeSpan.Zero;
                dealer.Options.SendHighWatermark = 10;
                dealer.Connect($"{serverAddress}:{dealerPort}");
                _dealerSocket = dealer;

                DebugLog($"[Thread] DEALER connected → {serverAddress}:{dealerPort}");

                // Subscriber for transforms (high-frequency channel)
                using var sub = new SubscriberSocket();
                sub.Options.Linger = TimeSpan.Zero;
                sub.Options.ReceiveHighWatermark = 10;
                sub.Connect($"{serverAddress}:{subPort}");
                sub.Subscribe(roomId);
                _subSocket = sub;

                DebugLog($"[Thread] SUB (transform) connected → {serverAddress}:{subPort}");

                // Optional: Subscriber for state traffic (NV, ID mappings, RPC)
                bool useSplitSub = stateSubPort > 0 && stateSubPort != subPort;
                if (useSplitSub)
                {
                    stateSub = new SubscriberSocket();
                    stateSub.Options.Linger = TimeSpan.Zero;
                    stateSub.Options.ReceiveHighWatermark = 100; // Higher for control traffic
                    stateSub.Connect($"{serverAddress}:{stateSubPort}");
                    stateSub.Subscribe(roomId);
                    _stateSubSocket = stateSub;
                    DebugLog($"[Thread] SUB (state) connected → {serverAddress}:{stateSubPort}");
                }

                // Notify connection established
                if (OnConnectionEstablished != null)
                {
                    OnConnectionEstablished.Invoke();
                }

                while (!_shouldStop)
                {
                    bool receivedAny = false;

                    // Poll transform socket
                    if (sub.TryReceiveFrameString(TimeSpan.FromMilliseconds(5), out var topic))
                    {
                        if (sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(5), out var payload))
                        {
                            if (topic == roomId)
                            {
                                try
                                {
                                    _messageProcessor.ProcessIncomingMessage(payload);
                                    receivedAny = true;
                                }
                                catch (Exception ex)
                                {
                                    Debug.LogError($"Transform parse error: {ex.Message}");
                                }
                            }
                        }
                    }

                    // Poll state socket if available
                    if (stateSub != null)
                    {
                        if (stateSub.TryReceiveFrameString(TimeSpan.FromMilliseconds(5), out var stateTopic))
                        {
                            if (stateSub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(5), out var statePayload))
                            {
                                if (stateTopic == roomId)
                                {
                                    try
                                    {
                                        _messageProcessor.ProcessIncomingMessage(statePayload);
                                        receivedAny = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.LogError($"State parse error: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }

                    // Small sleep to avoid busy-waiting when no messages
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
                    var statePortStr = stateSubPort > 0 ? $"/{stateSubPort}" : "";
                    var endpoint = $"{serverAddress}:{dealerPort}/{subPort}{statePortStr}";
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
            finally
            {
                // Clean up state socket if it was created
                if (stateSub != null)
                {
                    try { stateSub.Dispose(); }
                    catch { /* ignore */ }
                    _stateSubSocket = null;
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

        public void ProcessDiscoveredServer(string serverAddress, int dealerPort, int subPort)
        {
            ProcessDiscoveredServer(serverAddress, dealerPort, subPort, 0);
        }

        public void ProcessDiscoveredServer(string serverAddress, int dealerPort, int subPort, int stateSubPort)
        {
            if (_discoveryManager != null)
            {
                _discoveryManager.StopDiscovery();
            }
            Connect(serverAddress, dealerPort, subPort, stateSubPort, _currentRoomId);
        }

        public void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[ConnectionManager] {msg}"); }
        }

        /// <summary>
        /// Sends a heartbeat message to keep the client alive on the server.
        /// Should be called periodically (e.g., every 1.0s) to prevent false timeout
        /// when transform traffic is congested.
        /// </summary>
        /// <param name="roomId">The room ID to send heartbeat to.</param>
        /// <param name="deviceId">The device ID of this client.</param>
        /// <returns>True if heartbeat was sent successfully, false otherwise.</returns>
        public bool SendHeartbeat(string roomId, string deviceId)
        {
            if (_dealerSocket == null || string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(deviceId))
            {
                return false;
            }

            try
            {
                var msg = new NetMQ.NetMQMessage();
                msg.Append(roomId);
                msg.Append(BinarySerializer.SerializeHeartbeat(deviceId));
                return _dealerSocket.TrySendMultipartMessage(msg);
            }
            catch (Exception ex)
            {
                if (_enableDebugLogs)
                {
                    Debug.LogWarning($"[ConnectionManager] Failed to send heartbeat: {ex.Message}");
                }
                return false;
            }
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
