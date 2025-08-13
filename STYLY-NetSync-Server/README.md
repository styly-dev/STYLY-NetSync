# STYLY NetSync Server

A Unity-based multiplayer framework for Location-Based Entertainment (LBE) VR/AR experiences. Provides real-time synchronization of player positions, hand tracking, and virtual objects using a Python server with ZeroMQ networking and binary serialization.

## Installation

### From PyPI (when published)
```bash
pip install styly-netsync-server
```

### From Source
```bash
# Clone the repository
git clone https://github.com/PsychicVRLab/STYLY-LBE-Multiplayer.git
cd STYLY-LBE-Multiplayer/STYLY-NetSync-Server

# Install in development mode
pip install -e .
```

### Using uvx (recommended for one-time use)
```bash
# Run without installing
uvx styly-netsync-server

# Run with custom options
uvx styly-netsync-server --dealer-port 5555 --pub-port 5556
```

## Usage

### Command Line

Start the server with default settings:
```bash
styly-netsync-server
```

Start with custom configuration:
```bash
styly-netsync-server --dealer-port 5555 --pub-port 5556 --beacon-port 9999 --name "My-Server"
```

Disable UDP discovery:
```bash
styly-netsync-server --no-beacon
```

### Programmatic Usage

```python
from styly_netsync import NetSyncServer
import time

# Create and start server
server = NetSyncServer(
    dealer_port=5555,
    pub_port=5556,
    enable_beacon=True,
    beacon_port=9999,
    server_name="STYLY-NetSync-Server"
)

try:
    server.start()
    # Keep server running
    while True:
        time.sleep(1)
except KeyboardInterrupt:
    server.stop()
```

### Client Simulator

Test the server with simulated clients:
```bash
# Simulate 100 clients
styly-netsync-simulator --clients 100

# Custom server and room
styly-netsync-simulator --server tcp://localhost --room my_room --clients 50
```

## Architecture

- **Binary Protocol**: Efficient ZeroMQ-based networking with ~60% bandwidth reduction vs JSON
- **Threading Model**: Separate threads for receive, periodic broadcast, and cleanup operations
- **Client Management**: Device ID to client number mapping with automatic cleanup
- **Network Variables**: Synchronized key-value storage with conflict resolution
- **RPC System**: Remote procedure calls for custom game logic

## Dependencies

- Python 3.8+
- pyzmq >= 26.0.0

## Development

### Testing During Development

There are several ways to test the server during development:

#### 1. Install in Development Mode
```bash
# Install the package in editable mode
pip install -e .

# Run the server
styly-netsync-server
```

#### 2. Run Directly as Python Module
```bash
# From the project root
python -m styly_netsync

# With custom options
python -m styly_netsync --dealer-port 6000 --pub-port 6001
```

#### 3. Use uvx Without Installation
```bash
# Run from the project directory
uvx --from . styly-netsync-server

# Test with client simulator
uvx --from . styly-netsync-simulator --clients 50
```

#### 4. Create Test Scripts
```python
# test_server.py
from src.styly_netsync import NetSyncServer

server = NetSyncServer(dealer_port=5555, pub_port=5556)
try:
    server.start()
    print("Server is running. Press Ctrl+C to stop.")
    while True:
        pass
except KeyboardInterrupt:
    server.stop()
```

#### 5. Use the Test Client
```bash
# Start the server first
styly-netsync-server

# In another terminal, run the test client
python test_client.py
```

#### 6. Load Testing with Client Simulator
```bash
# Start server
styly-netsync-server

# In another terminal, simulate multiple clients
styly-netsync-simulator --clients 100 --room test_room

# Monitor server logs for performance metrics
```

### Code Quality

```bash
# Install development dependencies
pip install -e ".[dev]"

# Run tests
pytest

# Format code
black src/ tests/

# Lint code
ruff src/ tests/

# Type check
mypy src/
```
