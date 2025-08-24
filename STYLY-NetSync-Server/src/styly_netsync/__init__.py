"""
STYLY NetSync Package

A Unity-based multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.
Provides real-time synchronization of player positions, hand tracking, and virtual objects using
a Python server with ZeroMQ networking and binary serialization.

Server Classes:
    NetSyncServer: The main server class for multiplayer coordination
    
Client Classes:
    NetSyncManager: Python client manager with Unity NetSync API compatibility
    
Data Types:
    Transform: 6-DOF transform with position and rotation
    ClientTransform: Complete client transform data
    RoomSnapshot: Snapshot of all clients in a room
    
Main Functions:
    main: Command-line entry point for running the server
    create_manager: Create configured NetSync client manager

Examples:
    # Run server as a module
    python -m styly_netsync
    
    # Use server programmatically
    from styly_netsync import NetSyncServer
    server = NetSyncServer(dealer_port=5555, pub_port=5556)
    server.start()
    
    # Use client programmatically
    from styly_netsync import create_manager, Events
    manager = create_manager(room_id="my_room", device_id="python-client")
    manager.connect()
    
    # Get latest room state
    room = manager.latest_room()
    
    # Handle events
    manager.subscribe(Events.RPC_RECEIVED, lambda sender, func, args: print(f"RPC: {func}"))
"""

from .server import NetSyncServer, main, get_version
from .client import NetSyncManager, create_manager
from .types import Transform, ClientTransform, RoomSnapshot, DeviceMapping, RPCMessage, NetworkVariable
from .events import Events

# Export public API
__all__ = [
    # Server
    'NetSyncServer',
    'main', 
    'get_version',
    
    # Client
    'NetSyncManager',
    'create_manager',
    
    # Data Types
    'Transform',
    'ClientTransform', 
    'RoomSnapshot',
    'DeviceMapping',
    'RPCMessage',
    'NetworkVariable',
    
    # Events
    'Events'
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
