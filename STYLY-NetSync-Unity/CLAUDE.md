# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Unity Project Structure

This is the Unity client component of STYLY-NetSync, a multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.

- **Unity Version**: Unity 6 required
- **Package Location**: `Packages/com.styly.styly-netsync/` (main package code)
- **Test Scenes**:
  - `Assets/Samples_Dev/Demo-01/Demo-01.unity` (main demo scene)
  - `Assets/Samples_Dev/Debug/Debug Scene.unity` (debug testing)

## Development Commands

### Unity Development
```bash
# Open project in Unity Editor (no CLI build available)
# Use Unity Hub to open: /path/to/STYLY-NetSync-Unity

# Package structure
ls Packages/com.styly.styly-netsync/Runtime/     # Core runtime code
ls Packages/com.styly.styly-netsync/Editor/     # Editor-only scripts
ls Assets/Samples_Dev/                          # Test scenes and examples
```

### Testing and Debugging
- **Play Mode**: Test in Unity Editor with demo scenes
- **Network Testing**: Requires running Python server (see parent CLAUDE.md)
- **Debug Output**: Enable debug logs in NetSyncManager inspector
- **Package Testing**: Use `Window > Package Manager > In Project` to verify package

## Architecture Overview

### Core Components
- **NetSyncManager.cs**: Main singleton entry point and API
- **NetSyncAvatar.cs**: Component for synchronizing GameObjects
- **Internal/** namespace: Core networking implementation (not public API)

### Key Internal Scripts
- **ConnectionManager**: ZeroMQ socket management and threading
- **TransformSyncManager**: Transform synchronization with SendRate upper bound, only-on-change filtering, and 1Hz idle heartbeat
- **RPCManager**: Remote procedure call system with priority-based sending and targeted delivery by client number
- **NetworkVariableManager**: Synchronized key-value storage
- **AvatarManager**: Player spawn/despawn management
- **MessageProcessor**: Binary protocol message handling
- **BinarySerializer**: Protocol v3 pose serialization/deserialization (absolute int24 positions, head-relative int16 positions, and 32-bit smallest-three quaternion compression)
- **ServerDiscoveryManager**: UDP discovery service client
- **HumanPresenceManager**: Collision avoidance visualization
- **OutboundPacket**: Outbound send queue with priority lanes
- **SendOutcome**: Send result type (Sent/Backpressure/Fatal)
- **DataStructure**: Core data structures for networking
- **NetSyncSmoothing**: Transform smoothing and interpolation
- **NetSyncTransformApplier**: Transform application with snapshot interpolation
- **HandPoseNormalizer**: Hand pose normalization utilities
- **Information**: Version handling and compatibility checking
- **NetMQLifecycle**: NetMQ lifecycle management
- **ReusableBufferWriter**: Memory-efficient buffer writing

### Util Scripts
- **BodyTransformSolver**: Body transform solving utilities
- **NetworkUtils**: Network utility functions

### Dependencies (package.json)
- NetMQ (ZeroMQ for Unity)
- Unity XR Core Utils
- Newtonsoft.Json
- STYLY XR Rig
- STYLY Shader Collection URP
- Device ID Provider

### Transform Protocol Notes
- Transform wire format is `protocolVersion=3` only.
- Message IDs for transform traffic are `MSG_CLIENT_POSE=11` and `MSG_ROOM_POSE=12`.
- `Head` is serialized in absolute space; `Right/Left/Virtual` are serialized relative to `Head`.
- Position quantization ranges are per-axis: absolute (`Head`/`Physical`) is `int24 @ 0.01m` (`[-83,886.08m, 83,886.07m]`), and head-relative (`Right`/`Left`/`Virtual`) is `int16 @ 0.005m` (`[-163.84m, 163.835m]`).
- These ranges are wire-encoding limits, not a hard world-size cap; out-of-range encoded values are clamped.
- Idle clients still send heartbeat updates at 1Hz.

## Unity C# Coding Rules (CRITICAL)

- **Never use null propagation (`?.` / `??`) with UnityEngine.Object types**
  - ❌ `return transform?.position;`
  - ✅ `return transform != null ? transform.position : Vector3.zero;`
- **Threading**: All Unity API calls must be on main thread only
- **Namespace**: `Styly.NetSync` (public) / `Styly.NetSync.Internal` (internal)
- **Naming**: Private fields `_camelCase`, public properties `PascalCase`

### Code Patterns
- **Singleton**: NetSyncManager uses static Instance property
- **Events**: UnityEvent system for callbacks (OnReady, OnRPCReceived)
- **Threading**: ConcurrentQueue for thread-safe message passing
- **Memory**: Reuse objects, avoid unnecessary allocations in Update loops

## Common Development Tasks

### Adding New Features
1. Check existing Internal/ managers for similar functionality
2. Follow existing patterns (event-driven, thread-safe messaging)
3. Use NetworkVariableManager for synchronized data
4. Test with both Demo-01 and Debug scenes

### Debugging Network Issues
1. Enable debug logs in NetSyncManager inspector
2. Check Unity Console for connection status
3. Verify Python server is running (see parent CLAUDE.md)
4. Test with `Assets/Samples_Dev/Debug/Debug Scene.unity`

### Testing Changes
1. Verify compilation in Unity Editor (no build errors/warnings)
2. Test in both demo scenes
3. Check Unity Console for runtime errors
4. Test network functionality with server connection

## File Organization

### Package Structure (`Packages/com.styly.styly-netsync/`)
- `Runtime/` - Core package implementation
  - `NetSyncManager.cs` - Main API entry point
  - `NetSyncAvatar.cs` - Sync component for GameObjects
  - `Internal/` - Manager classes (not public API)
  - `Prefabs/` - Prefab resources (avatars, manager prefab)
- `Editor/` - Editor-only functionality
  - Custom inspectors and menu items

### Package Samples (`Packages/com.styly.styly-netsync/Samples~/`)
- `SimpleDemos/` - Official package sample scenes

### Development Assets (`Assets/`)
- `Samples_Dev/Demo-01/` - Main demonstration scene
- `Samples_Dev/Debug/` - Debug testing environment
- `Samples_Dev/HandTest/` - Hand tracking testing
- `Samples_Dev/SimpleSyncCheck/` - Network sync verification
- `Settings/` - Render pipeline and project settings
- `XR/` - XR/VR configuration files
