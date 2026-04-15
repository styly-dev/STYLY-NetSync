# Replication Protocol v1

This document describes the wire format for the NetSyncObject replication
protocol. It runs alongside the existing client-pose protocol (v3) on the
same ZeroMQ transports. All frames are little-endian and match the byte
layout conventions of `BinarySerializer.cs` / `binary_serializer.py`.

## Conventions

- **Endianness:** little-endian for all multi-byte primitives.
- **Strings (short):** `u8 length` + UTF-8 bytes (max 255 bytes). Used for
  `roomId`, `deviceId`, and error messages that are expected to be short.
- **Strings (long):** `u16 length` + UTF-8 bytes (max 65535 bytes). Not
  used in v1 — reserved.
- **Lists:** `u16 count` + repeated element payloads. The one exception is
  `StateBatch.updates`, which uses `u32 count` to accommodate very large
  rooms.
- **Floats:** `float32` unless otherwise stated. v1 stores transform
  components as float32. A later version may swap the pluggable
  `TransformCodec` out for a quantized codec without changing the
  surrounding framing.
- **Frame prefix:** every message starts with
  `u8 msgType` + `u8 replProtocolVersion` (= 1).

## Message IDs

Replication message IDs occupy the 30–39 range. The existing pose/RPC
protocol uses 1–12 and is unchanged.

| ID | Name                         | Direction         |
|----|------------------------------|-------------------|
| 30 | `MSG_REPL_JOIN_ROOM`         | client -> server  |
| 31 | `MSG_REPL_ROOM_SNAPSHOT`     | server -> client  |
| 32 | `MSG_REPL_OWNERSHIP_REQUEST` | client -> server  |
| 33 | `MSG_REPL_OWNERSHIP_EVENT`   | server -> clients |
| 34 | `MSG_REPL_RESYNC_REQUEST`    | client -> server  |
| 35 | `MSG_REPL_RESYNC_REPLY`      | server -> client  |
| 36 | `MSG_REPL_STATE_BATCH`       | both              |
| 37 | `MSG_REPL_JOIN_REJECT`       | server -> client  |

## Shared primitives

### EntityId

`u64` — 64-bit identifier derived deterministically from the authored
`NetSyncObject._guid`. Scene-load authored entities share the same value
across Unity and Python.

### TransformState

A replicated pose. Only the fields selected by `ChangedMask` appear on the
wire. `TransformState` is encoded/decoded via `TransformCodecV1`; a future
`TransformCodecV2` may introduce quantization.

v1 layout (per field, if present in `changedMask`):

| Field     | Bit | Layout                         |
|-----------|-----|--------------------------------|
| Position  | 0   | `float32 x`, `float32 y`, `float32 z` |
| Rotation  | 1   | `float32 x`, `float32 y`, `float32 z`, `float32 w` |
| Scale     | 2   | `float32 x`, `float32 y`, `float32 z` |

### StateFlags

`u8` bitfield:

| Bit | Name       | Meaning                                          |
|-----|------------|--------------------------------------------------|
| 0   | Keyframe   | Full state; receiver may overwrite stale data.   |
| 1   | Teleport   | Skip smoothing/interpolation on the receiver.    |
| 2   | Heartbeat  | No change; sent to keep authority/liveness.      |

### StateUpdate

Per-entity state carried inside a `StateBatch`.

```
u64  entityId
u32  authorityEpoch
u16  poseSeq
u8   flags          // StateFlags
u8   changedMask    // bit0=Position, bit1=Rotation, bit2=Scale
// Followed by Position / Rotation / Scale payloads in that order,
// only for mask bits that are set. Encoded by TransformCodec.
```

## Control plane

### JOIN_ROOM (30)

```
u8  msgType = 30
u8  replVersion = 1
str roomId       // u8 length + UTF-8
str deviceId     // u8 length + UTF-8
str sceneHash    // u8 length + UTF-8 (SceneHashBuilder output)
```

`sceneHash` lets the server reject clients built against a different
scene (see `JOIN_REJECT`). An empty string means "unknown"; servers may
accept or reject at their discretion.

### ROOM_SNAPSHOT (31)

Initial synchronization for a joining client. The server sends the
authoritative state for every touched entity in the room.

```
u8   msgType = 31
u8   replVersion = 1
str  roomId
u32  baseRoomSeq          // RoomState.nextRoomSeq - 1 at snapshot time
u64  serverTimeUs         // server wall clock in microseconds since Unix epoch
u32  entityCount
repeat entityCount:
    u64 entityId
    u32 authorityEpoch
    u32 ownerShortId      // 0 = server-owned
    u16 poseSeq
    u8  changedMask       // typically Position|Rotation|Scale for snapshot
    // Transform fields per mask, encoded by TransformCodec
```

`baseRoomSeq` anchors subsequent `STATE_BATCH` deltas so a client can
detect out-of-order or missing updates. `serverTimeUs` lets clients age
incoming snapshots against their own clock; cross-process monotonic
clocks would be unusable here, which is why wall-clock is used.

### JOIN_REJECT (37)

Sent by the server in place of a `ROOM_SNAPSHOT` when a `JOIN_ROOM`
cannot be accepted. Clients should surface `reasonText` to the user.

```
u8  msgType = 37
u8  replVersion = 1
str roomId
u8  reasonCode          // JoinRejectReason
str reasonText          // free-form diagnostic; may be empty
```

`JoinRejectReason` values:

| Code | Name                                    |
|------|-----------------------------------------|
| 0    | `SCENE_HASH_MISMATCH`                   |
| 1    | `ROOM_FULL` (reserved)                  |
| 2    | `PROTOCOL_VERSION_MISMATCH` (reserved)  |
| 255  | `UNSPECIFIED`                           |

Unknown reason codes MUST be coerced to `UNSPECIFIED` on the receiver
(log a warning) so forward-compatible clients still surface the reason
text when the server adds new codes.

### OWNERSHIP_REQUEST (32)

```
u8  msgType = 32
u8  replVersion = 1
u64 entityId
u32 requesterShortId
u32 expectedEpoch          // last epoch the requester saw; server may reject
```

### OWNERSHIP_EVENT (33)

Broadcast when ownership changes (accept, reject, or revoke).

```
u8  msgType = 33
u8  replVersion = 1
u64 entityId
u32 newOwnerShortId        // 0 = server-owned / released
u32 newAuthorityEpoch
u8  reason                 // 0=Granted, 1=Rejected, 2=Revoked, 3=Released
```

### RESYNC_REQUEST (34)

Client asks the server for fresh state on one or more entities (e.g. after
detecting a gap in `poseSeq`).

```
u8  msgType = 34
u8  replVersion = 1
u16 entityCount
repeat entityCount:
    u64 entityId
```

### RESYNC_REPLY (35)

```
u8  msgType = 35
u8  replVersion = 1
u16 entityCount
repeat entityCount:
    u64 entityId
    u32 authorityEpoch
    u32 ownerShortId
    u16 poseSeq
    u8  changedMask       // fields present for this entity
    // Transform fields per mask, encoded by TransformCodec
```

## State plane

### STATE_BATCH (36)

A batch of per-entity updates. Client -> server batches carry updates for
entities the client currently owns. Server -> client batches fan-out
relayed updates.

```
u8   msgType = 36
u8   replVersion = 1
u32  serverTick           // monotonic tick counter; 0 when sent by client
u32  updateCount
repeat updateCount:
    StateUpdate           // see above
```

## Versioning

Breaking changes bump `replVersion`. Per project policy, the network
protocol does not require backwards compatibility across releases; server
and clients are deployed together. Non-breaking additive changes (new
`StateFlags` bits, new reserved `changedMask` bits) may appear without a
version bump, provided unknown bits are ignored.
