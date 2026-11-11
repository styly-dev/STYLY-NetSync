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

## Repository Context

- Also check `../AGENTS.md` and `../STYLY-NetSync-Server/`.
- Keep Unity/Python behavior aligned for networking, protocol, RPC, Network Variable, discovery, and connection changes.

## Development

- **Play Mode**: Test in Unity Editor with demo scenes
- **Network Testing**: Requires running Python server (see parent AGENTS.md)
- **Debug Output**: Enable debug logs in NetSyncManager inspector
- **Automated tests**: Unity Test Framework suites live in
  `Packages/com.styly.styly-netsync/Tests/` (run via Test Runner window or CLI)
  - EditMode: serializer/manager unit tests, incl. cross-language golden bytes
  - PlayMode: offline tests plus Python-server integration tests; the
    integration tests launch the local server via `uv` and self-ignore when
    `uv` is not on PATH — set `STYLY_NETSYNC_TESTS_REQUIRE_SERVER=1` (CI) to
    fail instead of skip so coverage cannot silently disappear
- **Manual verification**: Demo scenes with Python server running; capture logs
  or screen recording to demonstrate sync state

## Key Internal Scripts

- **ConnectionManager**: ZeroMQ socket management and threading
- **TransformSyncManager**: Transform sync with SendRate cap, only-on-change filtering, 1Hz idle heartbeat
- **RPCManager**: Remote procedure calls with priority-based sending and targeted delivery
- **NetworkVariableManager**: Synchronized key-value storage
- **BinarySerializer**: Protocol v8 pose serialization/deserialization
- **MessageProcessor**: Binary protocol message handling
- **AvatarManager**: Player spawn/despawn management

## Debugging Network Issues

1. Enable debug logs in NetSyncManager inspector
2. Check Unity Console for connection status
3. Verify Python server is running
4. Test with `Assets/Samples_Dev/Debug/Debug Scene.unity`

## Adding a New UnityEvent Field

`UnityEventDrawer` swallows `[Tooltip]`, so event fields need a custom-editor overlay.

- Add `[Tooltip("…")]` and initialize at declaration (`= new UnityEvent<…>()`). No `[Header]` on event fields.
- In the custom editor, register the field name in `EventProperties` (and `EventGroupHeaders` if it starts a new section). Existing `DrawEventWithTooltip` handles the rest.

Non-UnityEvent fields: plain `[Tooltip]` is enough.
