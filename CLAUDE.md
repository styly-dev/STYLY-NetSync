# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

STYLY-NetSync is a Unity-based multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences. It provides real-time synchronization of player positions, hand tracking, and virtual objects using a Python server with ZeroMQ networking and binary serialization.

This is a multi-project repository containing:
- **STYLY-NetSync-Unity**: Unity 6 client package and demo scenes
- **STYLY-NetSync-Server**: Python server for multiplayer coordination

## Common Development Commands

### Python Server

```bash
# Navigate to server directory
cd STYLY-NetSync-Server

# Install as package (recommended for development)
pip install -e .
# Or install with dev dependencies
pip install -e ".[dev]"

# Run server (after installation)
styly-netsync-server                           # Using CLI entry point
python -m styly_netsync                        # As Python module
python src/styly_netsync/server.py             # Direct script

# Server options
styly-netsync-server --dealer-port 5555 --pub-port 5556 --beacon-port 9999
styly-netsync-server --no-beacon               # Disable UDP discovery

# Run client simulator
styly-netsync-simulator --clients 100 --server tcp://localhost --group default_group
python src/styly_netsync/client_simulator.py --clients 100  # Alternative

# Development tools
black src/                 # Format code
ruff check src/            # Lint code  
mypy src/                  # Type check
pytest                     # Run all tests
pytest tests/test_all_run_methods.py  # Test all documented run methods
pytest tests/integration/  # Run integration tests
pytest -k test_stealth     # Run specific test by name pattern

# Run specific test scripts
python tests/integration/test_client.py         # Test basic client functionality
python tests/integration/test_stealth_mode.py   # Test stealth mode feature

# Debug port conflicts
lsof -i :5555              # Linux/Mac: Find process using port
kill -9 <PID>              # Linux/Mac: Kill process
pkill -f styly-netsync     # Linux/Mac: Kill all STYLY processes

netstat -ano | findstr :5555   # Windows: Find process using port
taskkill /PID <PID> /F         # Windows: Kill process
```

### Unity Client

- **Unity Version**: Unity 6000.0.48f1 or later (Unity 6)
- **Build**: Use Unity Editor GUI (no command-line build scripts)
- **Package Location**: `STYLY-NetSync-Unity/Packages/com.styly.styly-netsync`
- **Package Name**: com.styly.styly-netsync
- **Dependencies**: 
  - NetMQ 4.0.2 (NuGet)
  - Unity XR Core Utils 2.1.1
  - Newtonsoft.Json 3.2.1
  - STYLY XR Rig 0.4.1
- **Demo Scenes**: 
  - `Assets/Samples_Dev/Demo-01/Demo-01.unity` - Main demo
  - `Assets/Samples_Dev/Debug/Debug Scene.unity` - Debug testing

## High-Level Architecture

### System Overview

```
Unity Clients (DEALER→ROUTER, SUB←PUB) ←→ Python Server (ROUTER→DEALER, PUB→SUB)
```

The system uses ZeroMQ with binary serialization for efficient networking:
- **DEALER→ROUTER**: Clients send their state to server (port 5555)
- **PUB→SUB**: Server broadcasts combined group state to all clients (port 5556)
- **UDP Discovery**: Optional server discovery service (port 9999)
- **Binary Protocol**: ~60% bandwidth reduction vs JSON

### Key Components

#### 1. Python Server (`src/styly_netsync/server.py`)
- **NetSyncServer**: Main server class with thread-safe group management
- **Threading Model**:
  - Main thread: Lifecycle management
  - Receive thread: Process client messages
  - Periodic thread: Adaptive broadcasting (2-20Hz) and cleanup
  - Beacon thread: UDP discovery service
- **Client Number System**: Maps device IDs to 2-byte numbers for efficiency
- **Binary Caching**: Server caches client binary data to avoid re-serialization
- **Client Timeout**: 1 second of inactivity = disconnect
- **Stealth Mode Support**: Clients can connect without visible avatars (NaN handshake)

#### 2. Unity Client Architecture

**Manager Components** (all internal to NetSyncManager, namespace Styly.NetSync):
- **ConnectionManager**: ZeroMQ socket management (DEALER/SUB)
- **PlayerManager**: Spawn/despawn local and remote players
- **TransformSyncManager**: Position synchronization (1-120Hz)
- **RPCManager**: Remote procedure calls
- **NetworkVariableManager**: Synchronized key-value storage
- **MessageProcessor**: Binary message parsing
- **ServerDiscoveryManager**: UDP discovery client

**NetSyncAvatar Component**:
- Attached to synchronized GameObjects
- **Physical Transform**: Local space (X,Z position, Y rotation only)
- **Head/Hands**: World space full 6DOF
- **Virtual Objects**: Array of additional world transforms

**Stealth Mode**:
- Enable by setting local player prefab to null in NetSyncManager
- Client sends NaN handshake to maintain connection without avatar
- Useful for spectator clients or server-side controllers

#### 3. Binary Protocol

**Message Types**:
```csharp
MSG_CLIENT_TRANSFORM = 1    // Client → Server: Transform update
MSG_GROUP_TRANSFORM = 2     // Server → Clients: Broadcast all transforms
MSG_RPC_BROADCAST = 3       // Client → Server → All: Broadcast RPC
MSG_RPC_SERVER = 4          // Client → Server: Server RPC
MSG_RPC_CLIENT = 5          // Client → Server → Client: Direct RPC
MSG_DEVICE_ID_MAPPING = 6   // Server → Clients: Device ID ↔ client number mappings
MSG_SET_GLOBAL_VAR = 7      // Client → Server: Set global network variable
MSG_SET_CLIENT_VAR = 8      // Client → Server: Set client network variable
MSG_NETWORK_VARS = 9        // Server → Clients: Network variable updates
```

**Optimization**:
- Client sends full device ID (36 bytes)
- Server broadcasts use client numbers (2 bytes)
- Physical transform: 3 floats (X, Z, rotY)
- Virtual transforms: 6 floats (full position + rotation)
- Stealth handshake: NaN values for all transform components

### Threading Model

**Server**:
- Thread-safe with reentrant locks (RLock)
- Context managers for safe lock acquisition
- Atomic operations for group cleanup

**Unity**:
- Main thread: Unity Update/UI
- Network thread: ZeroMQ operations
- ConcurrentQueue for thread-safe message passing

### RPC System

```csharp
// Broadcast to all clients in group
NetSyncManager.Instance.RpcBroadcast("FunctionName", new string[] { "arg1", "arg2" });

// Send to server
NetSyncManager.Instance.RpcServer("ServerFunction", new string[] { "data" });

// Send to specific client (using client number)
NetSyncManager.Instance.RpcClient(targetClientNo, "DirectMessage", new string[] { "hello" });

// Receive RPCs
NetSyncManager.Instance.OnRPCReceived.AddListener((senderClientNo, functionName, args) => {
    switch (functionName)
    {
        case "FunctionName":
            HandleFunction(senderClientNo, args);
            break;
    }
});
```

### Network Variables System

```csharp
// Set global variable (shared across all clients in group)
NetSyncManager.Instance.SetGlobalVariable("gameState", "playing");

// Set client-specific variable
NetSyncManager.Instance.SetClientVariable("playerScore", "100");

// Set another client's variable (requires their client number)
NetSyncManager.Instance.SetClientVariable(targetClientNo, "health", "50");

// Get variables
string gameState = NetSyncManager.Instance.GetGlobalVariable("gameState");
string score = NetSyncManager.Instance.GetClientVariable(clientNo, "playerScore");

// Listen for changes
NetSyncManager.Instance.OnGlobalVariableChanged.AddListener((name, oldVal, newVal) => {
    Debug.Log($"Global var {name} changed: {oldVal} -> {newVal}");
});

NetSyncManager.Instance.OnClientVariableChanged.AddListener((clientNo, name, oldVal, newVal) => {
    Debug.Log($"Client {clientNo} var {name} changed: {oldVal} -> {newVal}");
});

// Check if client is in stealth mode
bool isStealth = NetSyncManager.Instance.IsClientStealthMode(clientNo);
```

## Important Implementation Details

### Transform Synchronization
- **Send Rate**: Configurable 1-120Hz (default 10Hz)
- **Physical**: Local coordinates, ground movement only
- **Virtual**: World coordinates, full 6DOF
- **Interpolation**: Smooth movement for remote players
- **Stealth Mode**: NaN values indicate invisible client

### Connection Handling
- **Discovery**: UDP "STYLY-NETSYNC-DISCOVER" → "STYLY-NETSYNC|dealerPort|subPort|serverName"
- **Reconnection**: 10-second delay on failure
- **Platform-specific**: Special NetMQ cleanup for different platforms
- **Fallback**: Localhost if discovery fails
- **Client Number Assignment**: Server assigns 2-byte IDs to clients, broadcasted to all
- **Handshake**: Periodic transform or stealth handshake to maintain connection

### Performance Optimizations
- Binary protocol with struct packing
- Client number system (2 bytes vs 36 byte device ID)
- Adaptive broadcasting based on group activity
- Binary caching on server side
- Message queue high-water marks

### Current Limitations
- No authentication or encryption (LAN use only)
- No interest management (all clients receive all data)
- No delta compression or prediction
- Simple timeout-based disconnection
- No persistent state storage

## Development Workflow

When modifying the protocol:
1. Update message types in both Unity and Python
2. Ensure binary serialization order matches exactly
3. Test with `client_simulator.py` for compatibility
4. Check thread safety for any shared state

When adding features:
1. Define message type constant in both codebases
2. Implement serialization in BinarySerializer
3. Add handler in MessageProcessor
4. Expose API through NetSyncManager

When debugging issues:
1. Enable debug logs in NetSyncManager inspector
2. Check server logs for connection/group information
3. Use `test_client.py` for isolated testing
4. Monitor with `lsof` or `netstat` for port issues

## Troubleshooting

### Common Issues

**Port Already in Use**:
```bash
# Find and kill process using the port
lsof -i :5555  # Mac/Linux
kill -9 <PID>
```

**Client Not Connecting**:
1. Check firewall settings
2. Verify server is running
3. Confirm correct IP address
4. Test with `test_client.py` first

**Transforms Not Syncing**:
1. Check send rate settings
2. Verify group ID matches
3. Ensure prefabs have NetSyncAvatar component
4. Check debug logs for errors

## Key Files Reference

### Server (`STYLY-NetSync-Server/`)
- `src/styly_netsync/server.py`: Main server with group management
- `src/styly_netsync/binary_serializer.py`: Binary protocol implementation
- `src/styly_netsync/client_simulator.py`: Load testing with movement patterns
- `src/styly_netsync/cli.py`: CLI entry point wrapper
- `src/styly_netsync/__main__.py`: Module execution support
- `requirements.txt`: Python dependencies (pyzmq 26.4.0)
- `pyproject.toml`: Package configuration with dev tools
- `tests/integration/test_client.py`: Basic client integration test
- `tests/integration/test_stealth_mode.py`: Stealth mode feature test
- `tests/test_all_run_methods.py`: Test all documented execution methods

### Unity Package (`STYLY-NetSync-Unity/Packages/com.styly.styly-netsync/`)
- `Runtime/NetSyncManager.cs`: Main singleton entry point
- `Runtime/NetSyncAvatar.cs`: Component for sync
- `Runtime/Internal/BinarySerializer.cs`: Binary protocol
- `Runtime/Internal/DataStructure.cs`: Shared data structures
- `Runtime/Internal/ConnectionManager.cs`: ZeroMQ handling
- `Runtime/Internal/MessageProcessor.cs`: Message parsing
- `Runtime/Internal/NetworkVariableManager.cs`: Network variables
- `Runtime/Internal/PlayerManager.cs`: Player spawn/despawn
- `Runtime/Internal/TransformSyncManager.cs`: Transform interpolation
- `Runtime/Internal/RPCManager.cs`: RPC handling
- `Runtime/Internal/ServerDiscoveryManager.cs`: UDP discovery

### Demo Scenes
- `Assets/Samples_Dev/Demo-01/Scripts/ReceiveRPC_to_ChangeColor.cs`: Example RPC receiver
- `Assets/Samples_Dev/Demo-01/Scripts/SendRPC.cs`: Example RPC sender
- `Assets/Samples_Dev/Debug/DebugMoveAvatar.cs`: Debug avatar movement