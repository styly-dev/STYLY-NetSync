// ServerDiscoveryManager.cs - Handles automatic server discovery
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;
#if UNITY_IOS || UNITY_VISIONOS
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
#endif

namespace Styly.NetSync
{
    internal class ServerDiscoveryManager
    {
        private UdpClient _discoveryClient;
        private Thread _discoveryThread;
        private bool _isDiscovering;
        private bool _enableDebugLogs;
        private readonly object _lockObject = new object();
        // Round-robin offset to avoid probing the same early IPs first on every retry.
        private static int _roundRobinOffset;
#if UNITY_IOS || UNITY_VISIONOS
        private const string LastServersKey = "StylyNetSync.LastServerIPs";
        private List<string> _cachedLastServerIPs = new List<string>();
#endif
        // Unity main thread synchronization context captured at construction time.
        private readonly SynchronizationContext _unityContext;

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
            // Capture Unity's main thread context to marshal callbacks and logs.
            _unityContext = SynchronizationContext.Current;
        }

        public void SetBeaconPort(int port)
        {
            BeaconPort = port;
        }

        public void StartDiscovery()
        {
            if (_isDiscovering) { return; }

#if UNITY_IOS || UNITY_VISIONOS
            StartTcpScanDiscovery();
#else
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
                                DebugLog("Sent discovery request");
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

                DebugLog($"Started discovery service on port {BeaconPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start discovery: {ex.Message}");
                _isDiscovering = false;
            }
        }

#if UNITY_IOS || UNITY_VISIONOS
        private void StartTcpScanDiscovery()
        {
            try
            {
                _isDiscovering = true;
                // Load last-known servers on the main thread (uses PlayerPrefs)
                _cachedLastServerIPs = new List<string>(LoadLastServerIPs());

                _discoveryThread = new Thread(TcpScanWorker)
                {
                    IsBackground = true,
                    Name = "STYLY_TcpDiscoveryThread"
                };
                _discoveryThread.Start();

                DebugLog($"Started TCP discovery scan on port {BeaconPort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start TCP discovery: {ex.Message}");
                _isDiscovering = false;
            }
        }

        private void TcpScanWorker()
        {
            try
            {
                // 1) Probe previously successful addresses first (cached on main thread)
                foreach (var cached in _cachedLastServerIPs)
                {
                    if (!_isDiscovering) { return; }

                    if (TryProbeOne(cached, BeaconPort, out var dealerPort, out var subPort, out var serverName))
                    {
                        if (!_isDiscovering) { return; }
                        HandleTcpDiscoverySuccess(cached, dealerPort, subPort, serverName);
                        return;
                    }
                }

                // 2) Enumerate local subnet candidates (IPv4 only, limit to /24)
                var myAddresses = new HashSet<string>(GetLocalIPv4Addresses());
                var candidates = new List<string>();
                foreach (var host in EnumerateLocalSubnetHosts(24))
                {
                    if (!_isDiscovering) { return; }
                    if (!myAddresses.Contains(host))
                    {
                        candidates.Add(host);
                    }
                }

                // Apply round-robin offset so subsequent attempts scan a different slice first.
                if (candidates.Count > 1)
                {
                    // Step by typical concurrency to advance the starting window each cycle.
                    int step = 32;
                    int next = Interlocked.Add(ref _roundRobinOffset, step);
                    int start = next % candidates.Count;
                    if (start != 0)
                    {
                        var reordered = new List<string>(candidates.Count);
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            reordered.Add(candidates[(start + i) % candidates.Count]);
                        }
                        candidates = reordered;
                    }
                }

                int foundFlag = 0;
                var tasks = new List<Task>();
                using (var semaphore = new SemaphoreSlim(32))
                {
                    foreach (var candidate in candidates)
                    {
                        if (!_isDiscovering) { break; }
                        if (Interlocked.CompareExchange(ref foundFlag, 1, 1) == 1)
                        {
                            break;
                        }

                        semaphore.Wait();
                        var targetIp = candidate;
                        var task = Task.Run(() =>
                        {
                            try
                            {
                                if (!_isDiscovering) { return; }
                                if (Interlocked.CompareExchange(ref foundFlag, 1, 1) == 1)
                                {
                                    return;
                                }

                                if (TryProbeOne(targetIp, BeaconPort, out var dealerPort, out var subPort, out var serverName))
                                {
                                    if (Interlocked.CompareExchange(ref foundFlag, 1, 0) == 0)
                                    {
                                        HandleTcpDiscoverySuccess(targetIp, dealerPort, subPort, serverName);
                                    }
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });
                        tasks.Add(task);
                    }

                    if (tasks.Count > 0)
                    {
                        try
                        {
                            // Wait up to the configured discovery timeout for this scan cycle.
                            var waitSeconds = DiscoveryTimeout > 0f ? DiscoveryTimeout : 5f;
                            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(waitSeconds));
                        }
                        catch (AggregateException)
                        {
                            // Ignore - failures are logged per task if needed
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"TCP discovery error: {ex.Message}");
            }
            finally
            {
                lock (_lockObject)
                {
                    _isDiscovering = false;
                }
                DebugLog("Stopped TCP discovery scan");
            }
        }

        private void HandleTcpDiscoverySuccess(string ip, int dealerPort, int subPort, string serverName)
        {
            DebugLog($"Discovered server '{serverName}' at tcp://{ip} (dealer:{dealerPort}, sub:{subPort})");
            // Persist and notify on Unity main thread only.
            PostToMain(() =>
            {
                SaveLastServerIP(ip);
                if (OnServerDiscovered != null)
                {
                    OnServerDiscovered.Invoke($"tcp://{ip}", dealerPort, subPort);
                }
            });
            lock (_lockObject)
            {
                _isDiscovering = false;
            }
        }

        private static bool TryProbeOne(string ip, int port, out int dealerPort, out int subPort, out string serverName)
        {
            dealerPort = 0;
            subPort = 0;
            serverName = "Unknown Server";

            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(ip, port);
                    if (!connectTask.Wait(TimeSpan.FromMilliseconds(250)))
                    {
                        return false;
                    }

                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 300;
                        stream.WriteTimeout = 300;

                        var payload = Encoding.UTF8.GetBytes("STYLY-NETSYNC-DISCOVER\n");
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();

                        var buffer = new byte[256];
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                        {
                            return false;
                        }

                        var response = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                        if (!response.StartsWith("STYLY-NETSYNC|", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var parts = response.Split('|');
                        if (parts.Length < 4)
                        {
                            return false;
                        }

                        if (!int.TryParse(parts[1], out dealerPort))
                        {
                            return false;
                        }

                        if (!int.TryParse(parts[2], out subPort))
                        {
                            return false;
                        }

                        serverName = string.IsNullOrWhiteSpace(parts[3]) ? "Unknown Server" : parts[3];
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetLocalIPv4Addresses()
        {
            var results = new List<string>();
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    results.Add(unicast.Address.ToString());
                }
            }

            return results;
        }

        private static IEnumerable<string> EnumerateLocalSubnetHosts(int maxPrefix)
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var ipBytes = unicast.Address.GetAddressBytes();
                    var maskBytes = GetEffectiveMask(unicast, maxPrefix);
                    if (maskBytes == null)
                    {
                        continue;
                    }

                    var networkBytes = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
                    }

                    int prefixLength = GetPrefixLength(maskBytes);
                    int hostBits = 32 - prefixLength;
                    if (hostBits <= 0)
                    {
                        continue;
                    }

                    int totalHosts = 1 << hostBits;
                    int limit = Math.Min(totalHosts - 2, 254);
                    if (limit <= 0)
                    {
                        continue;
                    }
                    for (int host = 1; host <= limit; host++)
                    {
                        var candidate = (byte[])networkBytes.Clone();
                        candidate[3] = (byte)(networkBytes[3] + host);
                        yield return new IPAddress(candidate).ToString();
                    }
                }
            }
        }

        private static byte[] GetEffectiveMask(UnicastIPAddressInformation unicast, int maxPrefix)
        {
            var mask = unicast.IPv4Mask;
            int prefixLength = mask != null ? GetPrefixLength(mask.GetAddressBytes()) : maxPrefix;
            if (prefixLength <= 0)
            {
                prefixLength = maxPrefix;
            }

            int effectivePrefix = prefixLength < maxPrefix ? maxPrefix : prefixLength;
            if (effectivePrefix > 32)
            {
                effectivePrefix = 32;
            }

            return PrefixLengthToMask(effectivePrefix);
        }

        private static int GetPrefixLength(byte[] maskBytes)
        {
            int count = 0;
            foreach (var b in maskBytes)
            {
                for (int bit = 7; bit >= 0; bit--)
                {
                    if ((b & (1 << bit)) != 0)
                    {
                        count++;
                    }
                    else
                    {
                        return count;
                    }
                }
            }

            return count;
        }

        private static byte[] PrefixLengthToMask(int prefixLength)
        {
            uint mask = prefixLength <= 0 ? 0 : uint.MaxValue << (32 - prefixLength);
            return new byte[]
            {
                (byte)((mask >> 24) & 0xFF),
                (byte)((mask >> 16) & 0xFF),
                (byte)((mask >> 8) & 0xFF),
                (byte)(mask & 0xFF)
            };
        }

        private static void SaveLastServerIP(string ip)
        {
            // Persist only the most recently successful IP address.
            try
            {
                if (!string.IsNullOrEmpty(ip))
                {
                    PlayerPrefs.SetString(LastServersKey, ip);
                    PlayerPrefs.Save();
                }
            }
            catch
            {
                // Ignore persistence errors
            }
        }

        private static IEnumerable<string> LoadLastServerIPs()
        {
            // For backward compatibility the API returns IEnumerable<string>,
            // but now we store only a single last IP address.
            string ip;
            try
            {
                ip = PlayerPrefs.GetString(LastServersKey, string.Empty);
            }
            catch
            {
                yield break;
            }

            if (string.IsNullOrEmpty(ip))
            {
                yield break;
            }

            var trimmed = ip.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                yield return trimmed;
            }
        }
#endif

        public void StopDiscovery()
        {
            if (!_isDiscovering) { return; }

            _isDiscovering = false;

            try
            {
                if (_discoveryClient != null)
                {
                    _discoveryClient.Close();
                    _discoveryClient.Dispose();
                }
                _discoveryClient = null;

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

                    // Notify on the main thread only.
                    PostToMain(() =>
                    {
                        if (OnServerDiscovered != null)
                        {
                            OnServerDiscovered.Invoke(serverAddress, dealerPort, subPort);
                        }
                    });

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
            if (!_enableDebugLogs)
            {
                return;
            }
            // Ensure UnityEngine.Debug is called on the main thread only.
            PostToMain(() => { Debug.Log($"[ServerDiscovery] {msg}"); });
        }

        private void PostToMain(Action action)
        {
            if (action == null)
            {
                return;
            }
            // If we captured a Unity SynchronizationContext, use it to marshal back.
            // Otherwise, skip to avoid calling Unity APIs off the main thread.
            var ctx = _unityContext;
            if (ctx != null)
            {
                try { ctx.Post(_ => action(), null); } catch { /* ignore */ }
            }
        }
    }
}
