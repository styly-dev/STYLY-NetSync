from __future__ import annotations

from collections.abc import Callable
from threading import RLock


class EventHook:
    """Simple observer pattern helper."""

    def __init__(self) -> None:
        self._handlers: list[Callable[..., None]] = []
        self._lock = RLock()

    def register(self, handler: Callable[..., None]) -> Callable[[], None]:
        """Register a callback and return an unsubscriber."""
        with self._lock:
            self._handlers.append(handler)

        def unsubscribe() -> None:
            with self._lock:
                try:
                    self._handlers.remove(handler)
                except ValueError:
                    pass

        return unsubscribe

    def fire(self, *args, **kwargs) -> None:
        with self._lock:
            handlers = list(self._handlers)
        for handler in handlers:
            handler(*args, **kwargs)
