"""Network utility functions for STYLY NetSync."""

import logging
import socket

import psutil

logger = logging.getLogger(__name__)


def get_local_ip_addresses() -> list[str]:
    """
    Get all local IP addresses of the machine from physical network interfaces.

    Filters out virtual interfaces (bridges, VPNs, Docker, etc.) and APIPA addresses
    (169.254.x.x) to show only IP addresses that are likely accessible from external devices.

    Returns:
        list: List of IP addresses as strings

    Example:
        >>> ips = get_local_ip_addresses()
        >>> print(ips)
        ['192.168.1.100', '10.0.0.50']
    """
    ip_addresses = []
    try:
        # Patterns to exclude virtual/bridge interfaces
        # These are common virtual interface prefixes across different platforms
        virtual_prefixes = (
            "bridge",  # VMware, Parallels bridges
            "docker",  # Docker interfaces
            "veth",  # Virtual Ethernet (Docker, LXC)
            "vmnet",  # VMware network
            "vboxnet",  # VirtualBox network
            "virbr",  # libvirt bridge
            "tun",  # VPN tunnels
            "tap",  # Virtual network tap
            "utun",  # macOS VPN tunnels
            "vnic",  # Virtual NIC
            "ppp",  # Point-to-Point Protocol (VPN)
        )

        # Get all network interfaces
        for interface_name, interface_addresses in psutil.net_if_addrs().items():
            # Skip virtual interfaces
            if interface_name.lower().startswith(virtual_prefixes):
                continue

            for address in interface_addresses:
                # Filter for IPv4 addresses only
                if address.family == socket.AF_INET:
                    ip = address.address
                    # Exclude localhost and APIPA addresses (169.254.x.x)
                    if ip != "127.0.0.1" and not ip.startswith("169.254."):
                        ip_addresses.append(ip)
    except Exception as e:
        logger.warning(f"Failed to get local IP addresses: {e}")

    return ip_addresses
