# Benchmark Tests

This directory contains tests for the STYLY NetSync benchmark system.

## Running Tests

From the benchmark root directory:

```bash
# Activate virtual environment
source .venv/bin/activate

# Run all tests
cd tests
PYTHONPATH=.. python test_client_factory.py
PYTHONPATH=.. python test_locust_args.py
```

## Test Files

- `test_client_factory.py` - Tests the client factory and both client implementations
- `test_locust_args.py` - Tests environment variable and configuration handling