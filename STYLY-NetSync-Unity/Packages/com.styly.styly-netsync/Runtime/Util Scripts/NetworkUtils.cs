// NetworkUtils.cs - Network utility functions for STYLY NetSync
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace Styly.NetSync.Utils
{
    /// <summary>
    /// Network utility functions for STYLY NetSync.
    /// Provides methods for retrieving local IP addresses and network information.
    /// </summary>
    public static class NetworkUtils
    {
        /// <summary>
        /// Get local IP address with network type detection.
        /// Filters out virtual interfaces (bridges, VPNs, Docker, etc.) and APIPA addresses (169.254.x.x).
        /// Prioritizes Wi-Fi/Ethernet connections over cellular connections.
        /// </summary>
        /// <param name="ipAddress">Output: Local IP address string, or null if not found</param>
        /// <param name="isCellular">Output: True if the detected connection is cellular data</param>
        /// <example>
        /// <code>
        /// NetworkUtils.GetLocalIpAddressWithType(out string ip, out bool isCellular);
        /// if (ip != null)
        /// {
        ///     Debug.Log($"Local IP: {ip}, Cellular: {isCellular}");
        /// }
        /// </code>
        /// </example>
        public static void GetLocalIpAddressWithType(out string ipAddress, out bool isCellular)
        {
            ipAddress = null;
            isCellular = false;

            try
            {
                // Virtual interface prefixes to exclude (matches server.py logic)
                var virtualPrefixes = new[]
                {
                    "bridge",   // VMware, Parallels bridges
                    "docker",   // Docker interfaces
                    "veth",     // Virtual Ethernet (Docker, LXC)
                    "vmnet",    // VMware network
                    "vboxnet",  // VirtualBox network
                    "virbr",    // libvirt bridge
                    "tun",      // VPN tunnels
                    "tap",      // Virtual network tap
                    "utun",     // macOS VPN tunnels
                    "vnic",     // Virtual NIC
                    "ppp",      // Point-to-Point Protocol (VPN)
                };

                // Get all network interfaces
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                var wifiCandidates = new List<string>();
                var cellularCandidates = new List<string>();
                var otherCandidates = new List<string>();

                foreach (var iface in interfaces)
                {
                    // Skip interfaces that are down or loopback
                    if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    // Skip virtual interfaces
                    string ifaceName = iface.Name.ToLower();
                    bool isVirtual = false;
                    foreach (var prefix in virtualPrefixes)
                    {
                        if (ifaceName.StartsWith(prefix))
                        {
                            isVirtual = true;
                            break;
                        }
                    }
                    if (isVirtual)
                    {
                        continue;
                    }

                    // Get IP addresses for this interface
                    var ipProps = iface.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ipStr = addr.Address.ToString();

                            // Skip loopback and APIPA addresses
                            if (ipStr.StartsWith("127.") || ipStr.StartsWith("169.254."))
                            {
                                continue;
                            }

                            // Categorize interfaces by type
                            string ifaceNameLower = iface.Name.ToLower();
                            bool isWifiOrEthernet = ifaceNameLower.StartsWith("en") ||
                                                    ifaceNameLower.StartsWith("eth") ||
                                                    ifaceNameLower.StartsWith("wlan") ||
                                                    iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ||
                                                    iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet;

                            bool isCellularInterface = ifaceNameLower.StartsWith("pdp_ip") ||
                                                    ifaceNameLower.StartsWith("rmnet") ||
                                                    ifaceNameLower.StartsWith("ccmni");

                            if (isWifiOrEthernet)
                            {
                                wifiCandidates.Add(ipStr);
                            }
                            else if (isCellularInterface)
                            {
                                cellularCandidates.Add(ipStr);
                            }
                            else
                            {
                                otherCandidates.Add(ipStr);
                            }
                        }
                    }
                }

                // Prioritize Wi-Fi/Ethernet first, then other, then cellular (last resort)
                if (wifiCandidates.Count > 0)
                {
                    ipAddress = wifiCandidates[0];
                    isCellular = false;
                }
                else if (otherCandidates.Count > 0)
                {
                    ipAddress = otherCandidates[0];
                    isCellular = false;
                }
                else if (cellularCandidates.Count > 0)
                {
                    ipAddress = cellularCandidates[0];
                    isCellular = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkUtils] Error getting local IP: {ex.Message}");
            }
        }

        /// <summary>
        /// Get local IP address without cellular detection.
        /// This is a simplified version that returns only the IP address string.
        /// Filters out virtual interfaces and APIPA addresses, prioritizing Wi-Fi/Ethernet.
        /// </summary>
        /// <returns>Local IP address string, or null if not found</returns>
        /// <example>
        /// <code>
        /// string ip = NetworkUtils.GetLocalIpAddress();
        /// if (ip != null)
        /// {
        ///     Debug.Log($"Local IP: {ip}");
        /// }
        /// </code>
        /// </example>
        public static string GetLocalIpAddress()
        {
            GetLocalIpAddressWithType(out string ip, out _);
            return ip;
        }

        /// <summary>
        /// Get all physical NIC IPv4 addresses.
        /// Filters out virtual interfaces, loopback, APIPA, and cellular.
        /// Returns Wi-Fi/Ethernet candidates + other candidates.
        /// </summary>
        /// <returns>List of local IPv4 address strings</returns>
        public static List<string> GetAllLocalIpAddresses()
        {
            var result = new List<string>();

            try
            {
                var virtualPrefixes = new[]
                {
                    "bridge", "docker", "veth", "vmnet", "vboxnet",
                    "virbr", "tun", "tap", "utun", "vnic", "ppp",
                };

                var interfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (var iface in interfaces)
                {
                    if (iface.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    string ifaceName = iface.Name.ToLower();
                    bool isVirtual = false;
                    foreach (var prefix in virtualPrefixes)
                    {
                        if (ifaceName.StartsWith(prefix))
                        {
                            isVirtual = true;
                            break;
                        }
                    }
                    if (isVirtual) { continue; }

                    // Skip cellular interfaces
                    bool isCellular = ifaceName.StartsWith("pdp_ip") ||
                                      ifaceName.StartsWith("rmnet") ||
                                      ifaceName.StartsWith("ccmni");
                    if (isCellular) { continue; }

                    var ipProps = iface.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ipStr = addr.Address.ToString();
                            if (ipStr.StartsWith("127.") || ipStr.StartsWith("169.254."))
                            {
                                continue;
                            }
                            result.Add(ipStr);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkUtils] Error getting all local IPs: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Auto-detect the local IP the OS would use to reach a destination host.
        /// Uses the UDP connect trick (no packet sent) to query the OS routing table.
        /// Returns null for localhost destinations or on failure.
        /// </summary>
        /// <param name="destHost">Destination host IP or hostname</param>
        /// <param name="destPort">Destination port</param>
        /// <returns>Local source IP string, or null</returns>
        public static string ResolveSourceAddress(string destHost, int destPort)
        {
            if (destHost == "localhost" || destHost == "127.0.0.1" || destHost == "::1")
            {
                return null;
            }

            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(destHost, destPort);
                    var localEp = probe.LocalEndPoint as IPEndPoint;
                    if (localEp != null)
                    {
                        string localIp = localEp.Address.ToString();
                        if (localIp != "0.0.0.0" && localIp != "127.0.0.1")
                        {
                            return localIp;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Routing probe failed - fall back to default
            }

            return null;
        }

        /// <summary>
        /// Compute the directed broadcast address for a given local IP.
        /// Finds the matching network interface, reads its subnet mask,
        /// and computes ip | ~mask. Falls back to 255.255.255.255.
        /// </summary>
        /// <param name="localIp">Local IPv4 address string</param>
        /// <returns>Directed broadcast address string</returns>
        public static string GetBroadcastAddress(string localIp)
        {
            try
            {
                var targetIp = IPAddress.Parse(localIp);
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (var iface in interfaces)
                {
                    if (iface.OperationalStatus != OperationalStatus.Up) { continue; }

                    var ipProps = iface.GetIPProperties();
                    foreach (var unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) { continue; }
                        if (!unicast.Address.Equals(targetIp)) { continue; }

                        var mask = unicast.IPv4Mask;
                        if (mask == null) { continue; }

                        byte[] ipBytes = targetIp.GetAddressBytes();
                        byte[] maskBytes = mask.GetAddressBytes();
                        byte[] bcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                        }
                        return new IPAddress(bcastBytes).ToString();
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to default
            }

            return "255.255.255.255";
        }
    }
}
