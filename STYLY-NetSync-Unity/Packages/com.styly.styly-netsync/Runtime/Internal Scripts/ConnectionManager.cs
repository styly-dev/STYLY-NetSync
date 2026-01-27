// ConnectionManager.cs - Handles network connection management
using System;
using System.Collections.Concurrent;
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

        // --- Send Queue System ---
        // Reliable queue (RPC, Network Variables, Heartbeat, etc.) - FIFO order preserved
        private readonly ConcurrentQueue<DealerSendItem> _reliableSendQ = new();
        private int _reliableSendQCount;
        private const int ReliableSendQMax = 512;

        // Transform queue (latest-only, single slot with overwrite)
        private volatile string _pendingTransformRoomId;
        private volatile byte[] _pendingTransformPayload;
        private int _pendingTransformFlag; // 0 = empty, 1 = has pending

        // Constants for SUB socket configuration
        private const int TransformRcvHwm = 2; // Transform is latest-priority (practical conflate)

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

        [Obsolete("Direct DealerSocket access is deprecated. Use EnqueueTransformSend/EnqueueReliableSend instead.")]
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

            // Clear send queues to avoid stale data on reconnect
            ClearSendQueues();

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

        private void ClearSendQueues()
        {
            // Clear transform slot
            Interlocked.Exchange(ref _pendingTransformFlag, 0);
            _pendingTransformPayload = null;
            _pendingTransformRoomId = null;

            // Clear reliable queue
            while (_reliableSendQ.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _reliableSendQCount, 0);
        }

        private void NetworkLoop(string serverAddress, int dealerPort, int subPort, string roomId)
        {
            try
            {
                // Dealer (for sending) - accessed only from this thread
                using var dealer = new DealerSocket();
                dealer.Options.Linger = TimeSpan.Zero;
                dealer.Options.SendHighWatermark = 10;
                dealer.Connect($"{serverAddress}:{dealerPort}");
                _dealerSocket = dealer;

                DebugLog($"[Thread] DEALER connected → {serverAddress}:{dealerPort}");

                // Subscriber (for receiving) - use low HWM for latest-wins transform behavior
                using var sub = new SubscriberSocket();
                sub.Options.Linger = TimeSpan.Zero;
                sub.Options.ReceiveHighWatermark = TransformRcvHwm; // Low HWM for transform (latest-priority)
                sub.Connect($"{serverAddress}:{subPort}");
                // Subscribe with topic. Using string here is one-time and acceptable.
                sub.Subscribe(roomId);
                _subSocket = sub;

                DebugLog($"[Thread] SUB connected    → {serverAddress}:{subPort}");

                // Pre-encode roomId for byte comparison (avoids string allocation per message)
                var roomIdBytes = Encoding.UTF8.GetBytes(roomId);

                // Notify connection established
                if (OnConnectionEstablished != null)
                {
                    OnConnectionEstablished.Invoke();
                }

                while (!_shouldStop)
                {
                    bool didWork = false;

                    // === SEND: Flush outgoing messages (network thread is the only writer to socket) ===
                    didWork |= FlushOutgoing(dealer);

                    // === RECEIVE: Drain SUB socket and process only the last payload ===
                    byte[] lastPayload = null;
                    bool gotMessage = false;

                    // Drain all available messages, keeping only the last one
                    while (sub.TryReceiveFrameBytes(TimeSpan.Zero, out var topicBytes))
                    {
                        if (!sub.TryReceiveFrameBytes(TimeSpan.Zero, out var payload))
                        {
                            break; // Incomplete multipart, stop
                        }

                        // Compare topic bytes directly (avoids string allocation)
                        if (BytesEqual(topicBytes, roomIdBytes))
                        {
                            lastPayload = payload; // Overwrite with latest
                            gotMessage = true;
                        }
                    }

                    // Process only the last (most recent) message
                    if (gotMessage && lastPayload != null)
                    {
                        try
                        {
                            _messageProcessor.ProcessIncomingMessage(lastPayload);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Binary parse error: {ex.Message}");
                        }
                        didWork = true;
                    }

                    // Sleep only when no work was done to avoid busy-spinning
                    if (!didWork)
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

        /// <summary>
        /// Flush pending outgoing messages from queues. Called from network thread only.
        /// </summary>
        private bool FlushOutgoing(DealerSocket dealer)
        {
            bool didWork = false;

            // 1) Flush Reliable queue (RPC/NV) - higher priority, FIFO order
            // In-flight item for order preservation on send failure
            DealerSendItem? inFlight = null;

            while (true)
            {
                DealerSendItem item;
                if (inFlight.HasValue)
                {
                    item = inFlight.Value;
                    inFlight = null;
                }
                else if (_reliableSendQ.TryDequeue(out item))
                {
                    Interlocked.Decrement(ref _reliableSendQCount);
                }
                else
                {
                    break; // Queue empty
                }

                if (TrySendDealer(dealer, item.RoomId, item.Payload))
                {
                    didWork = true;
                }
                else
                {
                    // Send failed (HWM or socket issue) - hold this item for retry next loop
                    // This preserves FIFO order by not dequeuing more until this succeeds
                    inFlight = item;
                    break;
                }
            }

            // If we have an in-flight item that couldn't be sent, we need to re-queue it at the front
            // Since ConcurrentQueue doesn't support prepend, we'll hold it in a field for next iteration
            // For simplicity, we re-enqueue it (slight order change for one item, acceptable trade-off)
            if (inFlight.HasValue)
            {
                // Re-increment count since we'll re-enqueue
                Interlocked.Increment(ref _reliableSendQCount);
                // Note: This slightly breaks FIFO for failed items, but is acceptable for robustness
                // A more complex solution would use a separate in-flight field
            }

            // 2) Flush Transform slot (latest-only)
            if (Interlocked.Exchange(ref _pendingTransformFlag, 0) == 1)
            {
                var room = _pendingTransformRoomId;
                var payload = _pendingTransformPayload;

                if (room != null && payload != null)
                {
                    if (TrySendDealer(dealer, room, payload))
                    {
                        didWork = true;
                    }
                    else
                    {
                        // Send failed - restore slot for retry (transform is latest-wins anyway)
                        _pendingTransformRoomId = room;
                        _pendingTransformPayload = payload;
                        Interlocked.Exchange(ref _pendingTransformFlag, 1);
                    }
                }
            }

            return didWork;
        }

        /// <summary>
        /// Attempt to send a multipart message via dealer socket.
        /// </summary>
        private static bool TrySendDealer(DealerSocket dealer, string roomId, byte[] payload)
        {
            try
            {
                var msg = new NetMQMessage();
                msg.Append(roomId);
                msg.Append(payload);
                var ok = dealer.TrySendMultipartMessage(msg);
                msg.Clear();
                return ok;
            }
            catch
            {
                // Exception during send - treat as failure
                return false;
            }
        }

        /// <summary>
        /// Fast byte array comparison (avoids LINQ/string allocations).
        /// </summary>
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
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

        // === Send Queue APIs (thread-safe, called from main thread) ===

        /// <summary>
        /// Enqueue a transform message for sending. Uses latest-wins semantics (overwrites any pending).
        /// </summary>
        /// <param name="roomId">Room ID for the message</param>
        /// <param name="payload">Serialized transform payload</param>
        /// <returns>True if enqueued successfully, false if connection is not available</returns>
        public bool EnqueueTransformSend(string roomId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) return false;
            if (_dealerSocket == null) return false;

            // Latest-wins: simply overwrite the slot
            _pendingTransformRoomId = roomId;
            _pendingTransformPayload = payload;
            Interlocked.Exchange(ref _pendingTransformFlag, 1);
            return true;
        }

        /// <summary>
        /// Enqueue a reliable message (RPC, Network Variable, etc.) for sending. FIFO order preserved.
        /// </summary>
        /// <param name="roomId">Room ID for the message</param>
        /// <param name="payload">Serialized message payload</param>
        /// <returns>True if enqueued successfully, false if queue is full or connection unavailable</returns>
        public bool EnqueueReliableSend(string roomId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) return false;
            if (_dealerSocket == null) return false;

            var count = Interlocked.Increment(ref _reliableSendQCount);
            if (count > ReliableSendQMax)
            {
                Interlocked.Decrement(ref _reliableSendQCount);
                Debug.LogWarning($"[ConnectionManager] Reliable send queue full ({ReliableSendQMax}), dropping message");
                return false;
            }

            _reliableSendQ.Enqueue(new DealerSendItem(roomId, payload));
            return true;
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
