// ConnectionManager.cs - Handles network connection management
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using NetMQ;
using NetMQ.Sockets;
using UnityEngine;
using Styly.NetSync.Utils;

namespace Styly.NetSync
{
    internal class ConnectionManager : IConnectionManager
    {
        private DealerSocket _dealerSocket;
        private DealerSocket _transformDealerSocket;
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

        // Thread-safe queues for cross-thread logging and callbacks
        // Network thread enqueues; main thread drains via DrainMainThreadActions()
        private readonly ConcurrentQueue<(LogLevel level, string message)> _pendingLogs = new();
        private readonly ConcurrentQueue<Action> _pendingMainThreadActions = new();

        private enum LogLevel { Info, Warning, Error }

        public SubscriberSocket SubSocket => _subSocket;
        public bool IsConnected => _dealerSocket != null && _transformDealerSocket != null && _subSocket != null && !_connectionError;
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

        private readonly MessageProcessor _messageProcessor;
        private readonly string _deviceId;

        private const int TransformRcvHwm = 2;
        private const int ControlSendHwm = 1024;
        private const int ControlReceiveHwm = 1024;
        private const int TransformSendHwm = 2;
        private const long QueueFullWarnIntervalMs = 5000; // Rate limit warning logs to once per 5 seconds
        private long _lastQueueFullWarnMs;

        // Diagnostic counter for tracking dropped transform frames during draining
        private long _droppedTransformFrames;

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
        private OutboundPacket _pendingControl;

        /// <summary>
        /// Latest transform packet to send (latest-wins semantics).
        /// Only the most recent transform is sent; older ones are overwritten.
        /// </summary>
        private OutboundPacket _latestTransform;

        /// <summary>
        /// Per-objectId latest-wins slots for NetSyncObject transform sends.
        /// Each owned object gets its own slot so object updates do not clobber
        /// the avatar slot and multiple owned objects do not clobber each other.
        /// </summary>
        private readonly ConcurrentDictionary<uint, OutboundPacket> _latestObjectTransforms = new();

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

        public ConnectionManager(NetSyncManager netSyncManager, MessageProcessor messageProcessor, bool enableDebugLogs, bool logNetworkTraffic)
        {
            _messageProcessor = messageProcessor;
            _deviceId = netSyncManager != null ? netSyncManager.DeviceId : string.Empty;
            _enableDebugLogs = enableDebugLogs;
            _logNetworkTraffic = logNetworkTraffic;
        }

        public void Connect(string serverAddress, int controlPort, int transformPort, int subPort, string roomId)
        {
            if (_receiveThread != null)
            {
                return; // Already connected
            }

            _currentRoomId = roomId;
            _connectionError = false;
            _shouldStop = false;
            _receiveThread = new Thread(() => NetworkLoop(serverAddress, controlPort, transformPort, subPort, roomId))
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

            // Wait for receive thread to exit first — the using-var blocks inside
            // NetworkLoop will dispose the sockets safely when the thread exits.
            // Do NOT dispose sockets here; the network thread may still be using them.
            if (Thread.CurrentThread != _receiveThread)
            {
                WaitThreadExit(_receiveThread, 1000);
            }

            _subSocket = null;
            _dealerSocket = null;
            _transformDealerSocket = null;
            _receiveThread = null;

            // OS-specific cleanup
            SafeNetMQCleanup();
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

        private bool EnqueueClientHello(string roomId, bool isStealth)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            BinarySerializer.SerializeClientHelloInto(writer, _deviceId, isStealth);
            writer.Flush();
            return TryEnqueueControl(roomId, ms.ToArray());
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
            if (_transformDealerSocket == null) { return; }

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

        /// <summary>
        /// Set the latest transform packet for a specific NetSyncObject.
        /// Uses per-objectId latest-wins semantics so avatar sends and other objects
        /// are not overwritten.
        /// </summary>
        public void SetLatestObjectTransform(string roomId, uint objectId, byte[] payload)
        {
            if (_receiveThread == null || _shouldStop || _connectionError) { return; }
            if (_transformDealerSocket == null) { return; }
            if (objectId == 0u) { return; }

            var packet = new OutboundPacket
            {
                Lane = OutboundLane.ObjectTransform,
                RoomId = roomId,
                Payload = payload,
                EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Attempts = 0
            };

            _latestObjectTransforms[objectId] = packet;
        }

        /// <summary>
        /// Build a ZeroMQ connect address from a server address and port.
        /// Note: NetMQ (pure C# ZeroMQ) does not support the libzmq extended TCP
        /// format (tcp://source:0;dest:port) for source-NIC binding, so we use
        /// the plain tcp://host:port format.
        /// </summary>
        private static string BuildConnectAddress(string serverAddress, int port)
        {
            string host = serverAddress;
            if (host.StartsWith("tcp://"))
            {
                host = host.Substring(6);
            }

            return $"tcp://{host}:{port}";
        }

        private void NetworkLoop(string serverAddress, int controlPort, int transformPort, int subPort, string roomId)
        {
            try
            {
                // Control dealer (RPC, Network Variables, ownership, hello).
                using var controlDealer = new DealerSocket();
                controlDealer.Options.Linger = TimeSpan.Zero;
                controlDealer.Options.SendHighWatermark = ControlSendHwm;
                controlDealer.Options.ReceiveHighWatermark = ControlReceiveHwm;
                var controlAddr = BuildConnectAddress(serverAddress, controlPort);
                controlDealer.Connect(controlAddr);
                _dealerSocket = controlDealer;

                if (_enableDebugLogs) _pendingLogs.Enqueue((LogLevel.Info, $"[ConnectionManager] [Thread] CONTROL DEALER connected → {controlAddr}"));

                // Transform dealer (client and object poses only).
                using var transformDealer = new DealerSocket();
                transformDealer.Options.Linger = TimeSpan.Zero;
                transformDealer.Options.SendHighWatermark = TransformSendHwm;
                var transformAddr = BuildConnectAddress(serverAddress, transformPort);
                transformDealer.Connect(transformAddr);
                _transformDealerSocket = transformDealer;

                if (_enableDebugLogs) _pendingLogs.Enqueue((LogLevel.Info, $"[ConnectionManager] [Thread] TRANSFORM DEALER connected → {transformAddr}"));

                // Subscriber (for receiving)
                using var sub = new SubscriberSocket();
                sub.Options.Linger = TimeSpan.Zero;
                sub.Options.ReceiveHighWatermark = TransformRcvHwm;
                var subAddr = BuildConnectAddress(serverAddress, subPort);
                sub.Connect(subAddr);
                // Subscribe with topic. Using string here is one-time and acceptable.
                // Note: NetMQ also offers byte[] overloads, but subscription happens once per connection.
                sub.Subscribe(roomId);
                _subSocket = sub;

                if (_enableDebugLogs) _pendingLogs.Enqueue((LogLevel.Info, $"[ConnectionManager] [Thread] SUB connected    → {subAddr}"));

                var roomIdBytes = Encoding.UTF8.GetBytes(roomId);

                // Queue connection callback for main thread instead of invoking directly
                _pendingMainThreadActions.Enqueue(() => OnConnectionEstablished?.Invoke());
                EnqueueClientHello(roomId, isStealth: false);

                while (!_shouldStop)
                {
                    var sentAny = FlushOutgoing(controlDealer, transformDealer);
                    bool receivedAny = false;

                    byte[] lastAvatarPayload = null;
                    byte[] lastObjectPayload = null;
                    bool gotAvatar = false;
                    bool gotObject = false;
                    int avatarFramesReceived = 0;

                    while (sub.TryReceiveFrameBytes(TimeSpan.Zero, out var topicBytes))
                    {
                        if (!sub.TryReceiveFrameBytes(TimeSpan.Zero, out var payload)) { break; }

                        // Strict routing: avatar = exact roomId; object = exact roomId + "\0obj".
                        // Anything else (including other rooms sharing a prefix) is ignored.
                        if (IsAvatarTopic(topicBytes, roomIdBytes))
                        {
                            lastAvatarPayload = payload;
                            gotAvatar = true;
                            avatarFramesReceived++;
                        }
                        else if (IsObjectTopic(topicBytes, roomIdBytes))
                        {
                            lastObjectPayload = payload;
                            gotObject = true;
                        }
                    }

                    if (gotAvatar)
                    {
                        if (avatarFramesReceived > 1)
                        {
                            Interlocked.Add(ref _droppedTransformFrames, avatarFramesReceived - 1);
                        }

                        try
                        {
                            _messageProcessor.ProcessIncomingMessage(lastAvatarPayload);
                        }
                        catch (Exception ex)
                        {
                            _pendingLogs.Enqueue((LogLevel.Error, $"Transform parse error: {ex.Message}"));
                        }
                        receivedAny = true;
                    }

                    if (gotObject)
                    {
                        try
                        {
                            _messageProcessor.ProcessIncomingMessage(lastObjectPayload);
                        }
                        catch (Exception ex)
                        {
                            _pendingLogs.Enqueue((LogLevel.Error, $"Object sync parse error: {ex.Message}"));
                        }
                        receivedAny = true;
                    }

                    // DEALER receive: control messages (RPC, NV, ID mapping) from ROUTER
                    // Server sends control messages via ROUTER->DEALER instead of PUB/SUB
                    // Message format: [roomId, payload] (2 frames)
                    while (controlDealer.TryReceiveFrameString(TimeSpan.Zero, out var dealerRoomId))
                    {
                        if (!controlDealer.TryReceiveFrameBytes(TimeSpan.Zero, out var dealerPayload))
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
                                _pendingLogs.Enqueue((LogLevel.Error, $"Control message parse error: {ex.Message}"));
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
                    
                    // Queue log for main thread (Debug.Log* is not safe from background threads)
                    var threadId = Thread.CurrentThread.ManagedThreadId;
                    var endpoint = $"{serverAddress}:{controlPort}/{transformPort}/{subPort}";
                    _pendingLogs.Enqueue((LogLevel.Error,
                        $"[ConnectionManager] Network thread error. " +
                        $"Type={ex_local.GetType().Name} Message={ex_local.Message} " +
                        $"Endpoint={endpoint} ThreadId={threadId} " +
                        $"Time={timestamp}"));

#if NETSYNC_DEBUG_CONNECTION
                    _pendingLogs.Enqueue((LogLevel.Error,
                        $"[ConnectionManager] Stack trace: {ex_local.StackTrace}"));
#endif

                    _connectionError = true;
                    // Queue callback for main thread instead of invoking directly
                    var errorMessage = ex_local.Message;
                    _pendingMainThreadActions.Enqueue(() => OnConnectionError?.Invoke(errorMessage));
                }
            }
        }

        private bool FlushOutgoing(DealerSocket controlDealer, DealerSocket transformDealer)
        {
            bool didWork = false;

            // Priority-based send drain:
            // 1. Drain control messages first (higher priority)
            // 2. Then try to send latest transform (lower priority)

            didWork |= DrainControlSends(controlDealer, maxBatch: 64);
            didWork |= TrySendLatestTransform(transformDealer);
            didWork |= TrySendLatestObjectTransforms(transformDealer);

            return didWork;
        }

        /// <summary>
        /// Try to send the latest transform packet for each owned NetSyncObject.
        /// Uses per-objectId latest-wins semantics: each object has its own slot.
        /// </summary>
        private bool TrySendLatestObjectTransforms(DealerSocket dealer)
        {
            if (_latestObjectTransforms.IsEmpty) { return false; }

            bool didWork = false;

            // ConcurrentDictionary's enumerator returns a moment-in-time snapshot,
            // so removal during iteration is safe. Iterate kvps directly to avoid
            // allocating a separate Keys collection plus a second TryGetValue.
            foreach (var kvp in _latestObjectTransforms)
            {
                var packet = kvp.Value;
                if (packet == null) { continue; }

                if (TrySendDealer(dealer, packet.RoomId, packet.Payload))
                {
                    // Clear only if still the same packet — a newer SetLatestObjectTransform
                    // may have overwritten the slot while we were sending.
                    ((ICollection<KeyValuePair<uint, OutboundPacket>>)_latestObjectTransforms)
                        .Remove(kvp);
                    didWork = true;
                }
                else
                {
                    // Backpressure - keep the packet; stop draining this tick.
                    Interlocked.Increment(ref _wouldBlockCount);
                    break;
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

            while (sent < maxBatch)
            {
                if (_pendingControl == null)
                {
                    if (!_ctrlOutbox.TryDequeue(out _pendingControl))
                    {
                        break;
                    }
                    Interlocked.Decrement(ref _ctrlOutboxCount);
                }

                var packet = _pendingControl;

                // TTL check - skip expired packets
                if (now - packet.EnqueuedAt > CTRL_TTL_SECONDS)
                {
                    _pendingLogs.Enqueue((LogLevel.Warning, $"[ConnectionManager] Control packet expired (TTL {CTRL_TTL_SECONDS}s exceeded)"));
                    _pendingControl = null;
                    continue;
                }

                // Try to send
                if (TrySendDealer(dealer, packet.RoomId, packet.Payload))
                {
                    _pendingControl = null;
                    didWork = true;
                    sent++;
                }
                else
                {
                    // Backpressure - increment counter and stop draining
                    Interlocked.Increment(ref _wouldBlockCount);
                    packet.Attempts++;
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
                // Success - clear the packet using compare-exchange.
                // CompareExchange atomically sets _latestTransform to null ONLY if it still equals 'packet'.
                // This is safe against race conditions: if another thread called SetLatestTransform()
                // during our send, _latestTransform will hold the newer packet (not 'packet'),
                // so CompareExchange will fail and preserve the newer packet for the next send cycle.
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

        // Object topic suffix: "\0obj" (4 bytes). Must match the server's topic builder.
        private static readonly byte[] ObjectTopicSuffix = new byte[] { 0x00, (byte)'o', (byte)'b', (byte)'j' };

        /// <summary>
        /// True iff <paramref name="topicBytes"/> is exactly the room topic (no suffix).
        /// Strict equality — a longer topic sharing the room-ID prefix is rejected.
        /// </summary>
        private static bool IsAvatarTopic(byte[] topicBytes, byte[] roomIdBytes)
        {
            if (topicBytes == null || roomIdBytes == null) { return false; }
            if (topicBytes.Length != roomIdBytes.Length) { return false; }

            for (int i = 0; i < roomIdBytes.Length; i++)
            {
                if (topicBytes[i] != roomIdBytes[i]) { return false; }
            }
            return true;
        }

        /// <summary>
        /// True iff <paramref name="topicBytes"/> is exactly <c>roomId + "\0obj"</c>.
        /// </summary>
        private static bool IsObjectTopic(byte[] topicBytes, byte[] roomIdBytes)
        {
            if (topicBytes == null || roomIdBytes == null) { return false; }
            if (topicBytes.Length != roomIdBytes.Length + ObjectTopicSuffix.Length) { return false; }

            for (int i = 0; i < roomIdBytes.Length; i++)
            {
                if (topicBytes[i] != roomIdBytes[i]) { return false; }
            }
            for (int i = 0; i < ObjectTopicSuffix.Length; i++)
            {
                if (topicBytes[roomIdBytes.Length + i] != ObjectTopicSuffix[i]) { return false; }
            }
            return true;
        }

        private void ClearOutgoingBuffers()
        {
            // Clear control outbox
            while (_ctrlOutbox.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _ctrlOutboxCount, 0);
            _pendingControl = null;

            // Clear latest transform
            Volatile.Write(ref _latestTransform, null);

            // Clear per-object latest transforms
            _latestObjectTransforms.Clear();
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

        public void ProcessDiscoveredServer(string serverAddress, int controlPort, int transformPort, int subPort)
        {
            if (_discoveryManager != null)
            {
                _discoveryManager.StopDiscovery();
            }
            Connect(serverAddress, controlPort, transformPort, subPort, _currentRoomId);
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

        /// <summary>
        /// Drain pending logs and callbacks queued by the network thread.
        /// Must be called from the main Unity thread (e.g., in Update).
        /// </summary>
        public void DrainMainThreadActions()
        {
            while (_pendingLogs.TryDequeue(out var entry))
            {
                switch (entry.level)
                {
                    case LogLevel.Error: Debug.LogError(entry.message); break;
                    case LogLevel.Warning: Debug.LogWarning(entry.message); break;
                    default: Debug.Log(entry.message); break;
                }
            }

            while (_pendingMainThreadActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }
}
