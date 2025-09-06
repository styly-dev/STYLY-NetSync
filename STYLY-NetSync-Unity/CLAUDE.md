# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Unity Project Structure

This is the Unity client component of STYLY-NetSync, a multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.

- **Unity Version**: Unity 6000.0.48f1 or later required
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

### Key Internal Managers
- **ConnectionManager**: ZeroMQ socket management and threading
- **TransformSyncManager**: Position/rotation synchronization (1-120Hz)
- **RPCManager**: Remote procedure call system
- **NetworkVariableManager**: Synchronized key-value storage
- **AvatarManager**: Player spawn/despawn management
- **MessageProcessor**: Binary protocol message handling

### Dependencies (package.json)
- NetMQ 4.0.2 (ZeroMQ for Unity)
- Unity XR Core Utils 2.1.1
- Newtonsoft.Json 3.2.1
- STYLY XR Rig 0.4.7
- STYLY Shader Collection URP 0.0.5

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
  - `Prefab/` - Prefab resources (avatars, manager prefab)
- `Editor/` - Editor-only functionality
  - Custom inspectors and menu items

### Development Assets (`Assets/`)
- `Samples_Dev/Demo-01/` - Main demonstration scene
- `Samples_Dev/Debug/` - Debug testing environment
- `Settings/` - Render pipeline and project settings
- `XR/` - XR/VR configuration files