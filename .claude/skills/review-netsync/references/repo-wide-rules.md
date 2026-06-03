# Repository-Wide Code Review Rules

## Table of Contents
- ZeroMQ Patterns
- Protocol Details
- Backward Compatibility
- Transform Encoding
- Threading Architecture
- Memory & Performance
- Comments & Documentation
- Version Management
- Git Practices
- CI/CD
- Security

---

## ZeroMQ Patterns

- **NEVER use `ZMQ_CONFLATE`** — corrupts 2-frame multipart messages (topic + payload)
- Use DEALER-ROUTER pattern for client-server communication
- Use PUB-SUB pattern for broadcasting to all clients
- Handle `ZMQError` gracefully with specific catches
- Clean up sockets properly on shutdown

## Protocol Details

A **single** protocol version is supported at a time (no multi-version fallback). The current `PROTOCOL_VERSION` is defined once in `binary_serializer.py` and mirrored in `BinarySerializer.cs` — read it from there rather than relying on a number written here, which goes stale on every wire bump.

### Message Type IDs

Message-type IDs (`MSG_*`) and their values are the source of truth in `binary_serializer.py`'s constants block, mirrored in `BinarySerializer.cs`. When reviewing, read the current set from those files. Verify the invariants rather than memorizing values:

- Every `MSG_*` constant has the same numeric ID on both the Python and C# sides.
- A newly added message type uses a previously-unused ID (no collision, no silent reuse of a retired ID).
- Both directions of a message (serialize on sender, deserialize on receiver) are handled.

### Transform / Pose Encoding

These are encoding *principles*; exact scales/sizes live in the `*_SCALE` constants and serializer code — verify against source:

- **Head**: absolute position + 32-bit smallest-three quaternion.
- **Right/Left/Virtual**: head-relative position + rotation.
- Physical pose and reference-frame / moving-floor-local handling are flagged via pose flags (e.g. `PoseFlags`).
- Position quantization: absolute axes use a wider int (e.g. int24) at a coarser scale; head-relative axes use int16 at a finer scale.
- Out-of-range values are **clamped**, not wrapped.

### Verify in Reviews

- New message types use unused IDs
- Serialization matches deserialization (byte order, field count)
- Both Unity (`BinarySerializer.cs`) and Python (`binary_serializer.py`) updated together for protocol changes

## Backward Compatibility

- **Network protocol changes do NOT require backward compatibility**
- Both client and server must be updated together
- **Non-networking code should avoid unnecessary breaking changes**
- **Always notify users of breaking changes** clearly

## Unity–Python Feature Parity

The Python client (`net_sync_manager` in `client.py`) and the Unity client (`NetSyncManager.cs` + internal managers) must maintain feature parity for all shared functionality.

### Shared Functionality Areas
- Connection management
- Server discovery
- Transform synchronization
- RPC (Remote Procedure Calls)
- Network variables
- Stealth mode

### Counterpart Mappings

| Unity (C#) | Python | Scope |
|---|---|---|
| `NetSyncManager.cs` | `client.py` (`net_sync_manager`) | Main API surface |
| `ConnectionManager.cs` | connection logic in `client.py` | Connect/disconnect/reconnect |
| `ServerDiscoveryManager.cs` | discovery logic in `client.py` | UDP discovery |
| `BinarySerializer.cs` | `binary_serializer.py` | Serialization (CRITICAL parity) |
| `RPCManager.cs` | RPC logic in `client.py` | RPC send/receive |
| `NetworkVariableManager.cs` | NV logic in `client.py` | Network variable sync |
| `server.py` | *(no counterpart)* | Server-only, skip parity check |

### Review Rules
- When a feature is added or modified on one side, check whether the other side needs the same change
- If parity is missing, flag as **WARNING** (or **CRITICAL** for serialization/protocol changes)
- `server.py` is server-only — changes to server-only logic do not require Unity updates

## Threading Architecture

### Server (Python)
- Main Thread: lifecycle, periodic broadcasts, cleanup
- Receive Thread: client message processing (blocking ZMQ recv)
- Periodic Thread: broadcasting updates, maintenance
- Discovery Thread: UDP server discovery service

### Unity Client
- Main Thread: all Unity API, Update loop, event dispatch
- Network Thread: ZMQ socket I/O only (named `STYLY_NetworkThread`)
- Communication: `ConcurrentQueue<T>` from network to main thread

## Memory & Performance

- Object pooling for frequently allocated objects
- Reusable buffers (`ReusableBufferWriter`) for serialization
- Latest-wins transform sending (only most recent per frame)
- Rate limiting for RPC with sliding window
- TTL-based expiry for queued messages
- Priority-based sending: Control (RPC, NV) > Transform

## Comments & Documentation

- **All comments and documentation in English**
- C#: XML documentation (`///`) for public APIs
- Python: Docstrings with Args/Returns/Raises
- Inline comments explain "why", not "what"

## Version Management

- Python version: `pyproject.toml` `[project]` section
- Unity version: `Packages/com.styly.styly-netsync/package.json`
- Format: Semantic versioning (e.g., `0.9.0`)
- Version file: `Runtime/Resources/com.styly.styly-netsync.version.txt`
- Keep versions synchronized across Python and Unity for releases

## Git Practices

- Descriptive commit messages with conventional format: `type: description`
- Types: `feat`, `fix`, `chore`, `refactor`, `test`, `docs`
- **Unity `.meta` files ARE committed** in this repo — commit each `.meta` together with its new/renamed/moved asset or script. Do not author or edit `.meta` files by hand or with an LLM; let Unity generate them. A new asset/script added without its `.meta` (or a `.meta` without its asset) is a defect — flag it.
- Never commit: `__pycache__`, `.env`, credentials, secrets
- Reference issues/PRs: `(#123)` or `(closes #123)`

## CI/CD

- GitHub Actions validates on PR
- Python quality pipeline: `black` + `ruff` + `mypy` + `pytest`
- Release workflow validates semantic versioning
- All checks must pass before merge

## Security

- No secrets in code (API keys, tokens, passwords)
- No `.env` files committed
- Validate external inputs at system boundaries
- Sanitize user-provided data in REST API bridge
