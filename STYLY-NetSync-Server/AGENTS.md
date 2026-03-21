# STYLY-NetSync Server

## Repository Structure

- **Source**: `src/styly_netsync/`
- **Tests**: `tests/` (unit + integration)
- **Config**: `pyproject.toml`, `default.toml`

## Development Commands

### Setup and Installation
```bash
pip install -e .                    # Install package with dependencies
pip install -e ".[dev]"             # With dev dependencies (pytest, black, ruff, mypy)

# Using uv for quick execution
uvx styly-netsync-server            # One-time execution
uv run dev                          # Development mode
```

### Running the Server
```bash
styly-netsync-server
styly-netsync-server --dealer-port 5555 --pub-port 5556 --server-discovery-port 9999
styly-netsync-server --no-server-discovery  # Without UDP discovery
styly-netsync-server --config my-config.toml  # Custom TOML configuration

# Additional CLI options:
# --log-dir PATH         Directory for log files
# --log-level-console LEVEL  Console log level (default: INFO)
# --log-json-console     Emit console logs as JSON

# As Python module
python -m styly_netsync

# Direct script execution
python src/styly_netsync/server.py
```

### Testing and Load Simulation
```bash
pytest                    # All tests
pytest --cov=src          # With coverage
pytest -k test_stealth    # Specific test pattern
pytest tests/integration/ # Integration tests only

# Client simulation for load testing
styly-netsync-simulator --clients 100 --server localhost --room default_room
styly-netsync-simulator --clients 100 --transform-send-rate 60
styly-netsync-simulator --clients 100 --spawn-batch-size 10 --spawn-batch-interval 1.0  # Progressive spawning
```

### Code Quality (Required Before Commits)
```bash
black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest --cov=src
```

### Port Management
```bash
lsof -i :5555              # Check port conflicts (macOS/Linux)
kill <PID>                 # Kill specific process
pkill -f styly-netsync     # Kill all STYLY processes
```

## Architecture

### Core Components
- **NetSyncServer** (`server.py`): Main server class with multi-threaded architecture
  - Main Thread: Server lifecycle management
  - Receive Thread: Client message processing
  - Periodic Thread: Broadcasting and cleanup
  - Discovery Thread: UDP server discovery service
- **BinarySerializer** (`binary_serializer.py`): Protocol v3 transform serializer (absolute int24 positions, head-relative int16 positions, and 32-bit smallest-three quaternion compression)
- **Python Client API** (`client.py`): `net_sync_manager` class for Python clients
- **REST Bridge** (`rest_bridge.py`): FastAPI-based REST API for external integrations
- **Configuration** (`default.toml`): TOML-based server configuration

### Configuration System
- `default.toml` contains all available settings with defaults
- User config overrides: `--config user.toml`
- Priority: CLI args > user config > default config
- REST API port: `--rest-api-port` CLI argument or `rest_api_port` in config (default: 8800)
- REST endpoint: `POST /v1/rooms/{roomId}/devices/{deviceId}/client-variables`

### Key Data Types (`types.py`)
- **transform_data**: Basic position/rotation data
- **client_transform_data**: Client-specific transform with device info
- **room_snapshot**: Complete room state snapshot

### Protocol Details
- **ZeroMQ Patterns**: DEALER→ROUTER (client-server) and PUB→SUB (broadcasting)
- **Transform Message Types**: `MSG_CLIENT_POSE` (11) and `MSG_ROOM_POSE` (12) with `protocolVersion=3`
- **Encoding**: Compact pose body with absolute head pose and head-relative right/left/virtual transforms
- **Quantization Ranges**: absolute position uses `int24 @ 0.01m` (`[-83,886.08m, 83,886.07m]` per axis), head-relative position uses `int16 @ 0.005m` (`[-163.84m, 163.835m]` per axis); out-of-range values are clamped
- **Client Management**: Device ID to client number mapping system (2-byte client IDs)
- **Relay Path**: Server caches raw client pose body bytes and rebroadcasts with minimal reserialization
- **Compatibility**: Legacy v2/JSON transform fallback is removed; deploy server and clients together
- **UDP Discovery**: Automatic server discovery service

### Threading Model
Multi-threaded server with thread-safe design:
- Message queues for inter-thread communication
- Locks for shared state protection
- Graceful shutdown handling across all threads

## Development Standards

### Python Requirements
- **Version**: Python 3.11+ required (3.11 and 3.12 supported)
- **Type Hints**: Required for all functions (`mypy` strict mode)
- **Formatting**: Black (88-character line length)
- **Linting**: Ruff with comprehensive rule set

### Quality Gates (All Required)
- Black formatting applied
- Ruff linting passes
- MyPy type checking passes
- All tests pass including integration tests
- Test coverage maintained

### Testing Categories
- **Unit Tests**: Individual component testing
- **Integration Tests**: Full client-server interaction (`tests/integration/`)
- **Load Testing**: Client simulator with configurable client counts
- **Protocol Tests**: Binary serialization and message handling
- Test files named `test_*.py`; keep deterministic and independent; prefer branch coverage

## Entry Points and CLI
- **Server CLI**: `styly-netsync-server` (via `cli.py`)
- **Simulator CLI**: `styly-netsync-simulator` (via `client_simulator.py`)
- **Module Execution**: `python -m styly_netsync` (via `__main__.py`)
- **Direct Execution**: `python src/styly_netsync/server.py`

## Common Development Workflow
1. Make code changes
2. Run quality checks: `black src/ tests/ && ruff check src/ tests/ && mypy src/`
3. Run tests: `pytest --cov=src`
4. Test with simulator if needed: `styly-netsync-simulator --clients 50`
5. Commit changes with descriptive messages
