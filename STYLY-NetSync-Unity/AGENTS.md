# STYLY-NetSync Unity Client

## Unity Project Structure

- **Unity Version**: Unity 6 required
- **Package Location**: `Packages/com.styly.styly-netsync/`
  - `Runtime/` - Core package implementation
    - `NetSyncManager.cs` - Main API entry point (singleton)
    - `NetSyncAvatar.cs` - Sync component for GameObjects
    - `Internal/` - Manager classes (not public API)
    - `Prefabs/` - Prefab resources
  - `Editor/` - Custom inspectors and menu items
  - `Samples~/SimpleDemos/` - Official sample scenes
- **Test Scenes**:
  - `Assets/Samples_Dev/Demo-01/Demo-01.unity` (main demo)
  - `Assets/Samples_Dev/Debug/Debug Scene.unity` (debug testing)
  - `Assets/Samples_Dev/HandTest/` - Hand tracking testing
  - `Assets/Samples_Dev/SimpleSyncCheck/` - Network sync verification

## Development

- **Play Mode**: Test in Unity Editor with demo scenes
- **Network Testing**: Requires running Python server (see parent AGENTS.md)
- **Debug Output**: Enable debug logs in NetSyncManager inspector
- **No automated Unity tests**: Verify via demo scenes with Python server running
- Capture logs or screen recording to demonstrate sync state

## Key Internal Scripts

- **ConnectionManager**: ZeroMQ socket management and threading
- **TransformSyncManager**: Transform sync with SendRate cap, only-on-change filtering, 1Hz idle heartbeat
- **RPCManager**: Remote procedure calls with priority-based sending and targeted delivery
- **NetworkVariableManager**: Synchronized key-value storage
- **BinarySerializer**: Protocol v3 pose serialization/deserialization
- **MessageProcessor**: Binary protocol message handling
- **AvatarManager**: Player spawn/despawn management

## Debugging Network Issues

1. Enable debug logs in NetSyncManager inspector
2. Check Unity Console for connection status
3. Verify Python server is running
4. Test with `Assets/Samples_Dev/Debug/Debug Scene.unity`
