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

Protocol version: **3 only** (v2 removed).

### Message Type IDs

| ID | Name | Direction |
|----|------|-----------|
| 11 | `MSG_CLIENT_POSE` | Client -> Server |
| 12 | `MSG_ROOM_POSE` | Server -> Clients |
| 3 | `MSG_RPC` | Bidirectional |
| 6 | `MSG_DEVICE_ID_MAPPING` | Server -> Client |
| 7 | `MSG_GLOBAL_VAR_SET` | Client -> Server |
| 8 | `MSG_GLOBAL_VAR_SYNC` | Server -> Client |
| 9 | `MSG_CLIENT_VAR_SET` | Client -> Server |
| 10 | `MSG_CLIENT_VAR_SYNC` | Server -> Client |

### Transform Encoding

- **Head**: Absolute position + 32-bit smallest-three quaternion
- **Physical**: Yaw-only rotation at `0.1 deg` units
- **Right/Left/Virtual**: Head-relative position + rotation
- **Position quantization (absolute)**: `int24 @ 0.01m` per axis
- **Position quantization (relative)**: `int16 @ 0.005m` per axis
- Out-of-range values are **clamped**, not wrapped

### Verify in Reviews

- New message types use unused IDs
- Serialization matches deserialization (byte order, field count)
- Both Unity (`BinarySerializer.cs`) and Python (`binary_serializer.py`) updated together for protocol changes

## Backward Compatibility

- **Network protocol changes do NOT require backward compatibility**
- Both client and server must be updated together
- **Non-networking code should avoid unnecessary breaking changes**
- **Always notify users of breaking changes** clearly

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
- Never commit: `.meta` (Unity auto-generates), `__pycache__`, `.env`, credentials
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
