

## Prepare develop environment

### Option A: Use Dev Container (Recommended)

#### Prerequisites

- VS Code, Docker
- [Dev Container](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension

#### Open Dev Container

Click the `><` icon in the bottom-left corner of the window.
(Or press `Cmd + Shift + P`) and select `Dev Containers: Reopen in Container`.

### Option B:

#### Prerequisites

- Python >=3.11
- uv

#### Install `styly-netsync-server` in development mode
```
pip install -e .
```

## Usage — STYLY NetSync server & CLIs

When you install the package with pip install -e . (editable / development mode), changes to the package's Python source files in your working tree are reflected immediately when you run the commands below — you don't need to reinstall.
```
# Start STYLY NetSync Server
styly-netsync-server

# Simulate 50 clients
styly-netsync-simulator --clients 50

# Custom server and room
styly-netsync-simulator --server tcp://localhost --room my_room --clients 50
```

## Wire protocol compatibility

- Current wire protocol is `protocolVersion = 7`.
- Transport uses three sockets: control (`control_port`, default `5555`) for RPC, Network Variables, ownership, ID mapping, and client hello; transform uplink (`transform_port`, default `5557`) for client/object poses; PUB/SUB (`pub_port`, default `5556`) for room pose and room object downlink.
- Discovery responses use `STYLY-NETSYNC2|controlPort|transformPort|pubPort|serverName`; legacy `STYLY-NETSYNC|...` responses are explicitly incompatible.
- `dealer_port` / `--dealer-port` remain a one-release compatibility alias for `control_port` / `--control-port`.
- Transform messages use `MSG_CLIENT_POSE` (11) and `MSG_ROOM_POSE` (12) with the compact pose body. Clients register control identity with `MSG_CLIENT_HELLO` (19).
- Moving-floor-local poses set the `MovingFloorLocal` pose flag. Bound avatars send head, hands, and virtual transforms in the registered moving floor's local coordinates, and reuse the existing 8-byte physical slot as direct physical position/yaw.
- Unbound poses keep the `xrOriginDelta` semantics: `xrOriginDelta` carries a Y component as a 4th `int16` (`dx, dy, dz, dyaw` = 8 bytes), so receivers can reconstruct the sender's rig-Y motion.
- Legacy transform protocols (v2/v3) and JSON transform fallback are not supported.
- Deploy Unity and Python updates together when changing transform protocol behavior.
- Protocol v7 position quantization ranges:
  - Absolute (`headPosAbs` only): signed `int24` at `0.01 m` per unit, per-axis range `[-83,886.08 m, 83,886.07 m]`.
  - XROrigin locomotion delta for unbound poses (`xrOriginDelta`, 4×`int16`: `dx, dy, dz, dyaw`): `0.01 m` per unit for translation, `0.1°` for yaw. Receivers reconstruct `physicalPos = invDeltaRot * (headPos − deltaPos)`; it is not on the wire as a separate absolute field.
  - Direct physical payload for moving-floor-local poses (`physical`, 4×`int16`: `x, y, z, yaw`): `0.01 m` per unit for translation, `0.1°` for yaw.
  - Head-relative (`right/left/virtual`): signed `int16` at `0.005 m` per unit, per-axis range `[-163.84 m, 163.835 m]`.
- These are encoding limits, not a hard world-size cap. Worlds can be larger, but encoded axis values are clamped if they exceed the representable range.

### Coordinate Range Expansion Options (Design Notes)

The following options summarize trade-offs when expanding absolute-position range.

Assumed unbound baseline (`protocolVersion=7`, `MovingFloorLocal` off):
- Client pose body with `Physical+Head+Right+Left` valid and `virtualCount=0`: `46 bytes` (matches `test_client_body_size_with_full_pose_no_virtuals`).
- Room per-client entry (`clientNo + poseTime + clientBody`): `56 bytes`.

| Option | Absolute Position Encoding | Per-axis Range | Client Body Delta | Room Per-client Delta |
|---|---|---:|---:|---:|
| A. Coarser scale (current integer width) | `int24 @ 0.02m` | `[-167,772.16m, 167,772.14m]` | `+0B` (`46 -> 46`) | `+0B` (`56 -> 56`) |
| B. Cell + local | `cell(i16, 256m) + local(int24 @ 0.01m)` | `[-8,472,494.08m, 8,472,238.07m]` | `+6B` (`46 -> 52`, `+13.0%`) | `+6B` (`56 -> 62`, `+10.7%`) |
| C. Cell + local (large cell) | `cell(i16, 1024m) + local(int24 @ 0.01m)` | `[-33,638,318.08m, 33,637,294.07m]` | `+6B` (`46 -> 52`, `+13.0%`) | `+6B` (`56 -> 62`, `+10.7%`) |

Notes:
- Only `headPosAbs` is on the wire as an absolute field; `physicalPos` is reconstructed from `headPosAbs + xrOriginDelta`. Option B/C deltas therefore apply to `headPosAbs` only.
- Option B/C can reduce average overhead if `cell` is transmitted only when changed, but that requires extra state and flags in the wire format.

## Configuration

The server uses TOML configuration files. Default values are bundled in `src/styly_netsync/default.toml`.

To customize settings:

1. Get the default config file:
   - From local clone: `cp src/styly_netsync/default.toml my-config.toml`
   - Or download from GitHub: [default.toml](https://github.com/styly-dev/STYLY-NetSync/blob/develop/STYLY-NetSync-Server/src/styly_netsync/default.toml)

2. Edit `my-config.toml` and keep only the settings you want to change (delete the rest)

3. Run the server with your config file:
   ```bash
   styly-netsync-server --config my-config.toml
   ```

Configuration priority: CLI arguments > user config > default config

Example minimal config:
```toml
# Only override what you need
server_name = "My-Custom-Server"
transform_broadcast_rate = 30  # 30Hz instead of default 10Hz
```

## Logging

File output: add `--log-dir DIR` to write JSON lines to `DIR/netsync-server.log` (DEBUG level).

```
# output logs at ./logs directory
styly-netsync-simulator --log-dir logs
```

Console output: human-friendly text by default. Use `--log-json-console` for JSON and `--log-level-console LEVEL` to change the level.

Rotation & retention: default is 10 MB or 7 days, keeping the newest 20 files. Override with `--log-rotation` / `--log-retention` (loguru syntax like `"10 MB"`, `"1 day"`, `"keep 5 files"`).

Bridging: stdlib `logging` is routed to loguru automatically.

## REST bridge

The server launches an embedded FastAPI application that exposes REST endpoints for managing Network Variables. Default port: `8800` (override with `--rest-api-port` CLI argument or `rest_api_port` in config file).

### Client variables

- Endpoint: `POST /v1/rooms/{roomId}/devices/{deviceId}/client-variables`
- Payload body:

```json
{
  "variables": {
    "name": "Jack",
    "lang": "EN"
  }
}
```

- Constraints:
  - Variable names: 1–64 characters
  - Values: up to 1024 characters
  - Total variables per client: 20 (additional keys return HTTP 409)
- Behavior:
  - If a device has not connected yet, the values are queued in an in-memory preseed store and automatically applied once the server assigns a `clientNo`.
  - If the device is already connected, the variables are sent immediately through the existing ZeroMQ pathway.
- Example:

```bash
curl -sS -X POST "http://127.0.0.1:8800/v1/rooms/default_room/devices/00000000-0000-0000-0000-000000000000/client-variables" \
  -H "Content-Type: application/json" \
  -d '{"variables":{"name":"Jack","lang":"EN"}}'
```

The response includes the current mapping status (`clientNo` or `null`) and whether each key was `"applied"` or `"queued"`.

- Read endpoints:
  - `GET /v1/rooms/{roomId}/devices/{deviceId}/client-variables` — returns all client variables for the device.

    ```json
    {"clientNo": 7, "variables": {"name": "Jack", "lang": "EN"}}
    ```

    If the device has no `clientNo` mapping yet, returns `{"clientNo": null, "variables": {}}`.

  - `GET /v1/rooms/{roomId}/devices/{deviceId}/client-variables/{name}` — returns a single variable.

    ```json
    {"clientNo": 7, "value": "Jack"}
    ```

    Returns `404` if the device is unmapped or the variable is not set.

- Example:

  ```bash
  curl -sS "http://127.0.0.1:8800/v1/rooms/default_room/devices/00000000-0000-0000-0000-000000000000/client-variables"
  curl -sS "http://127.0.0.1:8800/v1/rooms/default_room/devices/00000000-0000-0000-0000-000000000000/client-variables/name"
  ```

### Global variables

- Endpoint: `POST /v1/rooms/{roomId}/global-variables`
- Payload body:

```json
{
  "variables": {
    "score": "42",
    "stage": "lobby"
  }
}
```

- Constraints:
  - Variable names: 1–64 characters
  - Values: up to 1024 characters
  - Total global variables per room: 100 (additional keys return HTTP 409)
- Behavior:
  - If the bridge client for the room is connected, variables are applied immediately via `set_global_variable`.
  - If not yet connected, variables are queued and automatically flushed once the bridge connects. Successfully applied variables are removed from the queue; failed variables are re-queued for the next flush cycle.
- Example:

```bash
curl -sS -X POST "http://127.0.0.1:8800/v1/rooms/default_room/global-variables" \
  -H "Content-Type: application/json" \
  -d '{"variables":{"score":"42","stage":"lobby"}}'
```

The response includes the room ID and whether each key was `"applied"`, `"queued"`, or `"failed"`.

- Read endpoints:
  - `GET /v1/rooms/{roomId}/global-variables` — returns all global variables for the room.

    ```json
    {"variables": {"score": "42", "stage": "lobby"}}
    ```

  - `GET /v1/rooms/{roomId}/global-variables/{name}` — returns a single variable.

    ```json
    {"value": "42"}
    ```

    Returns `404` if the variable is not set.

- Example:

  ```bash
  curl -sS "http://127.0.0.1:8800/v1/rooms/default_room/global-variables"
  curl -sS "http://127.0.0.1:8800/v1/rooms/default_room/global-variables/score"
  ```

### Read consistency

GET endpoints return a snapshot of the REST bridge's in-process cache, which is populated by control-lane sync messages from the server. The first request to a room lazily creates a bridge and may return an empty snapshot until the initial sync arrives — retry after a short delay if needed.
