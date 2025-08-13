// ServerDiscoveryManager.cs - Handles automatic server discovery
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Styly.NetSync
{
    public class ServerDiscoveryManager
    {
        private UdpClient _discoveryClient;
        private Thread _discoveryThread;
        private bool _isDiscovering;
        private bool _enableDebugLogs;
        private readonly object _lockObject = new object();

        public bool EnableDiscovery { get; set; } = true;
        public float DiscoveryTimeout { get; set; } = 5f;
        public int BeaconPort { get; set; } = 9999;
        public bool IsDiscovering => _isDiscovering;
        public float DiscoveryInterval { get; set; } = 0.5f; // Send discovery request every 0.5 seconds

        public event Action<string, int, int> OnServerDiscovered;

        public ServerDiscoveryManager(bool enableDebugLogs)
        {
            _enableDebugLogs = enableDebugLogs;
        }

        public void StartDiscovery()
        {
            if (_isDiscovering) { return; }

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

        public void StopDiscovery()
        {
            if (!_isDiscovering) { return; }

            _isDiscovering = false;

            try
            {
                _discoveryClient?.Close();
                _discoveryClient?.Dispose();
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

                    OnServerDiscovered?.Invoke(serverAddress, dealerPort, subPort);

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