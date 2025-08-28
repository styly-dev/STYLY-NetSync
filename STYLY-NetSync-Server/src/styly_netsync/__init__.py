"""
STYLY NetSync Server Package

A Unity-based multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.
Provides real-time synchronization of player positions, hand tracking, and virtual objects using
a Python server with ZeroMQ networking and binary serialization.

Main Classes:
    NetSyncServer: The main server class for multiplayer coordination
    net_sync_manager: Python client for connecting to NetSync servers

Examples:
    # Run server via CLI (after installation)
    styly-netsync-server
    styly-netsync-simulator

    # Use server programmatically
    from styly_netsync import NetSyncServer
    server = NetSyncServer(dealer_port=5555, pub_port=5556)
    server.start()

    # Use client programmatically
    from styly_netsync import net_sync_manager
    manager = net_sync_manager(server="tcp://localhost", room="my_room")
    manager.start()
    snapshot = manager.get_room_transform_data()
"""

from .client import net_sync_manager
from .server import NetSyncServer, get_version
from .types import client_transform_data, room_transform_data, transform_data

# Export public API
__all__ = [
    # Server API
    "NetSyncServer",
    "get_version",
    # Client API
    "net_sync_manager",
    # Data types
    "transform_data",
    "client_transform_data",
    "room_transform_data",
]

# Runtime version access (optional)
# Using importlib.metadata for standard compliance
try:
    from importlib.metadata import PackageNotFoundError, version

    try:
        __version__ = version("styly-netsync-server")
    except PackageNotFoundError:
        __version__ = "unknown"
except ImportError:
    # Python < 3.8 fallback
    __version__ = "unknown"
