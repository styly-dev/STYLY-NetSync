# Phase 6 — Migrate NetSyncAvatar onto ReplicationCore

## Context

Phases 1–5 are committed on `sync-object-01`. The shared `ReplicationCore`
(Unity: `ReplicationClient`, `OwnershipClient`, `PosePublisher`, `PoseBuffer`,
`PoseInterpolator`, `EntityRegistry`; Python: `RoomRegistry`,
`OwnershipArbiter`, `StateRelay`, `SnapshotService`, `ReplicationDispatcher`)
runs in parallel with the legacy avatar pipeline (MSG_CLIENT_POSE=11,
MSG_ROOM_POSE=12, `TransformSyncManager`, `MessageProcessor` → `AvatarManager`,
`BinarySerializer`).

Spec §18: "avatar は別実装にしない" — avatars must live on the same replication
core via a different codec/binding, not as a parallel manager. Phase 6
completes that migration.

**Hard constraint from the user:** do **not** change NetSyncAvatar's public
API. All plumbing moves behind the facade. Per-channel smoothing
(`NetSyncTransformApplier`, `NetSyncSmoothingSettings`) and hand-tracking
/ stealth behaviors must remain functionally identical.

Spec §22 permits removing legacy wire (MSG_CLIENT_POSE / MSG_ROOM_POSE)
because server and clients ship together.

## Design Summary

- **Single publisher, single relay, two codecs.** Generalize the existing
  core via an `IEntityCodec` abstraction. `TransformCodecV1` (scene object)
  stays; add `AvatarCodecV1` (multi-part avatar). Wire up via a per-entity
  `codecId:u8` tag inside STATE_BATCH / ROOM_SNAPSHOT per-entity records.
- **Per-entity binding.** Introduce `IReplicatedBinding` so `PosePublisher`
  can ask each owned entity for its captured state regardless of codec.
  Generic scene objects use the existing transform-capture path; avatars
  get `AvatarReplicatedBinding` that wraps `NetSyncAvatar.GetTransformData()`
  (zero API churn).
- **Per-channel smoothing survives via `ICustomInterpolator`.** An
  `AvatarReplicatedBinding` that implements `ICustomInterpolator` reroutes
  remote snapshot application to the existing `NetSyncTransformApplier`,
  so `NetSyncSmoothingSettings` stays authoritative for avatar smoothing.
  `PoseInterpolator` keeps its generic lerp as the default path; opt-in
  per binding.
- **Server auto-grants avatar ownership at JOIN_ROOM.** No client-side
  acquire round-trip for the avatar. The entity is created server-side
  with owner = joining client_no; snapshot includes it. Disconnect
  releases ownership via the Phase 5 disconnect sweep.
- **Deterministic avatar EntityId.** `sha256(roomId || ":avatar:" ||
  u32_le(clientNo))` folded to a u64 via the existing
  `EntityIdUtils` fold. Stable across a client's lifetime in a room;
  unique across clients; collision risk vs authored NetSyncObject GUIDs
  ≈ 2⁻⁶⁴.
- **Legacy avatar pipeline removed** at the end of Phase 6:
  `TransformSyncManager`, MSG_CLIENT_POSE / MSG_ROOM_POSE constants,
  avatar paths in `BinarySerializer` / `MessageProcessor`, Python avatar
  broadcast loop. `ClientTransformData` / `TransformData` / `PoseFlags`
  DTOs are retained (used by `NetSyncAvatar` internal API and the new
  codec).
- **Unchanged paths:** NetworkVariables, RPC, stealth handshake (remains a
  separate control message — not on the replication plane), hand-tracking
  lost/restored events (`HandPoseNormalizer` → `NetSyncAvatar` local
  dispatch), `NetSyncAvatar.cs` itself, `NetSyncTransformApplier`,
  `NetSyncSmoothingSettings`.

## Schema Delta

Exactly one wire change:

- **STATE_BATCH** and **ROOM_SNAPSHOT** per-entity records gain
  `codecId:u8` prepended to the length-prefixed payload.
  - Codec 1 body = current TransformCodecV1 payload (changedMask + state).
  - Codec 2 body = AvatarCodecV1 payload:
    `poseSeq:u16` + `PoseFlags:u8` + `ΔXROrigin(3×f32)` +
    `ΔPhysical(3×f32)` + head / Lhand / Rhand (each gated by its flag) +
    `virtualCount:u8` + virtuals[≤50], plus `deviceId` length-prefixed
    string included once via the avatar payload.

No changes to envelope of OWNERSHIP_*, RESYNC_*, JOIN_ROOM, JOIN_REJECT,
ROOM_SNAPSHOT top-level fields.

Docs/replication-protocol-v1.md is updated in the final sub-phase.

## Critical Files

- `STYLY-NetSync-Unity/Packages/com.styly.styly-netsync/Runtime/NetSyncAvatar.cs`
  — untouched public API; `Initialize` / `InitializeRemote` internally
  register an `AvatarReplicatedBinding`.
- `.../Runtime/Components/NetSyncObject.cs` — adopt `IReplicatedBinding`
  (trivial).
- `.../Runtime/Core/ITransformCodec.cs` → replaced/augmented by
  `IEntityCodec.cs`; `TransformCodecV1` retrofitted.
- `.../Runtime/Core/AvatarCodecV1.cs` — NEW.
- `.../Runtime/Core/IReplicatedBinding.cs`, `ICustomInterpolator.cs` — NEW.
- `.../Runtime/Core/PosePublisher.cs` — iterate bindings, dispatch by codecId.
- `.../Runtime/Core/PoseInterpolator.cs` — default path unchanged; binding
  can override via `ICustomInterpolator`.
- `.../Runtime/Core/ReplicationMessages.cs`, `MessageCodec.cs` — per-entity
  `codecId:u8` + codec-table dispatch.
- `.../Runtime/Core/EntityRegistry.cs` — add `codecId` to registration.
- `.../Runtime/Internal Scripts/AvatarManager.cs` — spawn hooks register an
  `AvatarReplicatedBinding` on local and remote avatars, compute avatar
  EntityId.
- `.../Runtime/Internal Scripts/AvatarReplicatedBinding.cs` — NEW adapter.
- `.../Runtime/Internal Scripts/ReplicationBridge.cs` — register both codecs
  at startup.
- Removed at end: `.../Runtime/Internal Scripts/TransformSyncManager.cs`,
  avatar paths in `MessageProcessor.cs`, MSG_CLIENT_POSE / MSG_ROOM_POSE
  constants in `BinarySerializer.cs`.

- `STYLY-NetSync-Server/src/styly_netsync/replication/avatar_codec.py` — NEW.
- `.../replication/models.py` — per-entity `codec_id` field; EntityState
  becomes a tagged union over codec payloads.
- `.../replication/state_relay.py` — codec-aware validate/forward; add rule
  that owner of an avatar entity must equal `AvatarIdFromClient(room_id,
  sender.client_no)` to prevent spoofing.
- `.../replication/room_registry.py` — on JOIN_ROOM, auto-create avatar
  entity with `codec_id=2`, auto-grant ownership to joining client_no.
- `.../replication/ownership_arbiter.py` — reject foreign acquire on
  avatar entities (ownership is locked to the originating client_no).
- `.../replication/snapshot_service.py` — include avatar entities in
  sparse snapshots with `codec_id=2` and last-known AvatarEntityState
  (empty-flags placeholder if never published).
- Removed at end: avatar broadcast paths in `server.py`, MSG_CLIENT_POSE /
  MSG_ROOM_POSE constants in `binary_serializer.py`.

- `docs/replication-protocol-v1.md` — codecId tag + AvatarCodecV1 layout.

## Ticket Breakdown (execution order)

All work happens on branch `sync-object-01`. Each sub-phase commits
separately with `feat(netsync): Phase 6.N …`. 6.1 → 6.3 are risk-free
refactors (existing Phase 1–5 tests gate them). 6.4 → 6.7 introduce and
activate the avatar codec. 6.8 removes legacy. 6.9 finalizes docs.

- **6.1 Codec abstraction (no behavior change).** Introduce `IEntityCodec`,
  retrofit `TransformCodecV1`, add per-entity `codecId:u8` tag to
  STATE_BATCH and ROOM_SNAPSHOT with only codec 1 registered. Mirror on
  Python. All Phase 5 tests must pass.
- **6.2 Binding abstraction.** `IReplicatedBinding` + `ICustomInterpolator`;
  generalize `PosePublisher` and `PoseInterpolator`; migrate
  `NetSyncObject` onto `IReplicatedBinding`. Regression test vs Phase 5.
- **6.3 Avatar EntityId helper.** C# + Python implementations; cross-lang
  golden-byte unit tests for determinism.
- **6.4 Server-side avatar auto-creation (no codec yet).** On JOIN_ROOM,
  server creates avatar entity with `codec_id=2`, auto-grants ownership,
  includes in snapshot. Payload is empty/default until 6.5. Feature-flag
  it so partial state is invisible to legacy clients.
- **6.5 AvatarCodecV1.** C# + Python codec; golden-byte round-trip tests
  driven off current `BinarySerializer` outputs so the avatar codec is
  bit-compatible with existing avatar payloads at the semantic level.
  Register codec in dispatcher / state_relay / publisher tables.
- **6.6 AvatarReplicatedBinding + AvatarManager wiring.**
  `AvatarReplicatedBinding` registers itself on local/remote spawn,
  implements `IReplicatedBinding.TryCaptureState` via
  `NetSyncAvatar.GetTransformData()`, and implements `ICustomInterpolator`
  routing remote snapshots to `NetSyncTransformApplier.SetTransformData()`.
  Legacy pipeline still active alongside — dual-publish is permitted for
  this step only; receive-side prefers replication plane when the avatar
  entity exists.
- **6.7 Cut over.** Stop `TransformSyncManager` publishing. Stop
  `MessageProcessor` consuming MSG_CLIENT_POSE / MSG_ROOM_POSE for
  avatars. Python server stops broadcasting legacy avatar frames.
  Legacy code remains compilable for one commit to ease bisect.
- **6.8 Legacy teardown.** Delete `TransformSyncManager`,
  MSG_CLIENT_POSE / MSG_ROOM_POSE constants, avatar paths in
  `BinarySerializer` / `MessageProcessor` / `server.py` / Python
  `binary_serializer`. `ClientTransformData` / `TransformData` /
  `PoseFlags` DTOs remain (used by `NetSyncAvatar` + `AvatarCodecV1`).
- **6.9 Docs + AGENTS.md.** Update replication spec with avatar codec
  layout + codecId tag; update AGENTS.md so internal-script references
  no longer cite TransformSyncManager.

## Feature-Parity Checklist

| Legacy behavior | New location |
|---|---|
| SendRate cap + change-only filtering | `PosePublisher` (already there; binding reports dirty) |
| 1 Hz idle heartbeat | `PosePublisher` (Phase 5 generic) |
| Pose signature caching | `AvatarReplicatedBinding.TryCaptureState` (hash of last sent AvatarEntityState) |
| Stealth handshake | Unchanged control message path |
| Hand tracking lost/restored | `HandPoseNormalizer` → `NetSyncAvatar` (unchanged) |
| XROrigin delta, physical offset, head/hands/virtuals | AvatarCodecV1 (from `NetSyncAvatar.GetTransformData()`, unchanged) |
| Remote per-channel smoothing | `NetSyncTransformApplier` via `ICustomInterpolator` |
| Device ID ↔ clientNo mapping | Phase 3's `ROOM_SNAPSHOT.your_client_no` + `deviceId` carried inside AvatarCodecV1 payload |
| `poseSeq:u16` | Preserved inside AvatarCodecV1 payload |

## Risks

- **AvatarCodecV1 byte-level regression vs BinarySerializer.** Mitigate
  with golden-byte tests on both sides driven from current
  BinarySerializer outputs.
- **Publisher refactor destabilizes NetSyncObject.** Do 6.2 before
  avatars land; Phase 5 tests gate it.
- **Avatar ownership lock on reconnect.** Server's disconnect sweep
  releases the avatar entity; on reconnect with a new clientNo a new
  avatar entity is created. If the grace-window retains clientNo, the
  existing avatar entity survives unchanged.
- **Snapshot size bloat with many avatars.** AvatarCodecV1 respects
  PoseFlags; uninitialized avatars serialize as flags=0 + deviceId/clientNo.
- **`PoseInterpolator` generalization alters NetSyncObject smoothing.**
  Default path unchanged; `ICustomInterpolator` opt-in only.

## Verification

**Automated (must pass before each sub-phase commits):**
- Python: `cd STYLY-NetSync-Server && black src/ tests/ && ruff check src/
  tests/ && mypy --strict src/styly_netsync/replication/ && pytest
  tests/replication/ tests/test_replication_codec.py`.
- Unity: compiles; existing Phase 1–5 EditMode tests remain green.
- New tests in Phase 6:
  - Deterministic avatar EntityId (C# + Python cross-check).
  - AvatarCodecV1 round-trip C# ↔ Python (golden bytes).
  - `PosePublisher` dispatches correct codecId per binding.
  - `state_relay` rejects avatar packets whose `entity_id` ≠ derived id
    for sender's `client_no`.
  - Room snapshot includes avatar entities with codec tag.
  - NetSyncObject regression (generic codec still works after
    generalization).

**Manual QA (end-of-phase gate before 6.8 legacy removal):**
- `Assets/Samples_Dev/Demo-01/Demo-01.unity` — two clients, verify
  physical / head / hands / virtuals visually identical pre/post; inspect
  smoothing quality.
- `Assets/Samples_Dev/HandTest/` — `OnHandTrackingLost` /
  `OnHandTrackingRestored` still fire; remote smoothing unchanged.
- `Assets/Samples_Dev/SimpleSyncCheck/` — latency/heartbeat character.
- `Assets/Samples_Dev/Debug/Debug Scene.unity` — stealth toggle
  round-trip; disconnect/reconnect with clientNo retention.

**Phase-6 exit criteria:**
1. NetSyncAvatar public API identical (diff-equal) to pre-Phase-6.
2. Avatar pose rides STATE_BATCH / ROOM_SNAPSHOT with codec 2.
3. TransformSyncManager and MSG_CLIENT_POSE / MSG_ROOM_POSE removed.
4. All automated tests green on both sides.
5. Spec §16 criterion 6 ("avatar と object が同一 replication core を使う")
   demonstrably satisfied: single publisher + single relay drives both
   entity kinds.
