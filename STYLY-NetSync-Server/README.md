

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

- Current transform wire protocol is `protocolVersion = 3`.
- Transform messages use `MSG_CLIENT_POSE` (11) and `MSG_ROOM_POSE` (12) with the compact V3 pose body.
- Legacy transform protocol v2 and JSON transform fallback are not supported.
- Deploy Unity and Python updates together when changing transform protocol behavior.
- Protocol v3 position quantization ranges:
  - Absolute (`physicalPos`, `headPosAbs`): signed `int24` at `0.01 m` per unit, per-axis range `[-83,886.08 m, 83,886.07 m]`.
  - Head-relative (`right/left/virtual`): signed `int16` at `0.005 m` per unit, per-axis range `[-163.84 m, 163.835 m]`.
- These are encoding limits, not a hard world-size cap. Worlds can be larger, but encoded axis values are clamped if they exceed the representable range.

### Coordinate Range Expansion Options (Design Notes)

The following options summarize trade-offs when expanding absolute-position range.

Assumed baseline (`protocolVersion=3`):
- Client pose body with `Physical+Head+Right+Left` valid and `virtualCount=0`: `49 bytes`
- Room per-client entry (`clientNo + poseTime + clientBody`): `59 bytes`

| Option | Absolute Position Encoding | Per-axis Range | Client Body Delta | Room Per-client Delta |
|---|---|---:|---:|---:|
| A. Coarser scale (current integer width) | `int24 @ 0.02m` | `[-167,772.16m, 167,772.14m]` | `+0B` (`49 -> 49`) | `+0B` (`59 -> 59`) |
| B. Cell + local | `cell(i16, 256m) + local(int24 @ 0.01m)` | `[-8,472,494.08m, 8,472,238.07m]` | `+6B` (`49 -> 55`, `+12.2%`) | `+6B` (`59 -> 65`, `+10.2%`) |
| C. Cell + local (large cell) | `cell(i16, 1024m) + local(int24 @ 0.01m)` | `[-33,638,318.08m, 33,637,294.07m]` | `+6B` (`49 -> 55`, `+12.2%`) | `+6B` (`59 -> 65`, `+10.2%`) |

Notes:
- Option B/C deltas assume both absolute transforms are present (`physicalPos` and `headPosAbs`).
- Option B/C can reduce average overhead if `cell` is transmitted only when changed, but that requires extra state and flags in the wire format.

## Configuration

The server uses TOML configuration files. Default values are bundled in `src/styly_netsync/default.toml`.

To customize settings:

1. Get the default config file:
   - From local clone: `cp src/styly_netsync/default.toml my-config.toml`
   - Or download from GitHub: [default.toml](https://github.com/psychic-vr-lab/STYLY-NetSync/blob/main/STYLY-NetSync-Server/src/styly_netsync/default.toml)

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

## REST bridge for client variables

Starting with this version the server launches an embedded FastAPI application that exposes a REST endpoint for pre-seeding and updating per-client Network Variables by `deviceId`.

- Endpoint: `POST /v1/rooms/{roomId}/devices/{deviceId}/client-variables`
- Default port: `8800` (override with environment variable `NETSYNC_REST_PORT`)
- Payload body:

```json
{
  "vars": {
    "name": "Jack",
    "lang": "EN"
  }
}
```

- Constraints enforced by the bridge:
  - Variable names: 1–64 characters
  - Values: up to 1024 characters
  - Total variables per client: 20 (additional keys return HTTP 409)
- Behavior:
  - If a device has not connected yet, the values are queued in an in-memory preseed store and automatically applied once the server assigns a `clientNo`.
  - If the device is already connected, the variables are sent immediately through the existing ZeroMQ pathway.
- Typical usage (curl example):

```bash
curl -sS -X POST "http://127.0.0.1:8800/v1/rooms/default_room/devices/00000000-0000-0000-0000-000000000000/client-variables" \
  -H "Content-Type: application/json" \
  -d '{"vars":{"name":"Jack","lang":"EN"}}'
```

The response includes the current mapping status (`clientNo` or `null`) and whether each key was queued or applied.
