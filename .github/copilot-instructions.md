# STYLY-NetSync GitHub Copilot Instructions

**CRITICAL**: Always follow these instructions FIRST before searching or using bash commands. Only fallback to additional search and context gathering if the information here is incomplete or found to be in error.

STYLY-NetSync is a Unity multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences. It provides real-time synchronization using ZeroMQ networking between Unity clients and a Python server.

## Working Effectively

### Bootstrap the Repository
Run these commands in order. NEVER CANCEL any commands - all timings are validated:

```bash
# Navigate to Python server directory
cd STYLY-NetSync-Server

# Install server in development mode - takes 7 seconds
pip install -e .

# Test server starts correctly (should see startup logs immediately)
styly-netsync-server --help
```

### Build and Test Python Server
**CRITICAL TIMING**: All pytest commands take 5-60 seconds. NEVER CANCEL. Set timeout to 120+ seconds minimum.

```bash
# Install development tools - may take 30+ seconds depending on network
pip install pytest black ruff mypy

# Run core functionality tests - takes 6 seconds. NEVER CANCEL.
pytest tests/test_all_run_methods.py -v

# Run network variable tests - takes 10 seconds. NEVER CANCEL.  
pytest tests/test_nv_improvements.py -v

# Run integration tests - REQUIRES RUNNING SERVER (see Integration Testing section)
# Integration tests take 10-30 seconds. NEVER CANCEL.
pytest tests/integration/test_stealth_mode.py -v
```

### Code Quality and Formatting
**TIMING**: Linting commands take 1-3 seconds each.

```bash
# Check code formatting - expect violations in current codebase
black src/ tests/ --check

# Run linting - expect many violations in current codebase  
ruff check src/ tests/

# Type check - expect many errors in current codebase
mypy src/
```

**IMPORTANT**: The current codebase has formatting violations and type errors. This is expected. These tools work correctly but the code needs cleanup.

### Run Python Server
**ALL SERVER COMMANDS START IMMEDIATELY**. If startup takes more than 5 seconds, there's a port conflict.

```bash
# Method 1: CLI entry point (VALIDATED)
styly-netsync-server

# Method 2: Python module (VALIDATED)  
python -m styly_netsync

# Method 3: Direct script (VALIDATED)
python src/styly_netsync/server.py

# With custom ports (use when testing multiple servers)
styly-netsync-server --dealer-port 6000 --pub-port 6001 --beacon-port 9999

# Disable UDP discovery (for firewall environments)
styly-netsync-server --no-beacon
```

## Unity Client

**CRITICAL**: Unity client CANNOT be built from command line. Use Unity Editor GUI only.

- **Unity Version**: Unity 6000.0.48f1 or later (Unity 6) - EXACT VERSION REQUIRED
- **Package Location**: `STYLY-NetSync-Unity/Packages/com.styly.styly-netsync`
- **Demo Scenes**: 
  - `Assets/Samples_Dev/Demo-01/Demo-01.unity` - Main demo
  - `Assets/Samples_Dev/Debug/Debug Scene.unity` - Debug testing

**Dependencies** (managed automatically by Unity Package Manager):
- NetMQ 4.0.2 (NuGet)
- Unity XR Core Utils 2.1.1  
- Newtonsoft.Json 3.2.1
- STYLY XR Rig 0.4.2

Do NOT attempt to install Unity or build Unity projects from command line. This requires Unity Editor GUI.

## Integration Testing

### Manual Integration Validation
**CRITICAL**: ALWAYS run this complete scenario after making changes to validate functionality.

**TIMING**: This test takes 15-30 seconds total. NEVER CANCEL during the process.

```bash
# Terminal 1: Start server with test ports
cd STYLY-NetSync-Server
styly-netsync-server --dealer-port 35555 --pub-port 35556 --beacon-port 39999

# Terminal 2: Run test client (will run for 10 seconds)
python tests/integration/test_client.py --dealer-port 35555 --sub-port 35556 --server localhost --room test_room

# Expected output:
# - Client connects and receives client number
# - Transform synchronization works  
# - Network variables are set and received
# - Server logs show client joined and left
```

### Load Testing
```bash
# Start server first, then run simulator
styly-netsync-simulator --clients 50 --server tcp://localhost --room default_room
```

### Testing All Server Methods
```bash
# Validates all documented run methods - takes 6 seconds. NEVER CANCEL.
pytest tests/test_all_run_methods.py -v
```

## Validation Requirements

### After Making ANY Changes
**MANDATORY**: Run these validation steps in order. DO NOT skip these steps.

1. **Server Start Test** (immediate):
```bash
cd STYLY-NetSync-Server
styly-netsync-server --help
styly-netsync-server --dealer-port 45555 --pub-port 45556 --beacon-port 49999
# Should see startup logs immediately. Stop with Ctrl+C.
```

2. **Integration Test** (15 seconds total):
```bash
# Terminal 1: Start test server
styly-netsync-server --dealer-port 35555 --pub-port 35556 --beacon-port 39999

# Terminal 2: Run test client (auto-stops after 10 seconds)
python tests/integration/test_client.py --dealer-port 35555 --sub-port 35556 --server localhost --room test_room

# NEVER CANCEL this test - validates core functionality
```

3. **Core Tests** (6 seconds total):
```bash
pytest tests/test_all_run_methods.py -v
# NEVER CANCEL - validates all server startup methods work
```

### Before Committing Code
```bash
# Format code (will modify files)
black src/ tests/

# Check for critical issues  
ruff check src/ tests/ --select E,F

# Run quick tests
pytest tests/test_all_run_methods.py -v
```

## Common Issues and Troubleshooting

### Port Conflicts
**SYMPTOM**: Server hangs on startup or "Address already in use" error.

```bash
# Linux/Mac: Find process using port
lsof -i :5555
kill -9 <PID>

# Kill all STYLY processes
pkill -f styly-netsync

# Windows: Find and kill process
netstat -ano | findstr :5555
taskkill /PID <PID> /F
```

### Network/Firewall Issues  
**SYMPTOM**: `pip install` timeouts or client connection failures.

- `pip install` may timeout due to firewall limitations - retry with `--timeout 60`
- Integration tests require localhost networking to work
- Server uses ports 5555 (DEALER), 5556 (PUB), 9999 (UDP discovery) by default

### Unity Editor Issues
**SYMPTOM**: Missing dependencies or compilation errors.

- Verify Unity 6000.0.48f1 is installed (exact version required)
- Dependencies are auto-managed by Unity Package Manager
- Do NOT attempt to resolve Unity issues via command line

## Project Structure

### Python Server (`STYLY-NetSync-Server/`)
**Key Files** (VALIDATED WORKING):
- `src/styly_netsync/server.py` - Main server implementation
- `src/styly_netsync/cli.py` - CLI entry point  
- `src/styly_netsync/client_simulator.py` - Load testing tool
- `tests/test_all_run_methods.py` - Validates all run methods
- `tests/integration/test_client.py` - Full integration test
- `pyproject.toml` - Package configuration with dev tools

### Unity Package (`STYLY-NetSync-Unity/Packages/com.styly.styly-netsync/`)
**Key Files**:
- `Runtime/NetSyncManager.cs` - Main singleton entry point
- `Runtime/NetSyncAvatar.cs` - Component for synchronization
- `Runtime/Internal/BinarySerializer.cs` - Protocol implementation
- `package.json` - Unity package definition

## Performance and Timing Expectations

### Command Timing (VALIDATED)
- `pip install -e .` - 7 seconds
- `styly-netsync-server` startup - immediate (< 1 second) 
- `pytest tests/test_all_run_methods.py` - 6 seconds
- `pytest tests/test_nv_improvements.py` - 10 seconds  
- Integration test full cycle - 15 seconds (10s client + 5s setup)
- `black`/`ruff` formatting - 1-3 seconds
- `mypy` type checking - 2 seconds

### Critical Warnings
- **NEVER CANCEL** any pytest command - they complete in under 60 seconds
- **NEVER CANCEL** server startup tests - they start immediately or fail fast
- **NEVER CANCEL** integration tests - they validate core functionality
- Set timeouts to 120+ seconds for any pytest command to be safe

## Development Workflow

### Making Changes to Python Server
1. Make minimal code changes
2. Run `styly-netsync-server --help` (verify CLI works)
3. Run integration test (verify functionality works)
4. Run `pytest tests/test_all_run_methods.py -v` (verify all methods work)
5. Format with `black src/ tests/` before committing

### Making Changes to Unity Package
1. Open Unity Editor with Unity 6000.0.48f1
2. Load `STYLY-NetSync-Unity` project
3. Test with demo scenes manually
4. Unity changes cannot be validated via command line

### Debug Tips
- Use custom ports for testing: `--dealer-port 35555 --pub-port 35556`
- Check server logs for connection issues
- Integration test is the definitive validation method
- Server starts immediately or fails fast - never hangs during startup

## Comprehensive Validation Scenarios

### Scenario 1: Fresh Repository Setup (VALIDATED WORKING)
```bash
# Clone repository (already done for you)
cd STYLY-NetSync-Server

# Install and verify server - takes 7 seconds
pip install -e .

# Verify all run methods work - takes 6 seconds
pytest tests/test_all_run_methods.py -v

# Test basic server functionality - immediate startup
styly-netsync-server --help
```

### Scenario 2: Full Integration Testing (VALIDATED WORKING)
```bash
# Terminal 1: Start server
cd STYLY-NetSync-Server  
styly-netsync-server --dealer-port 35555 --pub-port 35556 --beacon-port 39999

# Terminal 2: Run complete integration test
python tests/integration/test_client.py --dealer-port 35555 --sub-port 35556 --server localhost --room test_room

# Expected results:
# - Client connects and receives client number
# - Global variables are set and synchronized  
# - RPCs are sent and received
# - Transform data flows correctly
# - Client disconnects cleanly after 10 seconds
```

### Scenario 3: Load Testing (VALIDATED WORKING)
```bash
# Terminal 1: Start server
styly-netsync-server

# Terminal 2: Run load test
styly-netsync-simulator --clients 50 --server tcp://localhost --room default_room

# Monitor server performance and client synchronization
```

### Scenario 4: Development Workflow (VALIDATED WORKING)
```bash
# Make code changes to server
# Then validate step by step:

# 1. Quick functionality check
styly-netsync-server --help

# 2. Run core tests - 6 seconds
pytest tests/test_all_run_methods.py -v

# 3. Full integration test - 15 seconds
# (Run scenario 2 above)

# 4. Format before commit
black src/ tests/
```

### Scenario 5: Troubleshooting Port Conflicts (VALIDATED WORKING)
```bash
# Check if ports are in use
lsof -i :5555

# Kill processes if needed
pkill -f styly-netsync

# Test with custom ports
styly-netsync-server --dealer-port 45555 --pub-port 45556 --beacon-port 49999
```

### Expected Failure Scenarios
These scenarios SHOULD fail and indicate specific issues:

**Code Quality Issues** (EXPECTED TO FAIL in current codebase):
```bash
# These commands reveal existing issues that need fixing:
black src/ tests/ --check          # Shows formatting violations
ruff check src/ tests/              # Shows linting violations  
mypy src/                          # Shows type checking errors
```

**Network Environment Issues** (MAY FAIL):
```bash
# These may fail due to firewall/network limitations:
pip install -e ".[dev]"           # May timeout with firewall restrictions
pytest tests/integration/         # Requires localhost networking
```

## Summary
- **Python Server**: Fully functional with comprehensive test coverage
- **Unity Client**: Requires Unity Editor GUI - no command-line operations
- **Integration**: Complete server-client communication validated
- **Timing**: All commands complete within documented timeframes
- **Validation**: Multiple scenarios available for comprehensive testing