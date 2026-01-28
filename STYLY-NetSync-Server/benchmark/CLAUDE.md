# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with the benchmark component.

## Overview

The benchmark directory contains load testing tools for STYLY NetSync server using Locust framework.

## Quick Start

```bash
# Install dependencies
cd benchmark
pip install -r requirements.txt

# Run with Web UI (recommended for interactive testing)
locust -f locustfile.py --host=tcp://localhost:5555

# Run headless (for automated testing)
locust -f locustfile.py --headless -u 100 -r 10 -t 300s --host=tcp://localhost:5555
```

## Client Types

Two client implementations are supported:

- **raw_zmq**: Direct ZeroMQ socket implementation (lower overhead)
- **netsync_manager**: High-level net_sync_manager API wrapper

Select via environment variable or CLI:
```bash
STYLY_CLIENT_TYPE=raw_zmq locust -f locustfile.py
locust -f locustfile.py --styly-client-type netsync_manager
```

## Configuration

### Environment Variables
- `STYLY_CLIENT_TYPE`: Client implementation (raw_zmq/netsync_manager)
- `STYLY_TRANSFORM_RATE`: Transform update rate in Hz (default: 30.0)
- `STYLY_RPC_PER_TRANSFORMS`: RPC calls per transform cycle (default: 0.1)
- `STYLY_MOVEMENT_SPEED`: Simulated movement speed (default: 1.0)

### CLI Arguments
- `--styly-client-type`: Client implementation
- `--styly-detailed-logging`: Enable detailed logging
- `--styly-export-metrics`: Export metrics to JSON file

### Configuration File
Settings in `src/benchmark_config.py` define defaults for all parameters.

## Docker Visualization Stack

```bash
# Start Prometheus, Grafana, and locust-exporter
docker-compose up -d

# Access Grafana at http://localhost:3000
```

## Running Tests

```bash
cd benchmark
pytest tests/
```

## Project Structure

- `locustfile.py`: Main Locust test file with STYLYNetSyncUser behavior
- `src/benchmark_config.py`: Configuration management
- `src/client_factory.py`: Client abstraction layer
- `src/metrics_collector.py`: Metrics collection utilities
- `tests/`: Unit tests for benchmark components
- `docker-compose.yml`: Visualization stack configuration

## pyproject.toml Policy

This benchmark uses a **minimal pyproject.toml** by design - it is a script collection, not a distributable package.

**DO NOT ADD:**
- `[build-system]` section
- `[project.scripts]` section
- `[project.urls]` section
- Additional metadata: `authors`, `license`, `classifiers`
