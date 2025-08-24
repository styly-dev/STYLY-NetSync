"""
Lightweight observer pattern implementation for STYLY NetSync client events.

Provides simple subscription/unsubscription mechanism for client callbacks
without external dependencies.
"""

from typing import Callable, Dict, List, Any
import threading
from functools import wraps
import logging

logger = logging.getLogger(__name__)


class EventEmitter:
    """Thread-safe event emitter with subscription management."""
    
    def __init__(self):
        self._callbacks: Dict[str, List[Callable]] = {}
        self._lock = threading.RLock()
    
    def subscribe(self, event_name: str, callback: Callable) -> None:
        """Subscribe a callback to an event.
        
        Args:
            event_name: Name of the event to subscribe to
            callback: Function to call when event is emitted
        """
        with self._lock:
            if event_name not in self._callbacks:
                self._callbacks[event_name] = []
            
            if callback not in self._callbacks[event_name]:
                self._callbacks[event_name].append(callback)
    
    def unsubscribe(self, event_name: str, callback: Callable) -> bool:
        """Unsubscribe a callback from an event.
        
        Args:
            event_name: Name of the event to unsubscribe from
            callback: Function to remove from subscribers
            
        Returns:
            True if callback was found and removed, False otherwise
        """
        with self._lock:
            if event_name in self._callbacks:
                try:
                    self._callbacks[event_name].remove(callback)
                    # Clean up empty event lists
                    if not self._callbacks[event_name]:
                        del self._callbacks[event_name]
                    return True
                except ValueError:
                    return False
            return False
    
    def unsubscribe_all(self, event_name: str) -> int:
        """Remove all callbacks for a specific event.
        
        Args:
            event_name: Name of the event to clear
            
        Returns:
            Number of callbacks that were removed
        """
        with self._lock:
            if event_name in self._callbacks:
                count = len(self._callbacks[event_name])
                del self._callbacks[event_name]
                return count
            return 0
    
    def clear(self) -> None:
        """Remove all event subscriptions."""
        with self._lock:
            self._callbacks.clear()
    
    def emit(self, event_name: str, *args, **kwargs) -> None:
        """Emit an event to all subscribers.
        
        Args:
            event_name: Name of the event to emit
            *args: Positional arguments to pass to callbacks
            **kwargs: Keyword arguments to pass to callbacks
        """
        callbacks = []
        with self._lock:
            if event_name in self._callbacks:
                # Make a copy to avoid issues if callbacks modify the list
                callbacks = self._callbacks[event_name].copy()
        
        # Call callbacks outside of lock to prevent deadlocks
        for callback in callbacks:
            try:
                callback(*args, **kwargs)
            except Exception as e:
                logger.error(f"Error in event callback for '{event_name}': {e}")
    
    def has_subscribers(self, event_name: str) -> bool:
        """Check if an event has any subscribers.
        
        Args:
            event_name: Name of the event to check
            
        Returns:
            True if there are subscribers, False otherwise
        """
        with self._lock:
            return event_name in self._callbacks and len(self._callbacks[event_name]) > 0
    
    def get_subscriber_count(self, event_name: str) -> int:
        """Get the number of subscribers for an event.
        
        Args:
            event_name: Name of the event to check
            
        Returns:
            Number of subscribers for the event
        """
        with self._lock:
            if event_name in self._callbacks:
                return len(self._callbacks[event_name])
            return 0


def safe_emit(func):
    """Decorator to safely emit events and handle exceptions."""
    @wraps(func)
    def wrapper(*args, **kwargs):
        try:
            return func(*args, **kwargs)
        except Exception as e:
            logger.error(f"Error emitting event in {func.__name__}: {e}")
    return wrapper


# Event name constants for type safety and consistency
class Events:
    """Constants for event names used by NetSyncManager."""
    
    # Connection events
    CONNECTED = "connected"
    DISCONNECTED = "disconnected"
    CONNECTION_FAILED = "connection_failed"
    
    # Transform events
    ROOM_UPDATED = "room_updated"
    CLIENT_JOINED = "client_joined"
    CLIENT_LEFT = "client_left"
    
    # RPC events
    RPC_RECEIVED = "rpc_received"
    
    # Network variable events
    GLOBAL_VARIABLE_CHANGED = "global_variable_changed"
    CLIENT_VARIABLE_CHANGED = "client_variable_changed"
    
    # Device mapping events
    DEVICE_MAPPINGS_UPDATED = "device_mappings_updated"
    
    # Error events
    ERROR = "error"