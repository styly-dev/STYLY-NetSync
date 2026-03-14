# Python Server Code Review Rules

## Table of Contents
- Type Annotations (CRITICAL)
- Code Formatting (Black)
- Linting (Ruff)
- Import Organization
- Exception Handling
- Logging
- Naming Conventions
- Dataclass Usage
- Constants & Enums
- Test Organization
- Python Version

---

## Type Annotations (CRITICAL)

All functions must have complete type annotations. MyPy strict mode is enforced.

```python
# GOOD
def process_message(data: bytes, client_no: int) -> dict[str, Any]:
    ...

def get_clients(room: str) -> list[int]:
    ...

# BAD — missing annotations
def process_message(data, client_no):
    ...

def get_clients(room):
    ...
```

Rules:
- Use modern syntax: `dict[str, Any]` not `Dict[str, Any]`
- Use union syntax: `str | None` not `Optional[str]`
- Use `from __future__ import annotations` for forward references
- Use `TYPE_CHECKING` for type-only imports to avoid circular dependencies
- Every function parameter and return type must be annotated

## Code Formatting (Black)

- Line length: **88 characters** (configured in `pyproject.toml`)
- Target: Python 3.11+
- Run `black src/ tests/` before committing

## Linting (Ruff)

Run `ruff check src/ tests/` before committing.

Enabled rule sets:
- `E`: pycodestyle errors
- `W`: pycodestyle warnings
- `F`: pyflakes
- `I`: isort (import sorting)
- `B`: flake8-bugbear
- `C4`: flake8-comprehensions
- `UP`: pyupgrade

File-specific ignores:
- `__init__.py`: `F401` (intentional re-exports)
- `client.py`: `E402` (version check at top)
- `tests/integration/*.py`: `E402`

## Import Organization

Group imports in this order, separated by blank lines:

```python
# 1. Standard library
import sys
import json
from typing import Any, TYPE_CHECKING

# 2. Third-party
import zmq
from loguru import logger
import msgpack

# 3. Local
from . import binary_serializer
from .config import ServerConfig
```

## Exception Handling

```python
# GOOD — specific exception, chained
try:
    config = load_config(path)
except FileNotFoundError as exc:
    raise ConfigurationError(f"Config not found: {path}") from exc

# GOOD — specific exception with logging
try:
    socket.send(data)
except zmq.ZMQError as e:
    logger.error(f"Send failed: {e}")

# BAD — bare except
try:
    process()
except:  # CRITICAL: never do this
    pass

# BAD — broad exception without reason
try:
    process()
except Exception:
    pass
```

Rules:
- Always catch specific exception types
- Chain exceptions with `raise ... from exc`
- Use `logger.exception()` for unexpected errors
- Define custom exceptions with docstrings

## Logging

Use loguru, not stdlib `logging` or `print()`.

```python
from loguru import logger

# GOOD
logger.info(f"Client {client_no} connected to room {room}")
logger.debug(f"Processing {len(data)} bytes")
logger.warning(f"Slow client detected: {client_no}")
logger.error(f"Connection failed: {error}")

# BAD
print(f"Client connected")  # Never use print()
import logging              # Never use stdlib logging
```

## Naming Conventions

| Type | Convention | Example |
|------|-----------|---------|
| Functions/variables | `snake_case` | `transform_data`, `get_version()` |
| Classes | `PascalCase` | `ServerConfig`, `SendStatus` |
| Constants | `UPPER_SNAKE_CASE` | `PROTOCOL_VERSION = 3`, `MSG_CLIENT_POSE = 11` |
| Private | `_prefix` | `_is_stealth_client()` |
| Modules | `snake_case` | `binary_serializer.py` |

## Dataclass Usage

Use `@dataclass` for structured data:

```python
@dataclass
class transform_data:
    """Transform data for a single client pose."""
    position: list[float] = field(default_factory=lambda: [0.0, 0.0, 0.0])
    rotation: list[float] = field(default_factory=lambda: [0.0, 0.0, 0.0, 1.0])
    client_no: int | None = None
```

## Constants & Enums

Define protocol constants at module level:

```python
PROTOCOL_VERSION = 3
MSG_CLIENT_POSE = 11
MSG_ROOM_POSE = 12
MAX_VIRTUAL_TRANSFORMS = 50
```

Use `Enum` for enumerated types:

```python
class SendStatus(Enum):
    SENT = "sent"
    BACKPRESSURE = "backpressure"
    FATAL = "fatal"
```

## Test Organization

- Location: `tests/` directory
- Integration tests: `tests/integration/`
- Naming: `test_<feature>_<scenario>.py`
- Framework: pytest
- Run: `pytest --cov=src`

## Python Version

- Minimum: Python 3.11+
- Use modern syntax: match statements, `str | None`, `list[T]`
- Version check enforced at server startup

## Pre-Commit Checklist

```bash
black src/ tests/ && ruff check src/ tests/ && mypy src/ && pytest
```

All four must pass before committing Python changes.
