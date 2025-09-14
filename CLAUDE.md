# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Structure

STYLY-NetSync is a Unity multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences. The repository contains two main components:

- **STYLY-NetSync-Server/**: Python server using ZeroMQ for networking (`styly-netsync-server@0.5.7`)
- **STYLY-NetSync-Unity/**: Unity package with client implementation (`com.styly.styly-netsync@0.5.7`)

## Development Commands

### Python Server (STYLY-NetSync-Server/)

```bash
# Setup
cd STYLY-NetSync-Server
pip install -e .                    # Install package with dependencies (pyzmq>=26.0.0)
pip install -e ".[dev]"             # Install with dev dependencies (pytest, black, ruff, mypy)

# Running server
python -m styly_netsync
python -m styly_netsync --dealer-port 5555 --pub-port 5556 --beacon-port 9999
python -m styly_netsync --no-beacon  # Without UDP discovery

# Load testing
python src/styly_netsync/client_simulator.py --clients 100 --server tcp://localhost

# Development tools (ALWAYS run before committing)
black src/ tests/                   # Format code
ruff src/ tests/                    # Lint code  
mypy src/                          # Type check
pytest                             # Run tests

# Port management
lsof -i :5555                      # Check port conflicts (macOS/Linux)
kill <PID>                         # Kill process using port
```

### Unity Client (STYLY-NetSync-Unity/)

- Build and test using Unity Editor (Unity 6000.0.48f1+)
- Main package: `Packages/com.styly.styly-netsync/` (`com.styly.styly-netsync@0.5.7`)
- Test scenes: `Assets/Samples_Dev/Demo-01/` and `Assets/Samples_Dev/Debug/`

## Architecture Overview

### Core Components

**Server (Python):**
- `server.py` - Main server with group management and room handling
- `binary_serializer.py` - Binary protocol implementation (~60% bandwidth reduction)
- `client_simulator.py` - Load testing tool for performance validation

**Unity Client:**
- `NetSyncManager.cs` - Main singleton entry point and API
- `NetSyncAvatar.cs` - Component for avatar synchronization
- `Internal/` - Core networking components (ConnectionManager, MessageProcessor, etc.)

### Protocol Details

- Uses ZeroMQ with DEALER-ROUTER and PUB-SUB patterns
- Binary serialization with client number system (2-byte IDs)
- UDP discovery service for automatic server finding
- Adaptive broadcasting (2-20Hz) with thread-safe design

### Key Architectural Patterns

- **Server**: Group-based room management with transform broadcasting
- **Unity**: Manager pattern with internal components handling specific concerns
- **Networking**: Binary protocol with efficient serialization
- **Synchronization**: Transform, RPC, Network Variables, Device ID Mapping

## Unity C# Coding Rules (CRITICAL)

- **Never use null propagation (`?.` / `??`) with UnityEngine.Object types**
  - ❌ `return transform?.position;`
  - ✅ `return transform != null ? transform.position : Vector3.zero;`
- All Unity API calls must be on main thread - no background thread access
- Use explicit null checks instead of null propagation operators
- Follow namespace conventions: `Styly.NetSync` (public) / `Styly.NetSync.Internal` (internal)

## Task Completion Checklist

Before submitting code:

**Python Server:**
1. `black src/ tests/` (format)
2. `ruff src/ tests/` (lint)
3. `mypy src/` (type check)
4. `pytest` (tests)

**Unity Client:**
1. Verify compilation in Unity Editor
2. Test in demo scenes
3. Check console for errors

**Both:**
- Commit with descriptive messages
- Verify backward compatibility
- Test server-client integration