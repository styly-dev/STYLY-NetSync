// ConnectionManager.cs - Handles network connection management
using System;
using System.Collections.Concurrent;
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

        public SubscriberSocket SubSocket => _subSocket;
        public bool IsConnected => _dealerSocket != null && _subSocket != null && !_connectionError;
        public bool IsConnectionError => _connectionError;

        // Thread-safe accessors for exception state
        public Exception LastException => _lastException;
        public long LastExceptionAtUnixMs => Volatile.Read(ref _lastExceptionAtUnixMs);

        /// <summary>
        /// Cumulative count of transform frames that were skipped during SUB draining.
        /// Only the latest frame is processed; older frames are dropped for latency reduction.
        /// </summary>
        public long DroppedTransformFrames => Interlocked.Read(ref _droppedTransformFrames);

        public event Action<string> OnConnectionError;
        public event Action OnConnectionEstablished;

        private readonly NetSyncManager _netSyncManager;
        private readonly MessageProcessor _messageProcessor;

        private const int TransformRcvHwm = 2;
        private const int ReliableSendQMax = 512;
        private const long QueueFullWarnIntervalMs = 5000; // Rate limit warning logs to once per 5 seconds
        private long _lastQueueFullWarnMs;

        // Diagnostic counter for tracking dropped transform frames during draining
        private long _droppedTransformFrames;

        private readonly ConcurrentQueue<DealerSendItem> _reliableSendQ = new();
        private int _reliableSendQCount;
        private DealerSendItem _reliableInFlight;
        private int _hasReliableInFlight;

        // ===== Phase 2: Application-level send queue with priority control =====

        /// <summary>
        /// Maximum number of control messages that can be queued.
        /// Control messages include RPC and Network Variable updates.
        /// </summary>
        private const int CTRL_OUTBOX_MAX = 256;

        /// <summary>
        /// Time-to-live for control messages in seconds.
        /// Messages older than this are dropped during drain.
        /// </summary>
        private const double CTRL_TTL_SECONDS = 5.0;

        /// <summary>
        /// Queue for control messages (RPC, Network Variables).
        /// Control messages have priority over transform updates.
        /// </summary>
        private readonly ConcurrentQueue<OutboundPacket> _ctrlOutbox = new();
        private int _ctrlOutboxCount;

        /// <summary>
        /// Latest transform packet to send (latest-wins semantics).
        /// Only the most recent transform is sent; older ones are overwritten.
        /// </summary>
        private volatile OutboundPacket _latestTransform;

        /// <summary>
        /// Counter for tracking backpressure events (EAGAIN/would-block).
        /// Incremented when a send fails due to HWM being reached.
        /// </summary>
        private long _wouldBlockCount;

        /// <summary>
        /// Number of control messages currently in the queue.
        /// </summary>
        public int ControlQueueLength => Volatile.Read(ref _ctrlOutboxCount);

        /// <summary>
        /// Cumulative count of send failures due to backpressure (EAGAIN/would-block).
        /// </summary>
        public long WouldBlockCount => Interlocked.Read(ref _wouldBlockCount);

        private volatile string _pendingTransformRoomId;
        private volatile byte[] _pendingTransformPayload;
        private int _pendingTransformFlag;

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
            ClearOutgoingBuffers();

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

        public bool EnqueueTransformSend(string roomId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) { return false; }
            if (_dealerSocket == null) { return false; }

            _pendingTransformRoomId = roomId;
            _pendingTransformPayload = payload;
            // Ensure writes are visible to the network thread before setting the flag
            Thread.MemoryBarrier();
            Interlocked.Exchange(ref _pendingTransformFlag, 1);
            return true;
        }

        public bool EnqueueReliableSend(string roomId, byte[] payload)
        {
            // Delegate to TryEnqueueControl for backwards compatibility
            return TryEnqueueControl(roomId, payload);
        }

        /// <summary>
        /// Enqueue a control message (RPC, Network Variable) for sending.
        /// Control messages are sent with priority over transform updates.
        /// Messages are subject to TTL expiration and queue size limits.
        /// </summary>
        /// <param name="roomId">The room ID to send to.</param>
        /// <param name="payload">The serialized payload bytes.</param>
        /// <returns>True if enqueued successfully, false if queue is full or connection unavailable.</returns>
        public bool TryEnqueueControl(string roomId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) { return false; }
            if (_dealerSocket == null) { return false; }

            var n = Interlocked.Increment(ref _ctrlOutboxCount);
            if (n > CTRL_OUTBOX_MAX)
            {
                Interlocked.Decrement(ref _ctrlOutboxCount);
                // Rate-limited warning: only log occasionally to avoid spam
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - Interlocked.Read(ref _lastQueueFullWarnMs) > QueueFullWarnIntervalMs)
                {
                    Interlocked.Exchange(ref _lastQueueFullWarnMs, now);
                    Debug.LogWarning($"[ConnectionManager] Control outbox full ({CTRL_OUTBOX_MAX}), dropping message");
                }
                return false;
            }

            var packet = new OutboundPacket
            {
                Lane = OutboundLane.Control,
                RoomId = roomId,
                Payload = payload,
                EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Attempts = 0
            };

            _ctrlOutbox.Enqueue(packet);
            return true;
        }

        /// <summary>
        /// Set the latest transform packet to send (latest-wins semantics).
        /// Only the most recent transform is retained; calling this again overwrites the previous.
        /// </summary>
        /// <param name="roomId">The room ID to send to.</param>
        /// <param name="payload">The serialized transform payload bytes.</param>
        public void SetLatestTransform(string roomId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) { return; }
            if (_dealerSocket == null) { return; }

            var packet = new OutboundPacket
            {
                Lane = OutboundLane.Transform,
                RoomId = roomId,
                Payload = payload,
                EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Attempts = 0
            };

            // Atomic overwrite - latest-wins semantics
            Volatile.Write(ref _latestTransform, packet);
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

                var roomIdBytes = Encoding.UTF8.GetBytes(roomId);

                // Notify connection established
                if (OnConnectionEstablished != null)
                {
                    OnConnectionEstablished.Invoke();
                }

                while (!_shouldStop)
                {
                    var sentAny = FlushOutgoing(dealer);
                    bool receivedAny = false;

                    byte[] lastPayload = null;
                    bool gotPayload = false;
                    int framesReceived = 0;

                    while (sub.TryReceiveFrameBytes(TimeSpan.Zero, out var topicBytes))
                    {
                        if (!sub.TryReceiveFrameBytes(TimeSpan.Zero, out var payload)) { break; }

                        if (TopicMatches(topicBytes, roomIdBytes))
                        {
                            lastPayload = payload;
                            gotPayload = true;
                            framesReceived++;
                        }
                    }

                    if (gotPayload)
                    {
                        // Track dropped frames for diagnostics (only process the last one)
                        if (framesReceived > 1)
                        {
                            Interlocked.Add(ref _droppedTransformFrames, framesReceived - 1);
                        }

                        try
                        {
                            _messageProcessor.ProcessIncomingMessage(lastPayload);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Transform parse error: {ex.Message}");
                        }
                        receivedAny = true;
                    }

                    // DEALER receive: control messages (RPC, NV, ID mapping) from ROUTER
                    // Server sends control messages via ROUTER->DEALER instead of PUB/SUB
                    // Message format: [roomId, payload] (2 frames)
                    while (dealer.TryReceiveFrameString(TimeSpan.Zero, out var dealerRoomId))
                    {
                        if (!dealer.TryReceiveFrameBytes(TimeSpan.Zero, out var dealerPayload))
                        {
                            // Incomplete message - should not happen with proper framing
                            break;
                        }

                        // Only process messages for our room
                        if (dealerRoomId == roomId)
                        {
                            try
                            {
                                _messageProcessor.ProcessIncomingMessage(dealerPayload);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"Control message parse error: {ex.Message}");
                            }
                        }
                        receivedAny = true;
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

        private bool FlushOutgoing(DealerSocket dealer)
        {
            bool didWork = false;

            // Phase 2: Priority-based send drain
            // 1. Drain control messages first (higher priority)
            // 2. Then try to send latest transform (lower priority)

            // Drain control outbox with priority
            didWork |= DrainControlSends(dealer, maxBatch: 64);

            // Try to send the latest transform (latest-wins semantics)
            didWork |= TrySendLatestTransform(dealer);

            // Legacy: Handle old-style pending transform (for backwards compatibility with EnqueueTransformSend)
            if (Interlocked.Exchange(ref _pendingTransformFlag, 0) == 1)
            {
                var room = _pendingTransformRoomId;
                var payload = _pendingTransformPayload;

                // Null check to handle race condition during disconnect or incomplete writes
                if (room == null || payload == null)
                {
                    // Skip this send; data was cleared during disconnect
                }
                else if (!TrySendDealer(dealer, room, payload))
                {
                    _pendingTransformRoomId = room;
                    _pendingTransformPayload = payload;
                    Interlocked.Exchange(ref _pendingTransformFlag, 1);
                    Interlocked.Increment(ref _wouldBlockCount);
                }
                else
                {
                    didWork = true;
                }
            }

            return didWork;
        }

        /// <summary>
        /// Drain control messages from the outbox queue.
        /// Control messages are sent with priority and support TTL expiration.
        /// </summary>
        /// <param name="dealer">The dealer socket to send on.</param>
        /// <param name="maxBatch">Maximum number of messages to drain in one batch.</param>
        /// <returns>True if any messages were sent.</returns>
        private bool DrainControlSends(DealerSocket dealer, int maxBatch)
        {
            bool didWork = false;
            int sent = 0;
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            while (sent < maxBatch && _ctrlOutbox.TryDequeue(out var packet))
            {
                Interlocked.Decrement(ref _ctrlOutboxCount);

                // TTL check - skip expired packets
                if (now - packet.EnqueuedAt > CTRL_TTL_SECONDS)
                {
                    // Packet expired, drop it silently
                    continue;
                }

                // Try to send
                if (TrySendDealer(dealer, packet.RoomId, packet.Payload))
                {
                    didWork = true;
                    sent++;
                }
                else
                {
                    // Backpressure - increment counter and stop draining
                    Interlocked.Increment(ref _wouldBlockCount);
                    packet.Attempts++;

                    // Re-enqueue at the front is not possible with ConcurrentQueue,
                    // so we accept that this packet may be processed out of order on retry.
                    // For control messages, this is acceptable as they are idempotent or have timestamps.
                    // Note: We don't re-enqueue to avoid infinite loops; the packet is dropped on backpressure.
                    // In practice, the HWM should rarely be hit with proper flow control.
                    break;
                }
            }

            return didWork;
        }

        /// <summary>
        /// Try to send the latest transform packet.
        /// Uses latest-wins semantics: only the most recent transform is sent.
        /// </summary>
        /// <param name="dealer">The dealer socket to send on.</param>
        /// <returns>True if a transform was sent.</returns>
        private bool TrySendLatestTransform(DealerSocket dealer)
        {
            // Read atomically
            var packet = Volatile.Read(ref _latestTransform);
            if (packet == null)
            {
                return false;
            }

            // Try to send
            if (TrySendDealer(dealer, packet.RoomId, packet.Payload))
            {
                // Success - clear the packet using compare-exchange
                // This ensures we don't clear a newer packet that was set during send
                Interlocked.CompareExchange(ref _latestTransform, null, packet);
                return true;
            }
            else
            {
                // Backpressure - keep the packet for retry
                Interlocked.Increment(ref _wouldBlockCount);
                return false;
            }
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

        private static bool TopicMatches(byte[] topicBytes, byte[] roomIdBytes)
        {
            if (topicBytes == null || roomIdBytes == null) { return false; }
            if (topicBytes.Length != roomIdBytes.Length) { return false; }

            for (int i = 0; i < roomIdBytes.Length; i++)
            {
                if (topicBytes[i] != roomIdBytes[i]) { return false; }
            }

            return true;
        }

        private void ClearOutgoingBuffers()
        {
            // Clear legacy pending transform
            Interlocked.Exchange(ref _pendingTransformFlag, 0);
            _pendingTransformRoomId = null;
            _pendingTransformPayload = null;

            // Clear legacy reliable send queue (kept for backwards compatibility)
            while (_reliableSendQ.TryDequeue(out _)) { }
            _reliableInFlight = default;
            Interlocked.Exchange(ref _hasReliableInFlight, 0);
            Interlocked.Exchange(ref _reliableSendQCount, 0);

            // Clear Phase 2 control outbox
            while (_ctrlOutbox.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _ctrlOutboxCount, 0);

            // Clear Phase 2 latest transform
            Volatile.Write(ref _latestTransform, null);
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
