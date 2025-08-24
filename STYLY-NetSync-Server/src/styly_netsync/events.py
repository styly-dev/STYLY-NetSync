from typing import Callable, List, Any

class EventHandler:
    def __init__(self):
        self._listeners: List[Callable] = []

    def add(self, listener: Callable) -> Callable[[], None]:
        """Add a listener and return a function to remove it."""
        self._listeners.append(listener)

        def unsubscribe():
            if listener in self._listeners:
                self._listeners.remove(listener)

        return unsubscribe

    def fire(self, *args: Any, **kwargs: Any):
        """Call all listeners with the given arguments."""
        for listener in self._listeners:
            try:
                listener(*args, **kwargs)
            except Exception as e:
                print(f"Error in event listener: {e}")
