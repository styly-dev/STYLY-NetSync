
# STYLY NetSync Server

**Package**: `styly-netsync-server@0.5.7`

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

# Simulate 100 clients
styly-netsync-simulator --clients 100

# Custom server and room
styly-netsync-simulator --server tcp://localhost --room my_room --clients 50
```
