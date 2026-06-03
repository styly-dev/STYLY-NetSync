# Protocol Change Impact Checklist

When a diff touches any file in the "Trigger Files" list below, apply this entire checklist to ensure all dependent files are also updated.

## Trigger Files

Any change to these files indicates a protocol change:

- `STYLY-NetSync-Server/src/styly_netsync/binary_serializer.py`
- `STYLY-NetSync-Unity/Packages/com.styly.styly-netsync/Runtime/Internal Scripts/BinarySerializer.cs`
- `STYLY-NetSync-Server/src/styly_netsync/types.py`
- `STYLY-NetSync-Unity/Packages/com.styly.styly-netsync/Runtime/Internal Scripts/DataStructure.cs`

Also triggered by changes to protocol constants: `PROTOCOL_VERSION`, `MSG_CLIENT_POSE`, `MSG_ROOM_POSE`, `MSG_RPC`, `MSG_DEVICE_ID_MAPPING`, `MSG_GLOBAL_VAR_SET`, `MSG_GLOBAL_VAR_SYNC`, `MSG_CLIENT_VAR_SET`, `MSG_CLIENT_VAR_SYNC`, quantization scales (`ABS_POS_SCALE`, `LOCO_POS_SCALE`, `REL_POS_SCALE`, `PHYSICAL_YAW_SCALE`), `MAX_VIRTUAL_TRANSFORMS`, `PoseFlags`, or quaternion compression logic.

---

## CRITICAL: Serialization Parity (Python <-> C#)

When serialization/deserialization code changes on one side, verify the other side matches **byte-for-byte**.

| Python File | C# Counterpart | Check |
|---|---|---|
| `binary_serializer.py` — serialize functions | `BinarySerializer.cs` — Serialize methods | Field order, byte sizes, endianness, quantization scales |
| `binary_serializer.py` — deserialize functions | `BinarySerializer.cs` — Deserialize methods | Same as above in reverse |
| `binary_serializer.py` — quaternion compress/decompress | `BinarySerializer.cs` — quaternion compress/decompress | Identical algorithm, rounding behavior |
| `binary_serializer.py` — constants block (top of file) | `BinarySerializer.cs` — constants block (top of class) | All values must match exactly |
| `types.py` — dataclass fields | `DataStructure.cs` — class fields | Same fields, same semantics |

**How to check:** Read both files and compare constants, field order, byte layout, and encoding logic line by line.

---

## CRITICAL: Test Code

| Test File | What to Check |
|---|---|
| `tests/test_binary_serializer.py` | Add/update tests for changed serialization. Test roundtrip encode-decode. Test edge cases (min/max values, boundary conditions). |
| `tests/test_python_client.py` | Verify client API works with new protocol format. |
| `tests/test_nv_protocol.py` | Update if network variable message format changed. |
| `tests/integration/test_client.py` | Run full integration test with new protocol. Verify end-to-end data flow. |
| `tests/integration/test_stealth_mode.py` | Update if pose flags or stealth handling changed. |

**Required action:** If any serialization function changed, `test_binary_serializer.py` MUST have corresponding test updates. Flag as CRITICAL if missing.

---

## CRITICAL: Simulator Code

| File | What to Check |
|---|---|
| `src/styly_netsync/client_simulator.py` | Uses the message-type constants, the `MESSAGE_TYPE_NAMES` mapping, and the client-transform serialize call. If new message types added, `MESSAGE_TYPE_NAMES` must be updated. If transform format changed, the simulator's movement-pattern generation must match the new format. |

**Required action:** Verify simulator can still generate valid protocol-compliant messages. Flag as CRITICAL if simulator code is not updated alongside protocol changes.

---

## HIGH: Server Message Handling

| File | What to Check |
|---|---|
| `src/styly_netsync/server.py` | Message routing logic uses MSG_* constants. If new message type added, server must handle it. If message format changed, server deserialization and relay must be updated. |
| `src/styly_netsync/adapters.py` | Field name mappings (snake_case <-> camelCase). If fields added/removed/renamed, all adapter functions must be updated: `transform_to_wire()`, `transform_from_wire()`, `client_transform_to_wire()`, `client_transform_from_wire()`. |
| `src/styly_netsync/client.py` | Python client API. If message format changed, client serialization/deserialization calls must be updated. |
| `src/styly_netsync/rest_bridge.py` | REST API bridge. If variable size limits or serialization format changed, REST endpoints must be updated. |

---

## HIGH: Unity Client Message Handling

| File | What to Check |
|---|---|
| `Runtime/Internal Scripts/MessageProcessor.cs` | Message type routing. If new message type added, routing switch must handle it. Version mismatch handling must be checked. |
| `Runtime/Internal Scripts/TransformSyncManager.cs` | If transform sending logic, sequence numbering, or heartbeat behavior changed. |
| `Runtime/Internal Scripts/RPCManager.cs` | If MSG_RPC format changed. |
| `Runtime/Internal Scripts/NetworkVariableManager.cs` | If MSG_GLOBAL_VAR_* or MSG_CLIENT_VAR_* format changed. |
| `Runtime/Internal Scripts/OutboundPacket.cs` | If message priority classification changed. |
| `Runtime/Internal Scripts/Information.cs` | If version compatibility checking logic needs updating. |

---

## CRITICAL: Documentation

Project docs live in `AGENTS.md` files; each `CLAUDE.md` just `@`-imports the sibling `AGENTS.md`, so review and update the `AGENTS.md` content.

| File | What to Update |
|---|---|
| `/AGENTS.md` (root) | Protocol Rules section: version, message IDs, quantization ranges/scales, encoding description, pose/transform layout. Architecture Overview if new components added. |
| `/STYLY-NetSync-Server/AGENTS.md` | Server-side protocol documentation: binary serializer behavior, message-type routing. |
| `/STYLY-NetSync-Unity/AGENTS.md` | Unity client protocol documentation: transform/pose protocol notes, Internal Scripts descriptions. |
| `/README.md` | If major protocol features changed. |

**Required action:** The relevant `AGENTS.md` files MUST be reviewed and updated when the protocol changes. Flag as CRITICAL if documentation updates are missing from the diff.

---

## Review Output Template for Protocol Changes

When protocol changes are detected, add this section to the review output:

```
### Protocol Change Impact Analysis

**Trigger:** [describe what changed, e.g., "New message type MSG_FOO added in binary_serializer.py"]

#### Serialization Parity
- [ ] Python serializer updated
- [ ] C# serializer updated
- [ ] Constants match between Python and C#
- [ ] Byte layout matches between Python and C#

#### Tests
- [ ] test_binary_serializer.py updated with new/modified tests
- [ ] Integration tests updated
- [ ] All tests pass (`pytest --cov=src`)

#### Simulator
- [ ] client_simulator.py updated
- [ ] MESSAGE_TYPE_NAMES updated (if new message type)
- [ ] Simulator generates valid messages in new format

#### Server Handling
- [ ] server.py message routing updated
- [ ] adapters.py field mappings updated (if fields changed)
- [ ] client.py API updated
- [ ] rest_bridge.py updated (if applicable)

#### Unity Client Handling
- [ ] MessageProcessor.cs routing updated
- [ ] Related managers updated (Transform/RPC/NV as applicable)

#### Documentation
- [ ] /AGENTS.md updated
- [ ] /STYLY-NetSync-Server/AGENTS.md updated
- [ ] /STYLY-NetSync-Unity/AGENTS.md updated

#### Missing Updates (CRITICAL)
- [List any files from above that should have been updated but were NOT in the diff]
```

---

## Protocol Constants — Verify Against Source, Do Not Trust Quoted Values

**Do not hardcode protocol values in this checklist** — version, message IDs, scales, and limits change on every wire bump and any list here goes stale. Instead, read the live values from the source and check the invariants below.

**Source of truth:** the constants block near the top of `binary_serializer.py` (Python) and its mirror in `BinarySerializer.cs` (C#).

**Invariants to verify in a review:**

1. **Parity** — every protocol constant present on both sides has an identical value. Read both constants blocks and diff them.
2. **Version bump** — `PROTOCOL_VERSION` is identical on both sides, and was bumped in this same change if the wire format changed in any breaking way.
3. **Message IDs** — every `MSG_*` constant has the same numeric ID on both sides; any newly added message type uses a previously-unused ID (no collisions, no reuse of a retired ID without a version bump).
4. **Quantization** — every `*_SCALE` constant matches between Python and C#, and quantize/dequantize use the same rounding and clamping behavior.
5. **Limits** — virtual-transform and other size limits match between sides. Note that the virtual-transform max is runtime-configurable on the server (`set_max_virtual_transforms` / `_max_virtual_transforms`); `MAX_VIRTUAL_TRANSFORMS` is a legacy alias for the default — check the configured value, not just the constant.

To list the current constants quickly: grep `^MSG_`, `_SCALE`, `PROTOCOL_VERSION`, and the virtual-transform limit in `binary_serializer.py`, then confirm each appears with the same value in `BinarySerializer.cs`.
