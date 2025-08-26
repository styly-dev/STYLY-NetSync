"""
Simple event system for NetSync client callbacks.
"""

from collections.abc import Callable
from typing import Any


class EventHandler:
    """Simple event handler that manages callbacks."""

    def __init__(self):
        self._callbacks: list[Callable] = []

    def add_listener(self, callback: Callable) -> Callable[[], None]:
        """Add a callback listener. Returns unsubscribe function."""
        self._callbacks.append(callback)

        def unsubscribe():
            if callback in self._callbacks:
                self._callbacks.remove(callback)

        return unsubscribe

    def remove_listener(self, callback: Callable) -> None:
        """Remove a callback listener."""
        if callback in self._callbacks:
            self._callbacks.remove(callback)

    def invoke(self, *args: Any, **kwargs: Any) -> None:
        """Invoke all registered callbacks."""
        for callback in self._callbacks[
            :
        ]:  # Copy to avoid modification during iteration
            try:
                callback(*args, **kwargs)
            except Exception:
                # Continue with other callbacks even if one fails
                pass

    def clear(self) -> None:
        """Remove all callbacks."""
        self._callbacks.clear()
