# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

STYLY-NetSync-Server is the Python server component of STYLY NetSync, a multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences. This server handles real-time synchronization of player positions, hand tracking, and virtual objects using ZeroMQ networking with custom binary serialization.

## Development Commands

### Setup and Installation
```bash
# Install as editable package with dependencies
pip install -e .
pip install -e ".[dev]"  # With development dependencies (pytest, black, ruff, mypy)

# Using uv for quick execution
uvx styly-netsync-server  # One-time execution
uv run dev                # Development mode
```

### Running the Server
```bash
# Via CLI entry point
styly-netsync-server
styly-netsync-server --dealer-port 5555 --pub-port 5556 --beacon-port 9999
styly-netsync-server --no-beacon  # Without UDP discovery

# As Python module
python -m styly_netsync

# Direct script execution
python src/styly_netsync/server.py
```

### Testing and Load Simulation
```bash
# Run test suite
pytest                    # All tests
pytest --cov=src          # With coverage
pytest -k test_stealth    # Specific test pattern
pytest tests/integration/ # Integration tests only

# Client simulation for load testing
styly-netsync-simulator --clients 100 --server tcp://localhost --room default_room
python src/styly_netsync/client_simulator.py --clients 100
```

### Code Quality (Required Before Commits)
```bash
# Format, lint, type check (run all before committing)
black src/ tests/
ruff check src/ tests/
mypy src/

# Complete quality pipeline
black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest --cov=src
```

### Port Management and Debugging
```bash
# Check port conflicts (macOS/Linux)
lsof -i :5555
kill <PID>              # Kill specific process
pkill -f styly-netsync  # Kill all STYLY processes
```

## Architecture Overview

### Core Components
- **NetSyncServer** (`server.py`): Main server class with multi-threaded architecture
  - Main Thread: Server lifecycle management
  - Receive Thread: Client message processing  
  - Periodic Thread: Broadcasting and cleanup
  - Discovery Thread: UDP server discovery beacon
- **BinarySerializer** (`binary_serializer.py`): Custom binary protocol (~60% bandwidth reduction vs JSON)
- **Python Client API** (`client.py`): `net_sync_manager` class for Python clients

### Key Data Types (`types.py`)
- **transform_data**: Basic position/rotation data
- **client_transform_data**: Client-specific transform with device info
- **room_snapshot**: Complete room state snapshot

### Protocol Details
- **ZeroMQ Patterns**: DEALER→ROUTER (client-server) and PUB→SUB (broadcasting)
- **Message Types**: Binary message identifiers (MSG_CLIENT_TRANSFORM, MSG_RPC, etc.)
- **Client Management**: Device ID to client number mapping system (2-byte client IDs)
- **UDP Discovery**: Automatic server discovery service

### Threading Model
Multi-threaded server with thread-safe design:
- Message queues for inter-thread communication
- Locks for shared state protection
- Graceful shutdown handling across all threads

## Development Standards

### Python Requirements
- **Version**: Python 3.11+ (with 3.8+ compatibility)
- **Type Hints**: Required for all functions (`mypy` strict mode)
- **Formatting**: Black (88-character line length)
- **Linting**: Ruff with comprehensive rule set

### Quality Gates (All Required)
- ✅ Black formatting applied
- ✅ Ruff linting passes
- ✅ MyPy type checking passes  
- ✅ All tests pass including integration tests
- ✅ Test coverage maintained

### Testing Categories
- **Unit Tests**: Individual component testing
- **Integration Tests**: Full client-server interaction (`tests/integration/`)
- **Load Testing**: Client simulator with configurable client counts
- **Protocol Tests**: Binary serialization and message handling

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