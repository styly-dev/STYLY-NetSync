---
name: code-review-netsync
description: "Code review skill for the STYLY-NetSync repository (Unity multiplayer framework for LBE XR). Reviews git diff changes against project-specific rules covering Unity C#, Python server, ZeroMQ networking, and binary protocol conventions. Use this skill when the user asks to review code, check changes, do a code review, run /code-review-netsync, or wants feedback on their modifications before committing or merging."
---

# STYLY-NetSync Code Review

Review git diff output against all project-specific coding rules and conventions.

## Review Workflow

1. **Obtain the diff** using `git diff` (unstaged), `git diff --cached` (staged), or `git diff <base>...<head>` (branch comparison)
2. **Classify changed files** into: Unity C# (`.cs`), Python (`.py`), Config/Other
3. **Apply rule checks** per file type (see below and references)
4. **Output findings** grouped by severity: CRITICAL > WARNING > INFO

## Output Format

```
## Code Review: STYLY-NetSync

### Summary
- Files reviewed: N
- Critical: N | Warning: N | Info: N

### CRITICAL
- **[Rule Name]** `file:line` — Description of violation
  - Fix: suggested correction

### WARNING
- ...

### INFO
- ...

### Passed Checks
- List of rules verified with no violations found
```

## Severity Levels

- **CRITICAL**: Will cause bugs, crashes, or protocol errors. Must fix before merge.
- **WARNING**: Violates project conventions or may cause issues. Should fix.
- **INFO**: Style suggestions, minor improvements. Optional.

## Critical Rules (Always Check)

### Unity C# — Null Safety
- **NEVER** use `?.` or `??` on `UnityEngine.Object` types (`Transform`, `GameObject`, `Component`, `MonoBehaviour` subclasses, `Collider`, `Rigidbody`, etc.)
- Use explicit null checks: `x != null ? x.member : fallback`
- Severity: **CRITICAL**

### Unity C# — Thread Safety
- All `UnityEngine` API calls must be on the main thread only
- Background threads must use `ConcurrentQueue<T>` to pass data to main thread
- Check for `new Thread(...)` blocks accessing Unity objects directly
- Severity: **CRITICAL**

### ZeroMQ — No ZMQ_CONFLATE
- `ZMQ_CONFLATE` must never be used — it corrupts 2-frame multipart messages (topic + payload)
- Severity: **CRITICAL**

### Protocol — Version 3 Only
- Protocol version must be 3 (`PROTOCOL_VERSION = 3`)
- No v2 fallback or compatibility code
- Message IDs: `MSG_CLIENT_POSE=11`, `MSG_ROOM_POSE=12`
- Severity: **CRITICAL**

### Language — English Comments
- All comments and documentation must be written in English
- Severity: **WARNING**

## Protocol Change Detection (CRITICAL)

If the diff touches **any** of these trigger files or keywords, a protocol change has occurred:

**Trigger files:**
- `binary_serializer.py` or `BinarySerializer.cs`
- `types.py` or `DataStructure.cs`

**Trigger keywords in diff:** `PROTOCOL_VERSION`, `MSG_CLIENT_POSE`, `MSG_ROOM_POSE`, `MSG_RPC`, `MSG_DEVICE_ID_MAPPING`, `MSG_GLOBAL_VAR_SET`, `MSG_GLOBAL_VAR_SYNC`, `MSG_CLIENT_VAR_SET`, `MSG_CLIENT_VAR_SYNC`, `ABS_POS_SCALE`, `LOCO_POS_SCALE`, `REL_POS_SCALE`, `PHYSICAL_YAW_SCALE`, `MAX_VIRTUAL_TRANSFORMS`, `PoseFlags`, quaternion compression changes.

**When triggered:** Read [references/protocol-change-checklist.md](references/protocol-change-checklist.md) and apply its full checklist. For each item, check whether the corresponding file was updated in the diff. Flag any **missing updates** as CRITICAL violations.

**Key checks in brief:**
1. **Serialization parity** — Python and C# serializers must match byte-for-byte (constants, field order, byte sizes, quantization)
2. **Tests** — `test_binary_serializer.py` MUST have new/updated tests for any serialization change
3. **Simulator** — `client_simulator.py` must generate valid messages in new format; `MESSAGE_TYPE_NAMES` must include any new message type
4. **Server handling** — `server.py` routing, `adapters.py` field mappings, `client.py` API
5. **Unity handling** — `MessageProcessor.cs` routing, related managers (Transform/RPC/NV)
6. **Documentation** — All three `CLAUDE.md` files (root, server, Unity) must reflect protocol changes

## Detailed Rule References

For comprehensive checklists by file type, read the appropriate reference:

- **Unity C# changes**: Read [references/unity-csharp-rules.md](references/unity-csharp-rules.md)
- **Python server changes**: Read [references/python-server-rules.md](references/python-server-rules.md)
- **Repository-wide rules**: Read [references/repo-wide-rules.md](references/repo-wide-rules.md)
- **Protocol changes detected**: Read [references/protocol-change-checklist.md](references/protocol-change-checklist.md)

Load only the references relevant to the files in the diff. If the diff contains only `.cs` files, skip the Python reference and vice versa. Always load the protocol checklist when protocol changes are detected.

## Review Process Detail

1. Run `git diff` (or specified range) and read the full output
2. **Check for protocol changes first** — scan for trigger files/keywords; if found, load protocol checklist
3. For each changed file:
   - Identify file type and load corresponding reference
   - Check every added/modified line against applicable rules
   - Record violations with file path, line number, rule name, and suggested fix
4. **If protocol change detected:**
   - Read the trigger files (both Python and C# serializers) to verify parity
   - Check each item in the protocol checklist against the diff
   - List all files that SHOULD have been updated but are NOT in the diff as CRITICAL "Missing Update"
5. After checking all files, compile the report in the output format above
6. List rules that were checked but had no violations under "Passed Checks"
7. If Python files changed, remind the user to run: `black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest`
8. If C# files changed, remind the user to verify compilation in Unity Editor
