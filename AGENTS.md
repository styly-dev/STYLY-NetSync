# STYLY-NetSync

## Repository Structure

STYLY-NetSync is a Unity multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences.

- **STYLY-NetSync-Server/**: Python server using ZeroMQ for networking
- **STYLY-NetSync-Unity/**: Unity package with client implementation

## XR-Specific Design Considerations

STYLY-NetSync is designed exclusively for XR (VR/MR/AR) applications:

- **Motion-Adaptive Sending**: Transform sending uses `SendRate` as an upper bound with `Only-on-change` filtering plus a `1Hz` heartbeat while idle.
- **Bandwidth Planning**: Assume motion-dependent traffic with an idle heartbeat floor, not continuous full-payload flow.

## Deployment Assumptions & Threat Model

STYLY-NetSync targets on-site LBE installations. These assumptions are intentional
and bound what counts as a real issue during review:

- **Trusted LAN, no malicious devices**: The server and all clients run on a
  closed, operator-controlled local network. There is no untrusted participant on
  the wire. Therefore client-identity spoofing, control-lane takeover, message
  forgery, and similar adversarial scenarios are **out of scope** — do not treat
  them as findings. (Functional correctness for *legitimate* clients — e.g.
  socket-ordering races between the control and transform lanes — is still in
  scope.)
- **Server and clients always upgrade together**: A given deployment runs a single
  matching version everywhere. There is **no backward/forward compatibility
  requirement** on the network protocol or discovery format. Prefer the simplest
  implementation; do not add legacy-version fallbacks or compatibility shims for
  the wire protocol. See [Backward Compatibility Policy](#backward-compatibility-policy).
- **No transport-level authentication/encryption**: Security is provided by the
  trusted-network boundary, not by the protocol. Do not propose adding authn/crypto
  to the ZeroMQ transport unless the deployment model itself changes.

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

- **Server**: Multi-threaded Python (receive, periodic, discovery threads) with ZeroMQ control/transform DEALER-ROUTER sockets + PUB-SUB and group-based room management
- **Unity Client**: Manager pattern with internal components (connection, transform sync, RPC, network variables, avatars)
- **Protocol**: Binary v8 with quantized positions and smallest-three quaternion compression
- **Technology**: Python 3.11+ / pyzmq / FastAPI / msgpack (server), Unity 6 / NetMQ / Newtonsoft.Json (client)

## Protocol Rules

- Binary protocol is `protocolVersion=8` only (earlier versions removed); the version byte rides on transform/object/hello messages and bumps on any breaking wire change, including Network Variable message changes
- Client-originated control messages (RPC, Global/Client Var Set, Client Var Clear, Object Ownership Request) carry a sender `deviceId` so the server can rebind stale control-lane identities by stable device ID instead of volatile client numbers
- Message IDs: `MSG_CLIENT_POSE=11`, `MSG_ROOM_POSE=12`, `MSG_CLIENT_VAR_CLEAR=18`, `MSG_CLIENT_HELLO=19`
- Unbound `Head` is absolute; `Right/Left/Virtual` are head-relative
- Moving-floor-local poses set `PoseFlags.MovingFloorLocal`; `Head` is moving-floor local while `Right/Left/Virtual` remain head-relative within that floor
- Unbound `xrOriginDelta` is `(dx, dy, dz, dyaw)` quantized as 4×`int16` (`LOCO_POS_SCALE = 0.01m` for translation, `PHYSICAL_YAW_SCALE = 0.1°` for yaw); receivers reconstruct physical pose as `physical = invDeltaRot * (headPos − deltaPos)`
- Moving-floor-local poses reuse the same 8-byte physical slot as direct physical position/yaw instead of `xrOriginDelta`
- Position quantization: absolute `int24 @ 0.01m`, head-relative `int16 @ 0.005m`; out-of-range values clamped
- Quaternion: 32-bit smallest-three compression
- Server relays raw client pose body bytes (opaque relay, no decode)
- **Do NOT use `ZMQ_CONFLATE`**: It corrupts 2-frame multipart messages (topic + payload). Implement conflate-like behavior at the application level.
- Priority-based sending: Control messages (RPC, Network Variables) prioritized over Transform updates

### Backward Compatibility Policy

- Network protocol changes do NOT require backward compatibility — deploy server and clients together
- This also covers the discovery handshake (`STYLY-NETSYNC3`): a reply that is not the current format is simply "not a compatible server"; do not add legacy-version detection branches or fallbacks
- Avoid unnecessary breaking changes to non-networking code
- Always notify the user of breaking changes

## Unity–Python Feature Parity

The Python client (`client.py`) and Unity client (`NetSyncManager.cs` + internal managers) must maintain feature parity. When modifying one side, check whether the other needs the same change.

Key mappings: `NetSyncManager.cs` ↔ `client.py`, `ConnectionManager.cs` ↔ connection in `client.py`, `BinarySerializer.cs` ↔ `binary_serializer.py`, `RPCManager.cs`/`NetworkVariableManager.cs` ↔ RPC/NV in `client.py`

## Coding Rules

### Unity C# (CRITICAL)

- **Never use null propagation (`?.` / `??`) with UnityEngine.Object types**
- All Unity API calls must be on main thread — no background thread access
- Do not create or edit Unity `.meta` files manually or with an LLM.
- Commit Unity-generated `.meta` files together with the corresponding new, renamed, or moved asset/script.
- Namespace: `Styly.NetSync` (public) / `Styly.NetSync.Internal` (internal)
- Naming: private fields `_camelCase`, public `PascalCase`, 4-space indent

### Python

- Black (88 chars), Ruff, MyPy strict; `snake_case` functions, `PascalCase` classes

## Commit & PR Guidelines

- Write all commit messages, PR titles/descriptions, issues, and code comments in English
- Conventional Commits (`feat:`, `fix:`, `refactor:`); subject ≤72 chars
- Target PRs to `develop`; manual PRs to `main` are blocked (use release workflow)
- PR checklist: description, linked issues, test evidence (logs/screenshots)

## Task Completion Checklist

- **Python**: `black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest`
- **Unity**: Verify compilation, test in demo scenes, check console for errors
- **Both**: Commit with descriptive messages; test server-client integration when applicable
- Avoid committing secrets
