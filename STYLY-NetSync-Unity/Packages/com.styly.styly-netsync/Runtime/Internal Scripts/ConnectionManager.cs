// ConnectionManager.cs - Handles network connection management
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using NetMQ;
using NetMQ.Monitoring;
using NetMQ.Sockets;
using UnityEngine;

namespace Styly.NetSync
{
    internal class ConnectionManager
    {
        private static readonly TimeSpan SubReceiveTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SubReconnectDelay = TimeSpan.FromSeconds(1);
        private const int SubReconnectAttempts = 3;

        private DealerSocket _dealerSocket;
        private SubscriberSocket _subSocket;
        private Thread _receiveThread;
        private volatile bool _shouldStop;
        private bool _enableDebugLogs;
        private bool _logNetworkTraffic;
        private bool _connectionError;
        private ServerDiscoveryManager _discoveryManager;
        private string _currentRoomId;
        private int _subDisconnectedFlag;

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
                var lastReceiveTimer = System.Diagnostics.Stopwatch.StartNew();
                _subDisconnectedFlag = 0;
                var monitorEndpoint = string.Empty;

                // Dealer (for sending)
                using var dealer = new DealerSocket();
                dealer.Options.Linger = TimeSpan.Zero;
                dealer.Options.SendHighWatermark = 10;
                dealer.Connect($"{serverAddress}:{dealerPort}");
                _dealerSocket = dealer;

                DebugLog($"[Thread] DEALER connected → {serverAddress}:{dealerPort}");

                // Subscriber (for receiving)
                var sub = CreateSubscriber(serverAddress, subPort, roomId);
                if (sub == null)
                {
                    NotifyConnectionError("Failed to create SUB socket");
                    return;
                }
                using (sub)
                {
                    _subSocket = sub;

                    DebugLog($"[Thread] SUB connected    → {serverAddress}:{subPort}");

                    var subMonitor = AttachSubMonitor(sub, ref monitorEndpoint);

                    // Notify connection established
                    if (OnConnectionEstablished != null)
                    {
                        OnConnectionEstablished.Invoke();
                    }

                    while (!_shouldStop)
                    {
                        if (Interlocked.CompareExchange(ref _subDisconnectedFlag, 0, 0) == 1)
                        {
                            if (!TryReconnectSubscriber(ref sub, ref subMonitor, ref monitorEndpoint, serverAddress, subPort, roomId, lastReceiveTimer))
                            {
                                NotifyConnectionError("SUB socket disconnected");
                                break;
                            }
                        }

                        if (lastReceiveTimer.Elapsed >= SubReceiveTimeout)
                        {
                            if (!TryReconnectSubscriber(ref sub, ref subMonitor, ref monitorEndpoint, serverAddress, subPort, roomId, lastReceiveTimer))
                            {
                                NotifyConnectionError("SUB receive timeout");
                                break;
                            }
                        }

                        // Receive two frames: [topic][payload]. Use string topic and direct comparison.
                        if (!sub.TryReceiveFrameString(TimeSpan.FromMilliseconds(10), out var topic)) { continue; }
                        lastReceiveTimer.Restart();
                        if (!sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(10), out var payload)) { continue; }
                        lastReceiveTimer.Restart();

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

                    if (subMonitor != null)
                    {
                        subMonitor.Stop();
                        subMonitor.Dispose();
                        subMonitor = null;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_shouldStop)
                {
                    Debug.LogError($"Network thread error: {ex.Message}");
                    NotifyConnectionError(ex.Message);
                }
            }
        }

        private void NotifyConnectionError(string reason)
        {
            if (_connectionError) { return; }
            _connectionError = true;
            if (OnConnectionError != null)
            {
                OnConnectionError.Invoke(reason);
            }
        }

        private static SubscriberSocket CreateSubscriber(string serverAddress, int subPort, string roomId)
        {
            try
            {
                var sub = new SubscriberSocket();
                sub.Options.Linger = TimeSpan.Zero;
                sub.Options.ReceiveHighWatermark = 10;
                sub.Connect($"{serverAddress}:{subPort}");
                // Subscribe with topic. Using string here is one-time and acceptable.
                // Note: NetMQ also offers byte[] overloads, but subscription happens once per connection.
                sub.Subscribe(roomId);
                return sub;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to create SUB socket: {ex.Message}");
                return null;
            }
        }

        private NetMQMonitor AttachSubMonitor(SubscriberSocket sub, ref string monitorEndpoint)
        {
            monitorEndpoint = $"inproc://sub-monitor-{Guid.NewGuid():N}";
            var subMonitor = new NetMQMonitor(sub, monitorEndpoint, SocketEvents.Disconnected);
            subMonitor.Disconnected += (_, args) =>
            {
                Interlocked.Exchange(ref _subDisconnectedFlag, 1);
                DebugLog($"[Thread] SUB disconnected → {args.Address}");
            };
            subMonitor.Start();
            return subMonitor;
        }

        private bool TryReconnectSubscriber(
            ref SubscriberSocket sub,
            ref NetMQMonitor subMonitor,
            ref string monitorEndpoint,
            string serverAddress,
            int subPort,
            string roomId,
            System.Diagnostics.Stopwatch lastReceiveTimer)
        {
            if (subMonitor != null)
            {
                subMonitor.Stop();
                subMonitor.Dispose();
                subMonitor = null;
            }

            if (sub != null)
            {
                sub.Dispose();
                sub = null;
            }

            _subSocket = null;
            Interlocked.Exchange(ref _subDisconnectedFlag, 0);

            for (var attempt = 1; attempt <= SubReconnectAttempts; attempt++)
            {
                if (_shouldStop)
                {
                    return false;
                }

                DebugLog($"[Thread] Reconnecting SUB ({attempt}/{SubReconnectAttempts})...");
                sub = CreateSubscriber(serverAddress, subPort, roomId);
                if (sub != null)
                {
                    _subSocket = sub;
                    subMonitor = AttachSubMonitor(sub, ref monitorEndpoint);
                    lastReceiveTimer.Restart();
                    DebugLog($"[Thread] SUB reconnected → {serverAddress}:{subPort}");
                    return true;
                }

                Thread.Sleep(SubReconnectDelay);
            }

            return false;
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
    }
}
