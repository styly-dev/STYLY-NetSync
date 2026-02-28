# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Structure

STYLY-NetSync is a Unity multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences. The repository contains two main components:

- **STYLY-NetSync-Server/**: Python server using ZeroMQ for networking
- **STYLY-NetSync-Unity/**: Unity package with client implementation

## XR-Specific Design Considerations

STYLY-NetSync is designed exclusively for XR (VR/MR/AR) applications. This has important implications:

- **High-Frequency Tracking Inputs**: XR head and hand poses can change every frame, but outgoing network data is quantized and filtered before sending.
- **Motion-Adaptive Sending**: Transform sending uses `SendRate` as an upper bound with `Only-on-change` filtering plus a `1Hz` heartbeat while idle.
- **Bandwidth Planning**: Capacity planning should assume motion-dependent traffic with an idle heartbeat floor, not continuous full-payload flow.

## Development Commands

### Python Server (STYLY-NetSync-Server/)

```bash
# Setup
cd STYLY-NetSync-Server
pip install -e .                    # Install package with dependencies
pip install -e ".[dev]"             # Install with dev dependencies (pytest, black, ruff, mypy)

# Running server - CLI entry points (recommended)
styly-netsync-server                # Main server command
styly-netsync-server --dealer-port 5555 --pub-port 5556 --server-discovery-port 9999
styly-netsync-server --no-server-discovery  # Without UDP discovery

# Alternative: module execution
python -m styly_netsync

# Load testing
styly-netsync-simulator --clients 50 --server localhost --room default_room
styly-netsync-simulator --clients 100 --transform-send-rate 60  # Custom rate
python src/styly_netsync/client_simulator.py --clients 50

# Development tools (ALWAYS run before committing)
black src/ tests/                   # Format code
ruff check src/ tests/              # Lint code
mypy src/                           # Type check
pytest                              # Run tests
pytest --cov=src                    # Run tests with coverage

# Complete quality pipeline
black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest

# Port management
lsof -i :5555                       # Check port conflicts (macOS/Linux)
netstat -ano | findstr :5555        # Check port conflicts (Windows)
kill <PID>                          # Kill process using port
```

### Unity Client (STYLY-NetSync-Unity/)

- Build and test using Unity Editor (Unity 6)
- Main package: `Packages/com.styly.styly-netsync/`
- Test scenes: `Assets/Samples_Dev/Demo-01/` and `Assets/Samples_Dev/Debug/`
- Package samples: `Packages/com.styly.styly-netsync/Samples~/SimpleDemos/`

## Architecture Overview

### Core Components

**Server (Python):**
- `server.py` - Main server with multi-threaded architecture and room management
- `binary_serializer.py` - Protocol v3 binary serializer (quantized positions + 32-bit smallest-three quaternion compression + head-relative pose encoding)
- `client.py` - Python client API (`net_sync_manager` class) for non-Unity clients
- `client_simulator.py` - Load testing tool for performance validation
- `rest_bridge.py` - FastAPI-based REST API bridge for external integrations
- `types.py` - Core data types (transform_data, client_transform_data, room_snapshot)
- `nv_sync.py` - Network variable synchronization utilities
- `cli.py` - CLI entry point wrapper

**Unity Client:**
- `NetSyncManager.cs` - Main singleton entry point and API
- `NetSyncAvatar.cs` - Component for avatar synchronization
- `Internal Scripts/` - Core networking components:
  - `ConnectionManager.cs` - ZeroMQ socket management and threading
  - `MessageProcessor.cs` - Binary protocol message handling
  - `TransformSyncManager.cs` - Position/rotation synchronization with SendRate cap, only-on-change filtering, and 1Hz idle heartbeat
  - `RPCManager.cs` - Remote procedure call system with priority-based sending and targeted delivery by client number
  - `NetworkVariableManager.cs` - Synchronized key-value storage
  - `AvatarManager.cs` - Player spawn/despawn management
  - `ServerDiscoveryManager.cs` - UDP discovery service client
  - `HumanPresenceManager.cs` - Collision avoidance visualization
  - `BinarySerializer.cs` - Binary protocol serialization/deserialization
  - `OutboundPacket.cs` - Outbound send queue with priority lanes
  - `SendOutcome.cs` - Result type for send operations (Sent/Backpressure/Fatal)
  - `DataStructure.cs` - Core data structures for networking
  - `NetSyncSmoothing.cs` - Transform smoothing and interpolation
  - `NetSyncTransformApplier.cs` - Transform application with snapshot interpolation
  - `HandPoseNormalizer.cs` - Hand pose normalization utilities
  - `Information.cs` - Version handling and compatibility checking
  - `NetMQLifecycle.cs` - NetMQ lifecycle management
  - `ReusableBufferWriter.cs` - Memory-efficient buffer writing
- `Util Scripts/` - Utility components:
  - `BodyTransformSolver.cs` - Body transform solving utilities
  - `NetworkUtils.cs` - Network utility functions

### Protocol Details

- Uses ZeroMQ with DEALER-ROUTER and PUB-SUB patterns
- Transform protocol is `protocolVersion=3` only (`v2` compatibility removed)
- Transform message IDs are `MSG_CLIENT_POSE=11` and `MSG_ROOM_POSE=12`
- Compact transform encoding: int16 quantized positions, yaw-only physical rotation (`0.1°` units), and 32-bit smallest-three quaternion compression
- `Head` is absolute; `Right/Left/Virtual` transforms are encoded relative to `Head`
- Server relays cached raw client pose bodies to minimize reserialization work
- UDP discovery service for automatic server finding (port 9999)
- Adaptive broadcasting (1-120Hz) with thread-safe design
- Priority-based sending: Control messages (RPC, Network Variables) prioritized over Transform updates
- RPC targeting: RPCs can be broadcast to all clients (default) or sent to specific clients by client number

#### ZeroMQ Important Notes

- **Do NOT use `ZMQ_CONFLATE` option**: This option does not support multipart messages. Since the current implementation uses 2-frame multipart messages (topic + payload), enabling `ZMQ_CONFLATE` will corrupt messages. If you need conflate-like behavior (keeping only the latest message), implement it at the application level instead.

#### Backward Compatibility Policy

- **Network protocol changes do NOT require backward compatibility**: When modifying communication-related code (binary protocol, message formats, serialization), backward compatibility with older versions is not required.
- **Avoid unnecessary breaking changes**: Do not introduce breaking changes to non-networking code without good reason.
- **Always notify the user of breaking changes**: If a code modification breaks backward compatibility, clearly inform the user about the change and its impact.

### Key Architectural Patterns

- **Server**: Multi-threaded (receive, periodic, discovery threads) with group-based room management
- **Unity**: Manager pattern with internal components handling specific concerns
- **Networking**: Binary protocol with efficient serialization
- **Synchronization**: Transform, RPC, Network Variables, Device ID Mapping

## Technology Stack

### Server
- **Python**: 3.11+ required
- **ZeroMQ**: pyzmq >= 26.0.0
- **REST API**: FastAPI >= 0.115.0, uvicorn >= 0.30.0
- **Serialization**: msgpack >= 1.0.5
- **System**: psutil >= 5.9.0

### Unity Client
- **Unity**: Unity 6
- **NetMQ** (ZeroMQ for Unity)
- **Newtonsoft.Json**
- **STYLY XR Rig**
- **Device ID Provider**
- **XR Core Utils**
- **STYLY Shader Collection URP**

## Unity C# Coding Rules (CRITICAL)

- **Never use null propagation (`?.` / `??`) with UnityEngine.Object types**
  - ❌ `return transform?.position;`
  - ✅ `return transform != null ? transform.position : Vector3.zero;`
- All Unity API calls must be on main thread - no background thread access
- Use explicit null checks instead of null propagation operators
- Follow namespace conventions: `Styly.NetSync` (public) / `Styly.NetSync.Internal` (internal)

## Task Completion Checklist

Before submitting code:

**Python Server:**
1. `black src/ tests/` (format)
2. `ruff check src/ tests/` (lint)
3. `mypy src/` (type check)
4. `pytest` (tests)

**Unity Client:**
1. Verify compilation in Unity Editor
2. Test in demo scenes
3. Check console for errors

**Both:**
- Commit with descriptive messages
- For protocol changes, verify full Unity+Server rollout instead of backward compatibility
- Test server-client integration
