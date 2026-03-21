# STYLY-NetSync

## Repository Structure

STYLY-NetSync is a Unity multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.

- **STYLY-NetSync-Server/**: Python server using ZeroMQ for networking
- **STYLY-NetSync-Unity/**: Unity package with client implementation

## XR-Specific Design Considerations

STYLY-NetSync is designed exclusively for XR (VR/MR/AR) applications:

- **Motion-Adaptive Sending**: Transform sending uses `SendRate` as an upper bound with `Only-on-change` filtering plus a `1Hz` heartbeat while idle.
- **Bandwidth Planning**: Assume motion-dependent traffic with an idle heartbeat floor, not continuous full-payload flow.

## Quick Start

### Python Server

```bash
cd STYLY-NetSync-Server
pip install -e ".[dev]"
styly-netsync-server
# Quality pipeline (run before committing)
black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest --cov=src
```

### Unity Client

- Open `STYLY-NetSync-Unity/` in Unity 6
- Main package: `Packages/com.styly.styly-netsync/`
- Test scenes: `Assets/Samples_Dev/Demo-01/` and `Assets/Samples_Dev/Debug/`

## Architecture Overview

- **Server**: Multi-threaded Python (receive, periodic, discovery threads) with ZeroMQ DEALER-ROUTER + PUB-SUB and group-based room management
- **Unity Client**: Manager pattern with internal components (connection, transform sync, RPC, network variables, avatars)
- **Protocol**: Binary v3 with quantized positions and smallest-three quaternion compression
- **Technology**: Python 3.11+ / pyzmq / FastAPI / msgpack (server), Unity 6 / NetMQ / Newtonsoft.Json (client)

## Protocol Rules

- Transform protocol is `protocolVersion=3` only (`v2` removed)
- Message IDs: `MSG_CLIENT_POSE=11`, `MSG_ROOM_POSE=12`
- `Head` is absolute; `Right/Left/Virtual` are head-relative
- Position quantization: absolute `int24 @ 0.01m`, head-relative `int16 @ 0.005m`; out-of-range values clamped
- Quaternion: 32-bit smallest-three compression
- Server relays raw client pose body bytes (opaque relay, no decode)
- **Do NOT use `ZMQ_CONFLATE`**: It corrupts 2-frame multipart messages (topic + payload). Implement conflate-like behavior at the application level.
- Priority-based sending: Control messages (RPC, Network Variables) prioritized over Transform updates

### Backward Compatibility Policy

- Network protocol changes do NOT require backward compatibility — deploy server and clients together
- Avoid unnecessary breaking changes to non-networking code
- Always notify the user of breaking changes

## Unity–Python Feature Parity

The Python client (`client.py`) and Unity client (`NetSyncManager.cs` + internal managers) must maintain feature parity. When modifying one side, check whether the other needs the same change.

Key mappings: `NetSyncManager.cs` ↔ `client.py`, `ConnectionManager.cs` ↔ connection in `client.py`, `BinarySerializer.cs` ↔ `binary_serializer.py`, `RPCManager.cs`/`NetworkVariableManager.cs` ↔ RPC/NV in `client.py`

## Coding Rules

### Unity C# (CRITICAL)

- **Never use null propagation (`?.` / `??`) with UnityEngine.Object types**
- All Unity API calls must be on main thread — no background thread access
- Do not add `.meta` files manually
- Namespace: `Styly.NetSync` (public) / `Styly.NetSync.Internal` (internal)
- Naming: private fields `_camelCase`, public `PascalCase`, 4-space indent

### Python

- Black (88 chars), Ruff, MyPy strict; `snake_case` functions, `PascalCase` classes

## Commit & PR Guidelines

- Conventional Commits (`feat:`, `fix:`, `refactor:`); subject ≤72 chars
- Target PRs to `develop`; manual PRs to `main` are blocked (use release workflow)
- PR checklist: description, linked issues, test evidence (logs/screenshots)

## Task Completion Checklist

- **Python**: `black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest`
- **Unity**: Verify compilation, test in demo scenes, check console for errors
- **Both**: Commit with descriptive messages; test server-client integration when applicable
- Avoid committing secrets
