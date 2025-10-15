// ServerDiscoveryManager.cs - Handles automatic server discovery
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

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
        public float DiscoveryTimeout { get; set; } = 5f;
        private int _beaconPort = 9999;
        public int BeaconPort
        {
            get => Volatile.Read(ref _beaconPort);
            private set => Volatile.Write(ref _beaconPort, value);
        }
        public bool IsDiscovering => _isDiscovering;
        public float DiscoveryInterval { get; set; } = 0.5f; // Send discovery request every 0.5 seconds

        public event Action<string, int, int> OnServerDiscovered;

        public ServerDiscoveryManager(bool enableDebugLogs)
        {
            _enableDebugLogs = enableDebugLogs;
        }

        public void SetBeaconPort(int port)
        {
            BeaconPort = port;
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

                // Start discovery thread that sends requests and waits for responses
                _discoveryThread = new Thread(() =>
                {
                    var discoveryMessage = Encoding.UTF8.GetBytes("STYLY-NETSYNC-DISCOVER");
                    var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, BeaconPort);
                    var lastRequestTime = DateTime.MinValue;

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
                    }
                })
                {
                    IsBackground = true,
                    Name = "STYLY_DiscoveryThread"
                };
                _discoveryThread.Start();

                DebugLog($"Started UDP discovery service on port {BeaconPort}");
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

                    // Try last known server first
                    if (!string.IsNullOrEmpty(cachedServerIp))
                    {
                        DebugLog($"Trying cached server IP: {cachedServerIp}");
                        if (TryTcpDiscovery(cachedServerIp))
                        {
                            return; // Successfully discovered from cache
                        }
                    }

                    // Get local subnet and scan
                    List<string> ipsToScan = GetSubnetIpAddresses();
                    DebugLog($"Scanning {ipsToScan.Count} IP addresses in subnet");

                    foreach (var ip in ipsToScan)
                    {
                        if (!_isDiscovering) { break; }

                        if (TryTcpDiscovery(ip))
                        {
                            // Queue caching to happen on main thread
                            QueueCacheServerIp(ip);
                            break;
                        }
                    }

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

                DebugLog($"Started TCP scan discovery on port {BeaconPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start TCP discovery: {ex.Message}");
                _isDiscovering = false;
            }
        }

        private bool TryTcpDiscovery(string ipAddress)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient();
                var connectResult = client.BeginConnect(ipAddress, BeaconPort, null, null);
                bool success = connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));

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
                    var remoteEP = new IPEndPoint(IPAddress.Parse(ipAddress), BeaconPort);
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
                if (client != null)
                {
                    client.Close();
                }
            }

            return false;
        }

        private List<string> GetSubnetIpAddresses()
        {
            var ips = new List<string>();

            try
            {
                // Get local IP address
                string localIp = GetLocalIpAddress();
                if (string.IsNullOrEmpty(localIp))
                {
                    DebugLog("Could not determine local IP address");
                    return ips;
                }

                DebugLog($"Local IP: {localIp}");

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

        private string GetLocalIpAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ipStr = ip.ToString();
                        // Skip loopback and APIPA addresses
                        if (!ipStr.StartsWith("127.") && !ipStr.StartsWith("169.254."))
                        {
                            return ipStr;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Error getting local IP: {ex.Message}");
            }

            return null;
        }

        private string GetCachedServerIp()
        {
            return PlayerPrefs.GetString("STYLY_NetSync_LastServerIP", null);
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
