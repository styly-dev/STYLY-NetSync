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

# Install dependencies
pip install -r requirements.txt

# Run the server
python server.py

# Run with custom ports
python server.py --dealer-port 5555 --pub-port 5556 --beacon-port 9999

# Run without UDP discovery
python server.py --no-beacon

# Run client simulator for load testing
python client_simulator.py --clients 100 --server tcp://localhost --group default_group

# Check for port conflicts (Linux/Mac)
lsof -i :5555
kill <PID>

# Check for port conflicts (Windows)
netstat -ano | findstr :5555
taskkill /PID <PID> /F
```

### Unity Client

- **Unity Version**: Unity 6000.0.48f1 or later (Unity 6)
- **Build**: Use Unity Editor GUI (no command-line build scripts)
- **Package Location**: `STYLY-NetSync-Unity/Packages/com.styly.styly-lbe-multiplayer`
- **Package Name**: com.styly.styly-netsync
- **Dependencies**: 
  - NetMQ 4.0.2 (NuGet)
  - Unity XR Core Utils 2.1.1
- **Demo Scenes**: 
  - `Assets/Samples_Dev/Demo-01/Demo-01.unity` - Main demo
  - `Assets/Samples_Dev/Debug/Debug Scene.unity` - Debug testing

**Note**: No test or lint commands are currently configured for either Python or Unity code.

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

#### 1. Python Server (`server.py`)
- **MultiplayerServer**: Main server class with thread-safe group management
- **Threading Model**:
  - Main thread: Lifecycle management
  - Receive thread: Process client messages
  - Periodic thread: Adaptive broadcasting (2-20Hz) and cleanup
  - Beacon thread: UDP discovery service
- **Client Number System**: Maps device IDs to 2-byte numbers for efficiency
- **Binary Caching**: Server caches client binary data to avoid re-serialization
- **Client Timeout**: 1 second of inactivity = disconnect

#### 2. Unity Client Architecture

**Manager Components** (all internal to NetworkManager, namespace Styly.NetSync):
- **ConnectionManager**: ZeroMQ socket management (DEALER/SUB)
- **PlayerManager**: Spawn/despawn local and remote players
- **TransformSyncManager**: Position synchronization (1-120Hz)
- **RPCManager**: Remote procedure calls
- **NetworkVariableManager**: Synchronized key-value storage (new)
- **MessageProcessor**: Binary message parsing
- **ServerDiscoveryManager**: UDP discovery client

**NetworkObject Component**:
- Attached to synchronized GameObjects
- **Physical Transform**: Local space (X,Z position, Y rotation only)
- **Head/Hands**: World space full 6DOF
- **Virtual Objects**: Array of additional world transforms

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
NetworkManager.Instance.RpcBroadcast("FunctionName", new string[] { "arg1", "arg2" });

// Send to server
NetworkManager.Instance.RpcServer("ServerFunction", new string[] { "data" });

// Send to specific client (using client number)
NetworkManager.Instance.RpcClient(targetClientNo, "DirectMessage", new string[] { "hello" });

// Receive RPCs
NetworkManager.Instance.OnRPCReceived.AddListener((senderClientNo, functionName, args) => {
    switch (functionName)
    {
        case "FunctionName":
            HandleFunction(senderClientNo, args);
            break;
    }
});
```

#### Network Variables System

```csharp
// Set global variable (shared across all clients in group)
NetworkManager.Instance.SetGlobalVariable("gameState", "playing");

// Set client-specific variable (set your own)
NetworkManager.Instance.SetClientVariable("playerScore", "100");

// Set another client's variable (requires their client number)
NetworkManager.Instance.SetClientVariable(targetClientNo, "health", "50");

// Get variables
string gameState = NetworkManager.Instance.GetGlobalVariable("gameState");
string score = NetworkManager.Instance.GetClientVariable(clientNo, "playerScore");

// Listen for changes
NetworkManager.Instance.OnGlobalVariableChanged.AddListener((name, oldVal, newVal) => {
    Debug.Log($"Global var {name} changed: {oldVal} -> {newVal}");
});

NetworkManager.Instance.OnClientVariableChanged.AddListener((clientNo, name, oldVal, newVal) => {
    Debug.Log($"Client {clientNo} var {name} changed: {oldVal} -> {newVal}");
});
```

## Important Implementation Details

### Transform Synchronization
- **Send Rate**: Configurable 1-120Hz (default 10Hz)
- **Physical**: Local coordinates, ground movement only
- **Virtual**: World coordinates, full 6DOF
- **Interpolation**: Smooth movement for remote players

### Connection Handling
- **Discovery**: UDP "STYLY-NETSYNC-DISCOVER" → "STYLY-NETSYNC|dealerPort|subPort|serverName"
- **Reconnection**: 10-second delay on failure
- **Platform-specific**: Special NetMQ cleanup for different platforms
- **Fallback**: Localhost if discovery fails
- **Client Number Assignment**: Server assigns 2-byte IDs to clients, broadcasted to all

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
4. Expose API through NetworkManager

## Key Files Reference

### Server (`STYLY-NetSync-Server/`)
- `server.py`: Main server with group management
- `binary_serializer.py`: Binary protocol implementation
- `client_simulator.py`: Load testing with movement patterns
- `requirements.txt`: Python dependencies (pyzmq 26.4.0)

### Unity Package (`STYLY-NetSync-Unity/Packages/com.styly.styly-lbe-multiplayer/`)
- `Runtime/NetworkManager.cs`: Main singleton entry point
- `Runtime/NetworkObject.cs`: Component for sync
- `Runtime/Internal/BinarySerializer.cs`: Binary protocol
- `Runtime/Internal/DataStructure.cs`: Shared data structures (ClientData, DeviceIdMapping)
- `Runtime/Internal/ConnectionManager.cs`: ZeroMQ handling
- `Runtime/Internal/MessageProcessor.cs`: Message parsing
- `Runtime/Internal/NetworkVariableManager.cs`: Network Variables system
- `Runtime/Internal/PlayerManager.cs`: Player spawn/despawn logic
- `Runtime/Internal/TransformSyncManager.cs`: Transform interpolation
- `Runtime/Internal/RPCManager.cs`: RPC handling
- `Runtime/Internal/ServerDiscoveryManager.cs`: UDP discovery

### Demo Scenes
- `Assets/Samples_Dev/Demo-01/Scripts/ReceiveRPC_to_ChangeColor.cs`: Example RPC receiver