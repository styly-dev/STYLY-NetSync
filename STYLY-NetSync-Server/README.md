

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
