"""
Lightweight event system for NetSync client callbacks.

Provides subscription/unsubscription mechanism for RPC and Network Variable events.
"""

from collections import deque
from typing import Any, Callable, Deque, List, Optional, TypeVar
import threading
import weakref


T = TypeVar('T')
CallbackType = Callable[..., None]


class EventEmitter:
    """Thread-safe event emitter for handling callbacks."""
    
    def __init__(self, max_queue_size: int = 10000):
        self._callbacks: List[weakref.ref] = []
        self._event_queue: Deque[tuple] = deque()
        self._max_queue_size = max_queue_size
        self._lock = threading.RLock()
        
    def subscribe(self, callback: CallbackType) -> Callable[[], None]:
        """Subscribe to events. Returns unsubscribe function."""
        with self._lock:
            # Use weak reference to avoid circular references
            weak_ref = weakref.ref(callback)
            self._callbacks.append(weak_ref)
            
            def unsubscribe():
                with self._lock:
                    try:
                        self._callbacks.remove(weak_ref)
                    except ValueError:
                        pass  # Already removed
            
            return unsubscribe
    
    def emit(self, *args, **kwargs):
        """Emit event to all subscribers."""
        with self._lock:
            # Clean up dead weak references
            self._callbacks = [ref for ref in self._callbacks if ref() is not None]
            
            # Call all active callbacks
            for callback_ref in self._callbacks[:]:  # Copy list to avoid modification during iteration
                callback = callback_ref()
                if callback is not None:
                    try:
                        callback(*args, **kwargs)
                    except Exception:
                        # Silently ignore callback errors to prevent one bad callback
                        # from breaking the entire event system
                        pass
    
    def emit_queued(self, *args, **kwargs):
        """Queue event for later dispatch (thread-safe)."""
        with self._lock:
            # Drop oldest events if queue is full
            while len(self._event_queue) >= self._max_queue_size:
                self._event_queue.popleft()
            
            self._event_queue.append((args, kwargs))
    
    def dispatch_pending(self, max_items: int = 100) -> int:
        """Dispatch queued events on the caller's thread."""
        dispatched = 0
        
        with self._lock:
            # Process up to max_items events
            while dispatched < max_items and self._event_queue:
                args, kwargs = self._event_queue.popleft()
                dispatched += 1
                
                # Release lock during callback execution to prevent deadlock
                self._lock.release()
                try:
                    self.emit(*args, **kwargs)
                finally:
                    self._lock.acquire()
        
        return dispatched
    
    def clear_queue(self):
        """Clear all pending events."""
        with self._lock:
            self._event_queue.clear()
    
    @property
    def queue_size(self) -> int:
        """Get current queue size."""
        with self._lock:
            return len(self._event_queue)


class EventManager:
    """Manages multiple event emitters for different event types."""
    
    def __init__(self, max_queue_size: int = 10000):
        self._max_queue_size = max_queue_size
        self._rpc_emitter = EventEmitter(max_queue_size)
        self._global_var_emitter = EventEmitter(max_queue_size)
        self._client_var_emitter = EventEmitter(max_queue_size)
        self._lock = threading.RLock()
    
    # RPC Events
    def on_rpc_received(self, callback: Callable[[int, str, List[str]], None]) -> Callable[[], None]:
        """Subscribe to RPC events. Returns unsubscribe function."""
        return self._rpc_emitter.subscribe(callback)
    
    def emit_rpc(self, sender_client_no: int, function_name: str, args: List[str], auto_dispatch: bool = True):
        """Emit RPC event."""
        if auto_dispatch:
            self._rpc_emitter.emit(sender_client_no, function_name, args)
        else:
            self._rpc_emitter.emit_queued(sender_client_no, function_name, args)
    
    # Global Variable Events  
    def on_global_variable_changed(self, callback: Callable[[str, Optional[str], str, dict], None]) -> Callable[[], None]:
        """Subscribe to global variable change events. Returns unsubscribe function."""
        return self._global_var_emitter.subscribe(callback)
    
    def emit_global_variable_changed(self, name: str, old_value: Optional[str], new_value: str, 
                                   meta: dict, auto_dispatch: bool = True):
        """Emit global variable change event."""
        if auto_dispatch:
            self._global_var_emitter.emit(name, old_value, new_value, meta)
        else:
            self._global_var_emitter.emit_queued(name, old_value, new_value, meta)
    
    # Client Variable Events
    def on_client_variable_changed(self, callback: Callable[[int, str, Optional[str], str, dict], None]) -> Callable[[], None]:
        """Subscribe to client variable change events. Returns unsubscribe function.""" 
        return self._client_var_emitter.subscribe(callback)
    
    def emit_client_variable_changed(self, client_no: int, name: str, old_value: Optional[str], 
                                   new_value: str, meta: dict, auto_dispatch: bool = True):
        """Emit client variable change event."""
        if auto_dispatch:
            self._client_var_emitter.emit(client_no, name, old_value, new_value, meta)
        else:
            self._client_var_emitter.emit_queued(client_no, name, old_value, new_value, meta)
    
    def dispatch_pending(self, max_items: int = 100) -> int:
        """Dispatch all pending events. Returns total number dispatched."""
        total = 0
        total += self._rpc_emitter.dispatch_pending(max_items)
        remaining = max_items - total
        if remaining > 0:
            total += self._global_var_emitter.dispatch_pending(remaining)
        remaining = max_items - total
        if remaining > 0:
            total += self._client_var_emitter.dispatch_pending(remaining)
        return total
    
    def clear_all_queues(self):
        """Clear all pending events."""
        self._rpc_emitter.clear_queue()
        self._global_var_emitter.clear_queue()
        self._client_var_emitter.clear_queue()
    
    @property
    def total_queue_size(self) -> int:
        """Get total number of queued events across all emitters."""
        return (self._rpc_emitter.queue_size + 
                self._global_var_emitter.queue_size + 
                self._client_var_emitter.queue_size)