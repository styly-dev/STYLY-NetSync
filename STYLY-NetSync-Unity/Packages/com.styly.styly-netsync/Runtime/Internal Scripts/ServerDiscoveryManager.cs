// ServerDiscoveryManager.cs - Handles automatic server discovery
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Styly.NetSync.Utils;

namespace Styly.NetSync
{
    internal class ServerDiscoveryManager
    {
        private UdpClient _discoveryClient;
        private Thread _discoveryThread;
        private bool _isDiscovering;
        private bool _enableDebugLogs;
        private readonly object _lockObject = new object();

        // Queue for PlayerPrefs operations that must run on main thread
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();

        public bool EnableDiscovery { get; set; } = true;
        public float DiscoveryTimeout { get; set; } = 1f;
        private int _serverDiscoveryPort = 9999;
        public int ServerDiscoveryPort
        {
            get => Volatile.Read(ref _serverDiscoveryPort);
            private set => Volatile.Write(ref _serverDiscoveryPort, value);
        }
        public bool IsDiscovering => _isDiscovering;
        public float DiscoveryInterval { get; set; } = 0.1f; // Send discovery request every 0.1 seconds
        public bool EnableTcpFallback { get; set; } = true; // Allow TCP scanning when UDP broadcast is blocked
        public float TcpFallbackDelaySeconds { get; set; } = 2f; // Delay before starting TCP fallback

        // Parallel scanning configuration for iOS/visionOS
        public int MaxParallelConnections { get; set; } = 20; // Scan up to 20 IPs concurrently
        public int TcpConnectionTimeoutMs { get; set; } = 300; // Reduced timeout for faster scanning

        public event Action<string, int, int> OnServerDiscovered;

        public ServerDiscoveryManager(bool enableDebugLogs)
        {
            _enableDebugLogs = enableDebugLogs;
        }

        public void SetServerDiscoveryPort(int port)
        {
            ServerDiscoveryPort = port;
        }

        public void StartDiscovery()
        {
            if (_isDiscovering) { return; }

#if UNITY_IOS || UNITY_VISIONOS
            // Use TCP scanning for iOS/visionOS platforms
            StartTcpScanDiscovery();
#else
            // Use UDP broadcast for other platforms
            StartUdpBroadcastDiscovery();
#endif
        }

        private void StartUdpBroadcastDiscovery()
        {
            try
            {
                _isDiscovering = true;

                _discoveryClient = new UdpClient();
                _discoveryClient.EnableBroadcast = true;
                _discoveryClient.Client.ReceiveTimeout = 500; // 500ms timeout for responses
                string cachedServerIp = GetCachedServerIp();

                // Start discovery thread that sends requests and waits for responses
                _discoveryThread = new Thread(() =>
                {
                    // First, try to connect to localhost using TCP
                    DebugLog("Attempting to discover server on localhost via TCP...");
                    if (TryTcpDiscovery("127.0.0.1"))
                    {
                        DebugLog("Server discovered on localhost via TCP.");
                        return; // Exit thread if server is found
                    }

                    if (!_isDiscovering) { return; }

                    if (!string.IsNullOrEmpty(cachedServerIp))
                    {
                        DebugLog($"Attempting cached server IP {cachedServerIp} via TCP before broadcast...");
                        if (TryTcpDiscovery(cachedServerIp))
                        {
                            DebugLog("Server discovered from cached IP before UDP broadcast.");
                            return;
                        }
                    }

                    if (!_isDiscovering) { return; }
                    DebugLog("No cached server reachable, proceeding with UDP broadcast.");

                    var discoveryMessage = Encoding.UTF8.GetBytes("STYLY-NETSYNC-DISCOVER");
                    var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, ServerDiscoveryPort);
                    var lastRequestTime = DateTime.MinValue;
                    var fallbackDelaySeconds = Math.Max(0.1f, TcpFallbackDelaySeconds);
                    var fallbackTriggerTime = DateTime.UtcNow.AddSeconds(fallbackDelaySeconds);
                    bool tcpFallbackAttempted = !EnableTcpFallback;

                    while (_isDiscovering)
                    {
                        try
                        {
                            // Send discovery request at specified interval
                            if ((DateTime.Now - lastRequestTime).TotalSeconds >= DiscoveryInterval)
                            {
                                _discoveryClient.Send(discoveryMessage, discoveryMessage.Length, broadcastEndpoint);
                                lastRequestTime = DateTime.Now;
                                DebugLog("Sent UDP discovery request");
                            }

                            // Try to receive response (with timeout)
                            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                            byte[] data = _discoveryClient.Receive(ref remoteEP);
                            ProcessDiscoveryResponse(data, remoteEP);
                        }
                        catch (SocketException ex)
                        {
                            // Timeout is normal - just continue
                            if (ex.SocketErrorCode != SocketError.TimedOut && _isDiscovering)
                            {
                                Debug.LogWarning($"Discovery socket error: {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            if (_isDiscovering) { Debug.LogWarning($"Discovery error: {ex.Message}"); }
                        }

                        if (!tcpFallbackAttempted && _isDiscovering && DateTime.UtcNow >= fallbackTriggerTime)
                        {
                            tcpFallbackAttempted = true;
                            if (TryTcpFallbackDiscovery(cachedServerIp))
                            {
                                return;
                            }
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "STYLY_DiscoveryThread"
                };
                _discoveryThread.Start();

                DebugLog($"Started UDP discovery service on port {ServerDiscoveryPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start UDP discovery: {ex.Message}");
                _isDiscovering = false;
            }
        }

        private void StartTcpScanDiscovery()
        {
            try
            {
                _isDiscovering = true;

                // Read cached IP on main thread before starting background thread
                string cachedServerIp = GetCachedServerIp();

                // Start discovery thread that scans subnet using TCP
                _discoveryThread = new Thread(() =>
                {
                    DebugLog("Starting TCP scan discovery for iOS/visionOS");

                    // First, try to connect to localhost
                    DebugLog("Attempting to discover server on localhost...");
                    if (TryTcpDiscovery("127.0.0.1"))
                    {
                        DebugLog("Server discovered on localhost.");
                        return; // Exit thread if server is found
                    }

                    // If localhost fails, try last known server
                    if (!_isDiscovering) return; // Check if discovery was stopped
                    if (!string.IsNullOrEmpty(cachedServerIp))
                    {
                        DebugLog($"Trying cached server IP: {cachedServerIp}");
                        if (TryTcpDiscovery(cachedServerIp))
                        {
                            return; // Successfully discovered from cache
                        }
                    }

                    // Get local subnet and scan in parallel
                    List<string> ipsToScan = GetSubnetIpAddresses();
                    DebugLog($"Scanning {ipsToScan.Count} IP addresses in subnet with {MaxParallelConnections} parallel connections");

                    PerformParallelTcpScan(ipsToScan);

                    if (_isDiscovering)
                    {
                        Debug.LogWarning("TCP discovery scan completed without finding server");
                        _isDiscovering = false;
                    }
                })
                {
                    IsBackground = true,
                    Name = "STYLY_TcpDiscoveryThread"
                };
                _discoveryThread.Start();

                DebugLog($"Started TCP scan discovery on port {ServerDiscoveryPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start TCP discovery: {ex.Message}");
                _isDiscovering = false;
            }
        }

        private bool TryTcpFallbackDiscovery(string cachedServerIp)
        {
            if (!_isDiscovering)
            {
                return false;
            }

            DebugLog("UDP discovery timed out – starting TCP fallback scan");

            if (!string.IsNullOrEmpty(cachedServerIp))
            {
                DebugLog($"Trying cached server IP during fallback: {cachedServerIp}");
                if (TryTcpDiscovery(cachedServerIp))
                {
                    return true;
                }
            }

            var ipsToScan = GetSubnetIpAddresses();
            if (ipsToScan.Count == 0)
            {
                DebugLog("No subnet IPs available for TCP fallback scan");
                return false;
            }

            PerformParallelTcpScan(ipsToScan);
            return !_isDiscovering ? true : false;
        }

        private void PerformParallelTcpScan(List<string> ipsToScan)
        {
            if (ipsToScan.Count == 0) { return; }

            var pendingScans = new Queue<string>(ipsToScan);
            var activeThreads = new List<Thread>();
            var lockObj = new object();
            bool serverFound = false;

            // Launch worker threads
            for (int i = 0; i < Math.Min(MaxParallelConnections, ipsToScan.Count); i++)
            {
                var workerThread = new Thread(() =>
                {
                    while (true)
                    {
                        string ipToScan = null;

                        // Check if we should stop (server found or discovery stopped)
                        lock (lockObj)
                        {
                            if (serverFound || !_isDiscovering || pendingScans.Count == 0)
                            {
                                return;
                            }
                            ipToScan = pendingScans.Dequeue();
                        }

                        // Attempt TCP discovery
                        if (TryTcpDiscovery(ipToScan))
                        {
                            lock (lockObj)
                            {
                                if (!serverFound)
                                {
                                    serverFound = true;
                                    QueueCacheServerIp(ipToScan);
                                    DebugLog($"Server found at {ipToScan}, stopping other scans");
                                }
                            }
                            return; // Exit this worker thread
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = $"STYLY_TcpScanWorker_{i}"
                };

                workerThread.Start();
                activeThreads.Add(workerThread);
            }

            // Wait for all workers to complete
            foreach (var thread in activeThreads)
            {
                thread.Join();
            }
        }

        private bool TryTcpDiscovery(string ipAddress)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                var connectResult = client.BeginConnect(ipAddress, ServerDiscoveryPort, null, null);
                bool success = connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(TcpConnectionTimeoutMs));

                if (!success)
                {
                    return false;
                }

                client.EndConnect(connectResult);

                // Send discovery request
                var discoveryMessage = Encoding.UTF8.GetBytes("STYLY-NETSYNC-DISCOVER");
                NetworkStream stream = client.GetStream();
                stream.Write(discoveryMessage, 0, discoveryMessage.Length);

                // Read response
                stream.ReadTimeout = 1000;
                byte[] buffer = new byte[1024];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    // Process response
                    var remoteEP = new IPEndPoint(IPAddress.Parse(ipAddress), ServerDiscoveryPort);
                    byte[] responseData = new byte[bytesRead];
                    Array.Copy(buffer, responseData, bytesRead);
                    ProcessDiscoveryResponse(responseData, remoteEP);
                    return true;
                }
            }
            catch (Exception)
            {
                // Connection failed - not the server
            }
            finally
            {
                client?.Close();
            }

            return false;
        }

        private List<string> GetSubnetIpAddresses()
        {
            var ips = new List<string>();

            try
            {
                // Get local IP address and check if it's cellular
                NetworkUtils.GetLocalIpAddressWithType(out string localIp, out bool isCellular);

                if (string.IsNullOrEmpty(localIp))
                {
                    DebugLog("Could not determine local IP address");
                    return ips;
                }

                // Log connection type
                if (isCellular)
                {
                    DebugLog($"Local IP: {localIp} (Cellular)");
                }
                else
                {
                    DebugLog($"Local IP: {localIp} (Wi-Fi/Ethernet)");
                }

                // Do not perform port scanning on cellular data connections
                if (isCellular)
                {
                    Debug.LogWarning("[ServerDiscovery] Cellular data detected - skipping port scan to avoid data usage and performance issues");
                    DebugLog("Port scanning is disabled on cellular connections. Please connect to Wi-Fi.");
                    return ips; // Return empty list
                }

                // Parse IP address and generate subnet IPs (assuming /24 subnet)
                string[] parts = localIp.Split('.');
                if (parts.Length == 4)
                {
                    string subnet = $"{parts[0]}.{parts[1]}.{parts[2]}";

                    // Scan common ranges first (likely server IPs)
                    // Priority: .1, .100-200, .2-99, .201-254
                    var priorityIps = new List<string>();

                    // Router/server common IPs
                    priorityIps.Add($"{subnet}.1");

                    // Mid-range IPs (common for servers)
                    for (int i = 100; i <= 200; i++)
                    {
                        priorityIps.Add($"{subnet}.{i}");
                    }

                    // Lower range
                    for (int i = 2; i <= 99; i++)
                    {
                        priorityIps.Add($"{subnet}.{i}");
                    }

                    // Upper range
                    for (int i = 201; i <= 254; i++)
                    {
                        priorityIps.Add($"{subnet}.{i}");
                    }

                    ips = priorityIps;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Error generating subnet IPs: {ex.Message}");
            }

            return ips;
        }

        private string GetCachedServerIp()
        {
            return PlayerPrefs.GetString("STYLY_NetSync_LastServerIP", "");
        }

        private void QueueCacheServerIp(string ipAddress)
        {
            lock (_mainThreadQueue)
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    PlayerPrefs.SetString("STYLY_NetSync_LastServerIP", ipAddress);
                    PlayerPrefs.Save();
                    DebugLog($"Cached server IP: {ipAddress}");
                });
            }
        }

        public void Update()
        {
            // Process any queued main thread operations
            lock (_mainThreadQueue)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error processing main thread queue: {ex.Message}");
                    }
                }
            }
        }

        public void StopDiscovery()
        {
            if (!_isDiscovering) { return; }

            _isDiscovering = false;

            try
            {
                // Close UDP client if it exists
                if (_discoveryClient != null)
                {
                    _discoveryClient.Close();
                    _discoveryClient.Dispose();
                    _discoveryClient = null;
                }

                // Wait for discovery thread to exit
                if (_discoveryThread != null)
                {
                    WaitThreadExit(_discoveryThread, 1000);
                    _discoveryThread = null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Error stopping discovery: {ex.Message}");
            }

            DebugLog("Stopped discovery");
        }

        private void ProcessDiscoveryResponse(byte[] data, IPEndPoint sender)
        {
            try
            {
                var message = Encoding.UTF8.GetString(data);
                var parts = message.Split('|');

                if (parts.Length >= 3 && parts[0] == "STYLY-NETSYNC")
                {
                    var dealerPort = int.Parse(parts[1]);
                    var subPort = int.Parse(parts[2]);
                    var serverName = parts.Length >= 4 ? parts[3] : "Unknown Server";

                    var serverAddress = $"tcp://{sender.Address}";

                    DebugLog($"Discovered server '{serverName}' at {serverAddress} (dealer:{dealerPort}, sub:{subPort})");

                    QueueCacheServerIp(sender.Address.ToString());

                    if (OnServerDiscovered != null)
                    {
                        OnServerDiscovered.Invoke(serverAddress, dealerPort, subPort);
                    }

                    // Stop sending more discovery requests once we found a server
                    lock (_lockObject)
                    {
                        _isDiscovering = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to process discovery response: {ex.Message}");
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

        private void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[ServerDiscovery] {msg}"); }
        }
    }
}
