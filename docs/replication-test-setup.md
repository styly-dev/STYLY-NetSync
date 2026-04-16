# Replication Test Setup (Phase 5 manual repro)

The automatic `Assets/Samples_Dev/ReplicationTest/ReplicationTest.unity`
scene was intentionally left for a follow-up PR so this phase commit does
not introduce hand-authored `.unity` / `.meta` churn. Until then, use the
steps below to exercise the new Phase 5 APIs against a running server.

## Prerequisites

- `STYLY-NetSync-Server` running locally (`styly-netsync-server`).
- A scene containing a `NetSyncManager` (any of the existing
  `Assets/Samples_Dev/*` scenes is fine).

## One-off scene authoring

1. In the loaded scene, add a new empty GameObject (name it `NetSync Cube`).
2. Add a `MeshFilter`, `MeshRenderer`, and `NetSyncObject` to it.
   `NetSyncObject` auto-assigns a hidden GUID on validate — open the scene
   hash menu `Tools > STYLY NetSync > Validate Scene GUIDs` to confirm.
3. Add a Canvas with:
   - an **Acquire** button calling `NetSyncObject.RequestOwnership`
   - a **Release** button calling `NetSyncObject.ReleaseOwnership`
   - a **Force Resync** button calling `NetSyncManager.Instance
     .RequestReplicationResync()`
   - a status `TMP_Text` whose `text` property is refreshed each frame from
     `$"owned={cube.IsOwnedByMe} owner={cube.CurrentOwnerClientNo}"`.

## Triggering the new flow

On Play, NetSyncManager wires up the replication subsystem automatically.
Call `NetSyncManager.Instance.JoinReplicationRoom(roomId)` from a custom
hook (or a one-shot `[ContextMenu]`) once the regular connection reports
connected. Observe:

- `OnReplicationJoinStateChanged` fires
  `Disconnected → Joining → Joined`.
- After a grant event, the status text flips to `owned=true`.
- Pressing **Force Resync** emits a `RESYNC_REQUEST`; the matching
  `RESYNC_REPLY` arrives and `OnResyncCompleted` fires on the client.

## Automated coverage

EditMode tests that exercise the same state-machine paths live in
`Packages/com.styly.styly-netsync/Tests/Editor/ReplicationResyncTests.cs`
and `ReplicationClientTests.cs`.
