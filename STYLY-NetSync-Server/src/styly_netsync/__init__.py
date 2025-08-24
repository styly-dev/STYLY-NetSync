"""
STYLY NetSync Package

A Unity-based multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.
Provides real-time synchronization of player positions, hand tracking, and virtual objects using
ZeroMQ networking and binary serialization.

Server Classes:
    NetSyncServer: The main server class for multiplayer coordination
    
Client Classes:
    net_sync_manager: Python client manager with pull-based transform consumption
    
Data Types:
    transform: 6-DOF transform with position and rotation
    client_transform: Transform data for a single client
    room_snapshot: Complete snapshot of a room's state
    
Factory Functions:
    create_manager: Create a NetSync client manager
    main: Command-line entry point for running the server

Example Server:
    # Run as a module
    python -m styly_netsync
    
    # Use programmatically
    from styly_netsync import NetSyncServer
    server = NetSyncServer(dealer_port=5555, pub_port=5556)
    server.start()

Example Client:
    # Connect and poll transforms
    from styly_netsync import create_manager
    manager = create_manager(server="tcp://localhost", room="demo")
    manager.start()
    
    # Pull-based transform consumption
    snapshot = manager.latest_room()
    if snapshot:
        for client_no, client_tx in snapshot.clients.items():
            print(f"Client {client_no} head Y: {client_tx.head.pos_y}")
    
    manager.stop()
"""

from .server import NetSyncServer, main, get_version
from .client import net_sync_manager, create_manager
from .types import transform, client_transform, room_snapshot

# Export public API - snake_case only, no C# aliases
__all__ = [
    # Server
    'NetSyncServer',
    'main', 
    'get_version',
    # Client
    'net_sync_manager',
    'create_manager',
    # Data types
    'transform',
    'client_transform', 
    'room_snapshot'
]

# Runtime version access (optional)
# Using importlib.metadata for standard compliance
try:
    from importlib.metadata import version, PackageNotFoundError
    try:
        __version__ = version("styly-netsync-server")
    except PackageNotFoundError:
        __version__ = "unknown"
except ImportError:
    # Python < 3.8 fallback
    __version__ = "unknown"
