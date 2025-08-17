"""
STYLY NetSync Server Package

A Unity-based multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.
Provides real-time synchronization of player positions, hand tracking, and virtual objects using
a Python server with ZeroMQ networking and binary serialization.

Main Classes:
    NetSyncServer: The main server class for multiplayer coordination
    
Main Functions:
    main: Command-line entry point for running the server

Example:
    # Run as a module
    python -m styly_netsync
    
    # Use programmatically
    from styly_netsync import NetSyncServer
    server = NetSyncServer(dealer_port=5555, pub_port=5556)
    server.start()
"""

from .server import NetSyncServer, main

# Export public API
__all__ = [
    'NetSyncServer',
    'main'
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
