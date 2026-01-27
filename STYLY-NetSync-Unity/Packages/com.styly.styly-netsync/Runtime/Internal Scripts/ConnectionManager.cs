// ConnectionManager.cs - Handles network connection management
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
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
        private ServerDiscoveryManager _discoveryManager;
        private string _currentRoomId;

        // Thread-safe exception state (written on receive thread, read on main thread)
        private volatile Exception _lastException;
        private long _lastExceptionAtUnixMs;

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

        private readonly ConcurrentQueue<DealerSendItem> _reliableSendQ = new();
        private int _reliableSendQCount;
        private const int ReliableSendQMax = 512;
        private DealerSendItem _reliableInFlight;
        private int _hasReliableInFlight;

        private string _pendingTransformRoomId;
        private byte[] _pendingTransformPayload;
        private int _pendingTransformFlag;

        private const int TransformRcvHwm = 2;

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
            ClearOutgoingQueues();

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

            // Wait for receive thread to exit (unless we're on the receive thread)
            if (Thread.CurrentThread != _receiveThread)
            {
                WaitThreadExit(_receiveThread, 1000);
            }
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
                sub.Options.ReceiveHighWatermark = TransformRcvHwm;
                sub.Connect($"{serverAddress}:{subPort}");
                // Subscribe with topic. Using string here is one-time and acceptable.
                // Note: NetMQ also offers byte[] overloads, but subscription happens once per connection.
                sub.Subscribe(roomId);
                _subSocket = sub;

                DebugLog($"[Thread] SUB connected    → {serverAddress}:{subPort}");

                // Notify connection established
                if (OnConnectionEstablished != null)
                {
                    OnConnectionEstablished.Invoke();
                }

                var roomIdBytes = Encoding.UTF8.GetBytes(roomId);

                while (!_shouldStop)
                {
                    var sentAny = FlushOutgoing(dealer);
                    var receivedAny = false;

                    if (TryDrainTransform(sub, roomIdBytes, out var lastPayload))
                    {
                        try
                        {
                            _messageProcessor.ProcessIncomingMessage(lastPayload);
                            receivedAny = true;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Transform parse error: {ex.Message}");
                        }
                    }

                    if (!receivedAny && !sentAny)
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
                    var endpoint = $"{serverAddress}:{dealerPort}/{subPort}";
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

        public bool EnqueueTransformSend(string roomId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) { return false; }
            if (_dealerSocket == null) { return false; }

            _pendingTransformRoomId = roomId;
            _pendingTransformPayload = payload;
            Interlocked.Exchange(ref _pendingTransformFlag, 1);
            return true;
        }

        public bool EnqueueReliableSend(string roomId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) { return false; }
            if (_dealerSocket == null) { return false; }

            var n = Interlocked.Increment(ref _reliableSendQCount);
            if (n > ReliableSendQMax)
            {
                Interlocked.Decrement(ref _reliableSendQCount);
                return false;
            }

            _reliableSendQ.Enqueue(new DealerSendItem(roomId, payload));
            return true;
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

        private void ClearOutgoingQueues()
        {
            Interlocked.Exchange(ref _pendingTransformFlag, 0);
            _pendingTransformRoomId = null;
            _pendingTransformPayload = null;

            while (_reliableSendQ.TryDequeue(out _)) { }
            _reliableSendQCount = 0;
            _hasReliableInFlight = 0;
        }

        private bool FlushOutgoing(DealerSocket dealer)
        {
            bool didWork = false;

            if (Interlocked.CompareExchange(ref _hasReliableInFlight, 1, 1) == 1)
            {
                if (TrySendDealer(dealer, _reliableInFlight.RoomId, _reliableInFlight.Payload))
                {
                    Interlocked.Exchange(ref _hasReliableInFlight, 0);
                    didWork = true;
                }
                else
                {
                    return didWork;
                }
            }

            while (Interlocked.CompareExchange(ref _hasReliableInFlight, 1, 0) == 0 &&
                   _reliableSendQ.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _reliableSendQCount);
                _reliableInFlight = item;

                if (TrySendDealer(dealer, item.RoomId, item.Payload))
                {
                    Interlocked.Exchange(ref _hasReliableInFlight, 0);
                    didWork = true;
                    continue;
                }

                return didWork;
            }

            if (Interlocked.Exchange(ref _pendingTransformFlag, 0) == 1)
            {
                var room = _pendingTransformRoomId;
                var payload = _pendingTransformPayload;
                if (!TrySendDealer(dealer, room, payload))
                {
                    _pendingTransformRoomId = room;
                    _pendingTransformPayload = payload;
                    Interlocked.Exchange(ref _pendingTransformFlag, 1);
                }
                else
                {
                    didWork = true;
                }
            }

            return didWork;
        }

        private static bool TrySendDealer(DealerSocket dealer, string roomId, byte[] payload)
        {
            var msg = new NetMQMessage();
            try
            {
                msg.Append(roomId);
                msg.Append(payload);
                return dealer.TrySendMultipartMessage(msg);
            }
            finally
            {
                msg.Clear();
            }
        }

        private static bool TryDrainTransform(SubscriberSocket sub, byte[] roomIdBytes, out byte[] lastPayload)
        {
            lastPayload = null;
            bool got = false;

            if (!sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(10), out var topic))
            {
                return false;
            }

            if (!sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(10), out var payload))
            {
                return false;
            }

            if (TopicMatches(topic, roomIdBytes))
            {
                lastPayload = payload;
                got = true;
            }

            while (sub.TryReceiveFrameBytes(TimeSpan.Zero, out topic))
            {
                if (!sub.TryReceiveFrameBytes(TimeSpan.Zero, out payload))
                {
                    break;
                }

                if (TopicMatches(topic, roomIdBytes))
                {
                    lastPayload = payload;
                    got = true;
                }
            }

            return got;
        }

        private static bool TopicMatches(byte[] topic, byte[] roomIdBytes)
        {
            if (topic == null || roomIdBytes == null) { return false; }
            if (topic.Length != roomIdBytes.Length) { return false; }
            for (int i = 0; i < topic.Length; i++)
            {
                if (topic[i] != roomIdBytes[i])
                {
                    return false;
                }
            }
            return true;
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

        private readonly struct DealerSendItem
        {
            public readonly string RoomId;
            public readonly byte[] Payload;

            public DealerSendItem(string roomId, byte[] payload)
            {
                RoomId = roomId;
                Payload = payload;
            }
        }
    }
}
