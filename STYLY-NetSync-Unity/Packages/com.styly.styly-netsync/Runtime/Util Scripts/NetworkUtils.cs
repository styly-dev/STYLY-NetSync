// NetworkUtils.cs - Network utility functions for STYLY NetSync
using System;
using System.Collections.Generic;
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
    }
}
