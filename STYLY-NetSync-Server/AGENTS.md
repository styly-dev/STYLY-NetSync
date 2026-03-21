# STYLY-NetSync Server

## Repository Structure

- **Source**: `src/styly_netsync/`
- **Tests**: `tests/` (unit + integration)
- **Config**: `pyproject.toml`, `default.toml`

## Development Commands

```bash
# Setup
pip install -e ".[dev]"

# Run server
styly-netsync-server
styly-netsync-server --dealer-port 5555 --pub-port 5556 --server-discovery-port 9999
styly-netsync-server --config my-config.toml

# Quality pipeline (run before committing)
black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest --cov=src

# Load testing
styly-netsync-simulator --clients 100 --server localhost --room default_room
```

## Architecture

- **NetSyncServer** (`server.py`): Multi-threaded (receive, periodic, discovery threads)
- **BinarySerializer** (`binary_serializer.py`): Protocol v3 transform serializer
- **Python Client API** (`client.py`): `net_sync_manager` class for Python clients
- **REST Bridge** (`rest_bridge.py`): FastAPI REST API for external integrations

## Configuration

- `default.toml` contains all settings with defaults
- Priority: CLI args > user config (`--config`) > default config
- REST API: `--rest-api-port` (default: 8800)

## Entry Points

- `styly-netsync-server` — CLI via `cli.py`
- `styly-netsync-simulator` — CLI via `client_simulator.py`
- `python -m styly_netsync` — module execution

## Development Standards

- Python 3.11+ required; type hints required for all functions (MyPy strict)
- Black (88 chars), Ruff, MyPy; `snake_case` modules/functions, `PascalCase` classes
- Test files named `test_*.py`; keep deterministic and independent; prefer branch coverage
