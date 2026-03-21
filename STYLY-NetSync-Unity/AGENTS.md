# STYLY-NetSync Unity Client

## Unity Project Structure

- **Unity Version**: Unity 6 required
- **Package Location**: `Packages/com.styly.styly-netsync/` (main package code)
  - `Runtime/` - Core package implementation
    - `NetSyncManager.cs` - Main API entry point
    - `NetSyncAvatar.cs` - Sync component for GameObjects
    - `Internal/` - Manager classes (not public API)
    - `Prefabs/` - Prefab resources (avatars, manager prefab)
  - `Editor/` - Custom inspectors and menu items
  - `Samples~/SimpleDemos/` - Official package sample scenes
- **Test Scenes**:
  - `Assets/Samples_Dev/Demo-01/Demo-01.unity` (main demo scene)
  - `Assets/Samples_Dev/Debug/Debug Scene.unity` (debug testing)
  - `Assets/Samples_Dev/HandTest/` - Hand tracking testing
  - `Assets/Samples_Dev/SimpleSyncCheck/` - Network sync verification
- **Other Assets**:
  - `Assets/Settings/` - Render pipeline and project settings
  - `Assets/XR/` - XR/VR configuration files

## Development

```bash
# Package structure
ls Packages/com.styly.styly-netsync/Runtime/     # Core runtime code
ls Packages/com.styly.styly-netsync/Editor/       # Editor-only scripts
ls Assets/Samples_Dev/                            # Test scenes and examples
```

- **Play Mode**: Test in Unity Editor with demo scenes
- **Network Testing**: Requires running Python server (see parent AGENTS.md)
- **Debug Output**: Enable debug logs in NetSyncManager inspector
- **Package Testing**: Use `Window > Package Manager > In Project` to verify package
- **No automated Unity tests in this repo**: Verify via demo scenes while the Python server is running
- Capture short logs or a screen recording that demonstrates sync state and any fixes

## Architecture

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

## Transform Protocol Notes

- Transform wire format is `protocolVersion=3` only.
- Message IDs for transform traffic are `MSG_CLIENT_POSE=11` and `MSG_ROOM_POSE=12`.
- `Head` is serialized in absolute space; `Right/Left/Virtual` are serialized relative to `Head`.
- Position quantization ranges are per-axis: absolute (`Head`/`Physical`) is `int24 @ 0.01m` (`[-83,886.08m, 83,886.07m]`), and head-relative (`Right`/`Left`/`Virtual`) is `int16 @ 0.005m` (`[-163.84m, 163.835m]`).
- These ranges are wire-encoding limits, not a hard world-size cap; out-of-range encoded values are clamped.
- Idle clients still send heartbeat updates at 1Hz.

## Code Patterns

- **Singleton**: NetSyncManager uses static Instance property
- **Events**: UnityEvent system for callbacks (OnReady, OnRPCReceived)
- **Threading**: ConcurrentQueue for thread-safe message passing
- **Memory**: Reuse objects, avoid unnecessary allocations in Update loops
- **Naming**: Private fields `_camelCase`, public properties `PascalCase`
- **Namespace**: `Styly.NetSync` (public) / `Styly.NetSync.Internal` (internal)

## Common Development Tasks

### Adding New Features
1. Check existing Internal/ managers for similar functionality
2. Follow existing patterns (event-driven, thread-safe messaging)
3. Use NetworkVariableManager for synchronized data
4. Test with both Demo-01 and Debug scenes

### Debugging Network Issues
1. Enable debug logs in NetSyncManager inspector
2. Check Unity Console for connection status
3. Verify Python server is running (see parent AGENTS.md)
4. Test with `Assets/Samples_Dev/Debug/Debug Scene.unity`
