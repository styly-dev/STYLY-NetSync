"""
STYLY NetSync Python Client Implementation

Provides a Python client API that mirrors Unity NetSync capabilities using ZeroMQ 
networking and binary protocol compatibility with the server.
"""

import json
import socket
import struct
import threading
import time
from typing import Dict, List, Optional, Callable, Tuple, Any
import uuid

try:
    import zmq
except ImportError:
    raise ImportError("pyzmq is required for NetSync client. Install with: pip install pyzmq")

from . import binary_serializer
from .types import (
    transform, client_transform, room_snapshot, device_mapping,
    rpc_event, network_variable_event, client_variable_event
)
from .adapters import (
    client_transform_to_wire, client_transform_from_wire, room_snapshot_from_wire,
    device_mappings_from_wire, create_stealth_handshake
)
from .events import EventManager


class net_sync_manager:
    """
    Python NetSync client manager with pull-based transform consumption.
    
    Provides real-time synchronization of transforms, RPC calls, and network variables
    with a NetSync server using ZeroMQ networking and binary serialization.
    
    Usage:
        manager = create_manager(server="tcp://localhost", room="demo")
        manager.start()
        
        # Pull-based transform consumption
        snapshot = manager.latest_room()
        if snapshot:
            for client_no, client_tx in snapshot.clients.items():
                print(f"Client {client_no} head Y: {client_tx.head.pos_y}")
        
        manager.stop()
    """
    
    def __init__(self, server: str = "tcp://localhost", dealer_port: int = 5555, 
                 sub_port: int = 5556, room: str = "default_room", 
                 auto_dispatch: bool = False, queue_max: int = 10000):
        """
        Initialize NetSync client manager.
        
        Args:
            server: ZeroMQ base address (e.g., "tcp://localhost")
            dealer_port: Uplink port to server ROUTER socket
            sub_port: Downlink port from server PUB socket
            room: Room topic to subscribe to
            auto_dispatch: If True, callbacks fire on recv thread; if False, require dispatch_pending()
            queue_max: Maximum queued events for RPC/NV pull or dispatch
        """
        self._server = server
        self._dealer_port = dealer_port
        self._sub_port = sub_port
        self._room = room
        self._auto_dispatch = auto_dispatch
        self._queue_max = queue_max
        
        # ZeroMQ context and sockets
        self._context: Optional[zmq.Context] = None
        self._dealer_socket: Optional[zmq.Socket] = None
        self._sub_socket: Optional[zmq.Socket] = None
        
        # Threading
        self._receive_thread: Optional[threading.Thread] = None
        self._discovery_thread: Optional[threading.Thread] = None
        self._running = False
        self._discovery_running = False
        self._lock = threading.RLock()
        
        # State management
        self._latest_room_snapshot: Optional[room_snapshot] = None
        self._device_mappings: Dict[int, device_mapping] = {}  # client_no -> mapping
        self._device_id_to_client_no: Dict[str, int] = {}     # device_id -> client_no
        
        # Network variables state
        self._global_variables: Dict[str, str] = {}
        self._client_variables: Dict[int, Dict[str, str]] = {}  # client_no -> {name: value}
        
        # Event management
        self._events = EventManager(queue_max)
        
        # Discovery
        self._discovery_socket: Optional[socket.socket] = None
        self._beacon_port = 9999
        
        # Statistics
        self._stats = {
            'messages_received': 0,
            'transforms_received': 0,
            'rpcs_received': 0,
            'last_snapshot_time': 0.0
        }
    
    # Properties
    @property
    def is_running(self) -> bool:
        """Whether the client is currently running."""
        return self._running
    
    @property
    def room(self) -> str:
        """Current room subscription."""
        return self._room
    
    @property
    def server(self) -> str:
        """Server base address.""" 
        return self._server
    
    @property
    def dealer_port(self) -> int:
        """Dealer (uplink) port."""
        return self._dealer_port
    
    @property
    def sub_port(self) -> int:
        """Subscriber (downlink) port."""
        return self._sub_port
    
    # Lifecycle methods
    def start(self) -> 'net_sync_manager':
        """
        Start the client manager.
        
        Connects DEALER/SUB sockets, subscribes to room, starts receive loop.
        
        Returns:
            Self for method chaining
        """
        with self._lock:
            if self._running:
                return self
            
            # Initialize ZeroMQ context
            self._context = zmq.Context()
            
            # Create DEALER socket for uplink
            self._dealer_socket = self._context.socket(zmq.DEALER)
            self._dealer_socket.setsockopt(zmq.LINGER, 0)  # Don't block on close
            dealer_addr = f"{self._server}:{self._dealer_port}"
            self._dealer_socket.connect(dealer_addr)
            
            # Create SUB socket for downlink  
            self._sub_socket = self._context.socket(zmq.SUB)
            self._sub_socket.setsockopt(zmq.LINGER, 0)  # Don't block on close
            sub_addr = f"{self._server}:{self._sub_port}"
            self._sub_socket.connect(sub_addr)
            
            # Subscribe to room topic
            self._sub_socket.setsockopt_string(zmq.SUBSCRIBE, self._room)
            
            # Start receive thread
            self._running = True
            self._receive_thread = threading.Thread(target=self._receive_loop, daemon=True)
            self._receive_thread.start()
            
            return self
    
    def stop(self) -> None:
        """Stop the client manager and close all connections."""
        with self._lock:
            if not self._running:
                return
            
            self._running = False
            
            # Stop discovery if running
            self.stop_discovery()
            
            # Wait for threads to finish
            if self._receive_thread and self._receive_thread.is_alive():
                self._receive_thread.join(timeout=1.0)
            
            # Close sockets
            if self._dealer_socket:
                self._dealer_socket.close()
                self._dealer_socket = None
                
            if self._sub_socket:
                self._sub_socket.close() 
                self._sub_socket = None
            
            # Terminate context
            if self._context:
                self._context.term()
                self._context = None
            
            # Clear event queues
            self._events.clear_all_queues()
    
    def close(self) -> None:
        """Alias for stop()."""
        self.stop()
    
    # Transform methods (pull-based)
    def latest_room(self) -> Optional[room_snapshot]:
        """
        Get the latest room snapshot.
        
        Returns a fresh, consolidated snapshot of the room from the most recent
        MSG_ROOM_TRANSFORM message. Contains only non-stealth clients.
        
        Returns:
            Latest room snapshot or None if no data received yet
        """
        with self._lock:
            return self._latest_room_snapshot
    
    def latest_client_transform(self, client_no: int) -> Optional[client_transform]:
        """
        Get the latest transform for a specific client.
        
        Args:
            client_no: Client number to look up
            
        Returns:
            Client transform or None if client not found in latest snapshot
        """
        snapshot = self.latest_room()
        if snapshot and client_no in snapshot.clients:
            return snapshot.clients[client_no]
        return None
    
    # Sending methods (uplink)
    def send_transform(self, tx: client_transform) -> bool:
        """
        Send local transform to server.
        
        Args:
            tx: Transform data to send
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self._running or not self._dealer_socket:
            return False
        
        try:
            # Convert to wire format and serialize
            wire_data = client_transform_to_wire(tx)
            payload = binary_serializer.serialize_client_transform(wire_data)
            
            # Send multipart message: [room, payload]
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload], zmq.NOBLOCK)
            return True
        except Exception:
            return False
    
    def send_stealth_handshake(self) -> bool:
        """
        Send stealth handshake to become invisible client.
        
        Sends NaN transform values to indicate this client should not appear
        in room broadcasts but should maintain connection for RPC/NV access.
        
        Returns:
            True if sent successfully, False otherwise
        """
        stealth_tx = create_stealth_handshake()
        return self.send_transform(stealth_tx)
    
    def rpc(self, function_name: str, args: List[str] | Tuple[str, ...]) -> bool:
        """
        Send RPC message to all clients in room.
        
        Args:
            function_name: Name of function to call
            args: List or tuple of string arguments
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self._running or not self._dealer_socket:
            return False
        
        try:
            # Convert args to list if needed
            args_list = list(args) if isinstance(args, tuple) else args
            
            # Create RPC data (sender client_no will be assigned by server)
            rpc_data = {
                'senderClientNo': 0,  # Server will assign
                'functionName': function_name,
                'argumentsJson': json.dumps(args_list)
            }
            
            payload = binary_serializer.serialize_rpc_message(rpc_data)
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload], zmq.NOBLOCK)
            return True
        except Exception:
            return False
    
    # Network Variable methods
    def set_global_variable(self, name: str, value: str) -> bool:
        """
        Set a global network variable.
        
        Args:
            name: Variable name (max 64 chars)
            value: Variable value (max 1024 chars)
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self._running or not self._dealer_socket:
            return False
        
        try:
            data = {
                'senderClientNo': 0,  # Server will assign
                'variableName': name[:64],
                'variableValue': value[:1024],
                'timestamp': time.time()
            }
            payload = binary_serializer.serialize_global_var_set(data)
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload], zmq.NOBLOCK)
            return True
        except Exception:
            return False
    
    def set_client_variable(self, target_client_no: int, name: str, value: str) -> bool:
        """
        Set a client-specific network variable.
        
        Args:
            target_client_no: Client number to set variable for
            name: Variable name (max 64 chars)
            value: Variable value (max 1024 chars)
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self._running or not self._dealer_socket:
            return False
        
        try:
            data = {
                'senderClientNo': 0,  # Server will assign
                'targetClientNo': target_client_no,
                'variableName': name[:64],
                'variableValue': value[:1024],
                'timestamp': time.time()
            }
            payload = binary_serializer.serialize_client_var_set(data)
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload], zmq.NOBLOCK)
            return True
        except Exception:
            return False
    
    def get_global_variable(self, name: str, default: Optional[str] = None) -> Optional[str]:
        """
        Get global network variable value from local cache.
        
        Args:
            name: Variable name
            default: Default value if not found
            
        Returns:
            Variable value or default
        """
        with self._lock:
            return self._global_variables.get(name, default)
    
    def get_client_variable(self, client_no: int, name: str, default: Optional[str] = None) -> Optional[str]:
        """
        Get client network variable value from local cache.
        
        Args:
            client_no: Client number
            name: Variable name  
            default: Default value if not found
            
        Returns:
            Variable value or default
        """
        with self._lock:
            client_vars = self._client_variables.get(client_no, {})
            return client_vars.get(name, default)
    
    # Event subscription methods
    def on_rpc_received(self, cb: Callable[[int, str, List[str]], None]) -> Callable[[], None]:
        """
        Subscribe to RPC events.
        
        Args:
            cb: Callback function (sender_client_no, function_name, args)
            
        Returns:
            Unsubscribe function
        """
        return self._events.on_rpc_received(cb)
    
    def on_global_variable_changed(self, cb: Callable[[str, Optional[str], str, dict], None]) -> Callable[[], None]:
        """
        Subscribe to global variable change events.
        
        Args:
            cb: Callback function (name, old_value, new_value, meta)
            
        Returns:
            Unsubscribe function
        """
        return self._events.on_global_variable_changed(cb)
    
    def on_client_variable_changed(self, cb: Callable[[int, str, Optional[str], str, dict], None]) -> Callable[[], None]:
        """
        Subscribe to client variable change events.
        
        Args:
            cb: Callback function (client_no, name, old_value, new_value, meta)
            
        Returns:
            Unsubscribe function
        """
        return self._events.on_client_variable_changed(cb)
    
    # Event dispatch
    def dispatch_pending(self, max_items: int = 100) -> int:
        """
        Process queued callback work on the caller's thread.
        
        Only needed when auto_dispatch=False. Processes RPC and network variable
        events that have been queued for thread-safe callback execution.
        
        Args:
            max_items: Maximum events to process
            
        Returns:
            Number of events processed
        """
        return self._events.dispatch_pending(max_items)
    
    # Device mapping methods
    def get_client_no(self, device_id: str) -> Optional[int]:
        """
        Get client number for device ID.
        
        Args:
            device_id: Device UUID string
            
        Returns:
            Client number or None if not found
        """
        with self._lock:
            return self._device_id_to_client_no.get(device_id)
    
    def get_device_id(self, client_no: int) -> Optional[str]:
        """
        Get device ID for client number.
        
        Args:
            client_no: Client number
            
        Returns:
            Device UUID string or None if not found
        """
        with self._lock:
            mapping = self._device_mappings.get(client_no)
            return mapping.device_id if mapping else None
    
    # Discovery methods
    def start_discovery(self, beacon_port: int = 9999) -> None:
        """
        Start UDP server discovery.
        
        Broadcasts discovery requests to find NetSync servers on the local network.
        When a server responds, the manager can auto-connect/update endpoints.
        
        Args:
            beacon_port: UDP port to send discovery requests to
        """
        with self._lock:
            if self._discovery_running:
                return
            
            self._beacon_port = beacon_port
            self._discovery_running = True
            self._discovery_thread = threading.Thread(target=self._discovery_loop, daemon=True)
            self._discovery_thread.start()
    
    def stop_discovery(self) -> None:
        """Stop UDP server discovery."""
        with self._lock:
            if not self._discovery_running:
                return
            
            self._discovery_running = False
            
            if self._discovery_socket:
                try:
                    self._discovery_socket.close()
                except:
                    pass
                self._discovery_socket = None
            
            if self._discovery_thread and self._discovery_thread.is_alive():
                self._discovery_thread.join(timeout=1.0)
    
    # Statistics
    def stats(self) -> dict:
        """
        Get client statistics.
        
        Returns:
            Dictionary with message counts, timing info, etc.
        """
        with self._lock:
            return self._stats.copy()
    
    # Private methods
    def _receive_loop(self):
        """Main receive loop running on background thread."""
        while self._running:
            try:
                if not self._sub_socket:
                    break
                
                # Check for messages with short timeout
                if self._sub_socket.poll(100, zmq.POLLIN):
                    # Receive multipart message: [room, payload]
                    frames = self._sub_socket.recv_multipart(zmq.NOBLOCK)
                    if len(frames) >= 2:
                        room = frames[0].decode('utf-8')
                        payload = frames[1]
                        
                        if room == self._room:
                            self._process_message(payload)
                            
            except zmq.Again:
                continue  # Timeout, check running flag
            except Exception:
                if self._running:
                    # Sleep briefly on error to avoid tight loop
                    time.sleep(0.1)
    
    def _process_message(self, payload: bytes):
        """Process received message payload."""
        try:
            msg_type, data, _ = binary_serializer.deserialize(payload)
            
            if data is None:
                return  # Invalid message
            
            with self._lock:
                self._stats['messages_received'] += 1
            
            if msg_type == binary_serializer.MSG_ROOM_TRANSFORM:
                self._handle_room_transform(data)
            elif msg_type == binary_serializer.MSG_RPC:
                self._handle_rpc(data)
            elif msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING:
                self._handle_device_mapping(data)
            elif msg_type == binary_serializer.MSG_GLOBAL_VAR_SYNC:
                self._handle_global_var_sync(data)
            elif msg_type == binary_serializer.MSG_CLIENT_VAR_SYNC:
                self._handle_client_var_sync(data)
                
        except Exception:
            # Silently ignore malformed messages
            pass
    
    def _handle_room_transform(self, data: dict):
        """Handle MSG_ROOM_TRANSFORM message."""
        try:
            # Convert wire format to snake_case snapshot
            snapshot = room_snapshot_from_wire(data)
            snapshot.timestamp = time.monotonic()
            
            with self._lock:
                self._latest_room_snapshot = snapshot
                self._stats['transforms_received'] += 1
                self._stats['last_snapshot_time'] = snapshot.timestamp
                
        except Exception:
            pass  # Ignore malformed transforms
    
    def _handle_rpc(self, data: dict):
        """Handle MSG_RPC message."""
        try:
            sender_client_no = data.get('senderClientNo', 0)
            function_name = data.get('functionName', '')
            args_json = data.get('argumentsJson', '[]')
            
            # Parse JSON args
            try:
                args = json.loads(args_json)
                if not isinstance(args, list):
                    args = []
            except:
                args = []
            
            with self._lock:
                self._stats['rpcs_received'] += 1
            
            # Emit event
            self._events.emit_rpc(sender_client_no, function_name, args, self._auto_dispatch)
            
        except Exception:
            pass  # Ignore malformed RPCs
    
    def _handle_device_mapping(self, data: dict):
        """Handle MSG_DEVICE_ID_MAPPING message."""
        try:
            mappings = device_mappings_from_wire(data)
            
            with self._lock:
                self._device_mappings.clear()
                self._device_id_to_client_no.clear()
                
                for mapping in mappings:
                    self._device_mappings[mapping.client_no] = mapping
                    self._device_id_to_client_no[mapping.device_id] = mapping.client_no
                    
        except Exception:
            pass  # Ignore malformed mappings
    
    def _handle_global_var_sync(self, data: dict):
        """Handle MSG_GLOBAL_VAR_SYNC message."""
        try:
            variables = data.get('variables', [])
            
            with self._lock:
                for var_data in variables:
                    name = var_data.get('name', '')
                    new_value = var_data.get('value', '')
                    timestamp = var_data.get('timestamp', 0.0)
                    last_writer = var_data.get('lastWriterClientNo', 0)
                    
                    old_value = self._global_variables.get(name)
                    self._global_variables[name] = new_value
                    
                    # Create metadata
                    meta = {
                        'timestamp': timestamp,
                        'last_writer_client_no': last_writer
                    }
                    
                    # Emit change event
                    self._events.emit_global_variable_changed(name, old_value, new_value, meta, self._auto_dispatch)
                    
        except Exception:
            pass  # Ignore malformed variable updates
    
    def _handle_client_var_sync(self, data: dict):
        """Handle MSG_CLIENT_VAR_SYNC message.""" 
        try:
            client_variables = data.get('clientVariables', {})
            
            with self._lock:
                for client_no_str, variables in client_variables.items():
                    try:
                        client_no = int(client_no_str)
                        
                        if client_no not in self._client_variables:
                            self._client_variables[client_no] = {}
                        
                        for var_data in variables:
                            name = var_data.get('name', '')
                            new_value = var_data.get('value', '')
                            timestamp = var_data.get('timestamp', 0.0)
                            last_writer = var_data.get('lastWriterClientNo', 0)
                            
                            old_value = self._client_variables[client_no].get(name)
                            self._client_variables[client_no][name] = new_value
                            
                            # Create metadata
                            meta = {
                                'timestamp': timestamp,
                                'last_writer_client_no': last_writer
                            }
                            
                            # Emit change event
                            self._events.emit_client_variable_changed(client_no, name, old_value, new_value, meta, self._auto_dispatch)
                            
                    except (ValueError, TypeError):
                        continue  # Skip invalid client numbers
                        
        except Exception:
            pass  # Ignore malformed variable updates
    
    def _discovery_loop(self):
        """UDP discovery loop running on background thread."""
        while self._discovery_running:
            try:
                # Create UDP socket for broadcast
                self._discovery_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                self._discovery_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
                self._discovery_socket.settimeout(1.0)
                
                # Send discovery request
                message = b"STYLY-NETSYNC-DISCOVER"
                self._discovery_socket.sendto(message, ('<broadcast>', self._beacon_port))
                
                # Listen for response
                try:
                    response, addr = self._discovery_socket.recvfrom(1024)
                    response_str = response.decode('utf-8')
                    
                    # Parse response: "STYLY-NETSYNC|dealerPort|subPort|serverName"
                    if response_str.startswith("STYLY-NETSYNC|"):
                        parts = response_str.split('|')
                        if len(parts) >= 4:
                            dealer_port = int(parts[1])
                            sub_port = int(parts[2])
                            server_name = parts[3]
                            
                            # Update connection info if different
                            server_addr = f"tcp://{addr[0]}"
                            if (server_addr != self._server or 
                                dealer_port != self._dealer_port or 
                                sub_port != self._sub_port):
                                
                                # Could trigger reconnection here
                                # For now just update internal state
                                with self._lock:
                                    self._server = server_addr
                                    self._dealer_port = dealer_port
                                    self._sub_port = sub_port
                                
                except socket.timeout:
                    pass  # No response, continue
                
                self._discovery_socket.close()
                self._discovery_socket = None
                
                # Wait before next discovery attempt
                time.sleep(5.0)
                
            except Exception:
                if self._discovery_socket:
                    try:
                        self._discovery_socket.close()
                    except:
                        pass
                    self._discovery_socket = None
                
                if self._discovery_running:
                    time.sleep(1.0)  # Brief delay on error


def create_manager(server: str = "tcp://localhost", dealer_port: int = 5555,
                  sub_port: int = 5556, room: str = "default_room",
                  auto_dispatch: bool = False, queue_max: int = 10000) -> net_sync_manager:
    """
    Factory function to create a NetSync client manager.
    
    Args:
        server: ZeroMQ base address (e.g., "tcp://localhost")
        dealer_port: Uplink port to server ROUTER socket
        sub_port: Downlink port from server PUB socket  
        room: Room topic to subscribe to
        auto_dispatch: If True, callbacks fire on recv thread; if False, require dispatch_pending()
        queue_max: Maximum queued events for RPC/NV pull or dispatch
        
    Returns:
        Configured NetSync client manager
    """
    return net_sync_manager(
        server=server,
        dealer_port=dealer_port, 
        sub_port=sub_port,
        room=room,
        auto_dispatch=auto_dispatch,
        queue_max=queue_max
    )