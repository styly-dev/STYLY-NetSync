"""
STYLY NetSync Client Manager

Provides a Python API that mirrors Unity NetSync capabilities:
- Pull-based transform model with latest snapshots
- RPC sending and receiving 
- Network variables (global and client-specific)
- Device ID ⇄ client number mapping
- Optional UDP server discovery
- Thread-safe ZeroMQ networking

Usage:
    manager = create_manager(room_id="default_room", device_id="python-client")
    manager.connect()
    
    # Get latest room state
    room = manager.latest_room()
    
    # Send transform update
    manager.send_transform(transform_data)
    
    # RPC
    manager.rpc("my_function", ["arg1", "arg2"])
    manager.subscribe(Events.RPC_RECEIVED, handle_rpc)
"""

import json
import socket
import struct
import threading
import time
import uuid
from typing import Optional, Dict, List, Any, Tuple
import logging

import zmq

from . import binary_serializer as bs
from .types import Transform, ClientTransform, RoomSnapshot, DeviceMapping, RPCMessage, NetworkVariable
from .adapters import (
    client_transform_to_wire, client_transform_from_wire, room_snapshot_from_wire,
    rpc_message_from_wire, device_mappings_from_wire, network_variables_from_wire,
    client_variables_from_wire, create_stealth_client_transform, is_stealth_client
)
from .events import EventEmitter, Events

logger = logging.getLogger(__name__)


class NetSyncManager:
    """Main client manager for STYLY NetSync networking.
    
    Provides pull-based transform synchronization, RPC, network variables,
    and device mapping using ZeroMQ sockets with binary protocol.
    """
    
    def __init__(
        self,
        room_id: str = "default_room",
        device_id: Optional[str] = None,
        server_host: str = "localhost",
        dealer_port: int = 5555,
        sub_port: int = 5556,
        beacon_port: int = 9999,
        use_discovery: bool = True,
        stealth_mode: bool = False
    ):
        """Initialize NetSync client manager.
        
        Args:
            room_id: Room identifier for multiplayer session
            device_id: Unique device identifier (auto-generated if None)
            server_host: Server hostname/IP (used if discovery fails)
            dealer_port: Server DEALER socket port for sending data
            sub_port: Server PUB socket port for receiving data  
            beacon_port: UDP beacon port for server discovery
            use_discovery: Whether to attempt UDP server discovery
            stealth_mode: Connect without visible avatar (NaN handshake)
        """
        self.room_id = room_id
        self.device_id = device_id or str(uuid.uuid4())
        self.server_host = server_host
        self.dealer_port = dealer_port
        self.sub_port = sub_port
        self.beacon_port = beacon_port
        self.use_discovery = use_discovery
        self.stealth_mode = stealth_mode
        
        # ZeroMQ context and sockets
        self.context: Optional[zmq.Context] = None
        self.dealer_socket: Optional[zmq.Socket] = None
        self.sub_socket: Optional[zmq.Socket] = None
        
        # Threading
        self._receive_thread: Optional[threading.Thread] = None
        self._receive_thread_running = False
        self._lock = threading.RLock()
        
        # State management
        self._connected = False
        self._client_no = 0
        self._latest_room: Optional[RoomSnapshot] = None
        self._device_mappings: Dict[int, DeviceMapping] = {}
        self._global_variables: Dict[str, NetworkVariable] = {}
        self._client_variables: Dict[int, Dict[str, NetworkVariable]] = {}
        
        # Event system
        self.events = EventEmitter()
        
        # Last transform send time for rate limiting
        self._last_transform_send = 0.0
        self._transform_send_rate = 10.0  # Hz
    
    def connect(self) -> bool:
        """Connect to STYLY NetSync server.
        
        Returns:
            True if connection successful, False otherwise
        """
        try:
            # Discover server if enabled
            if self.use_discovery:
                discovered = self._discover_server()
                if discovered:
                    logger.info(f"Discovered server at {self.server_host}:{self.dealer_port}/{self.sub_port}")
            
            # Create ZeroMQ context and sockets
            self.context = zmq.Context()
            
            # DEALER socket for sending data to server
            self.dealer_socket = self.context.socket(zmq.DEALER)
            self.dealer_socket.connect(f"tcp://{self.server_host}:{self.dealer_port}")
            
            # SUB socket for receiving broadcast data from server
            self.sub_socket = self.context.socket(zmq.SUB)
            self.sub_socket.connect(f"tcp://{self.server_host}:{self.sub_port}")
            self.sub_socket.setsockopt(zmq.SUBSCRIBE, b"")  # Subscribe to all messages
            
            # Set socket options for better performance
            self.dealer_socket.setsockopt(zmq.LINGER, 1000)
            self.sub_socket.setsockopt(zmq.LINGER, 1000)
            self.dealer_socket.setsockopt(zmq.SNDHWM, 100)
            self.sub_socket.setsockopt(zmq.RCVHWM, 100)
            
            # Start receive thread
            self._receive_thread_running = True
            self._receive_thread = threading.Thread(target=self._receive_loop, daemon=True)
            self._receive_thread.start()
            
            # Send initial handshake
            self._send_initial_handshake()
            
            with self._lock:
                self._connected = True
            
            self.events.emit(Events.CONNECTED)
            logger.info(f"Connected to STYLY NetSync server at {self.server_host}")
            return True
            
        except Exception as e:
            logger.error(f"Failed to connect to server: {e}")
            self.events.emit(Events.CONNECTION_FAILED, str(e))
            return False
    
    def disconnect(self) -> None:
        """Disconnect from STYLY NetSync server."""
        with self._lock:
            self._connected = False
        
        # Stop receive thread
        if self._receive_thread and self._receive_thread_running:
            self._receive_thread_running = False
            # Give thread time to exit gracefully
            self._receive_thread.join(timeout=1.0)
        
        # Close sockets
        if self.dealer_socket:
            self.dealer_socket.close()
            self.dealer_socket = None
        
        if self.sub_socket:
            self.sub_socket.close()
            self.sub_socket = None
        
        # Terminate context
        if self.context:
            self.context.term()
            self.context = None
        
        self.events.emit(Events.DISCONNECTED)
        logger.info("Disconnected from STYLY NetSync server")
    
    def is_connected(self) -> bool:
        """Check if client is connected to server."""
        with self._lock:
            return self._connected
    
    def get_client_no(self) -> int:
        """Get this client's assigned number."""
        with self._lock:
            return self._client_no
    
    def latest_room(self) -> Optional[RoomSnapshot]:
        """Get the latest room snapshot with all client transforms.
        
        This is the main API for pull-based transform updates.
        
        Returns:
            Latest RoomSnapshot or None if no data received yet
        """
        with self._lock:
            return self._latest_room
    
    def latest_client_transform(self, client_no: int) -> Optional[ClientTransform]:
        """Get the latest transform for a specific client.
        
        Args:
            client_no: Client number to get transform for
            
        Returns:
            ClientTransform for the client or None if not found
        """
        room = self.latest_room()
        if room and client_no in room.clients:
            return room.clients[client_no]
        return None
    
    def send_transform(self, client_transform: ClientTransform) -> bool:
        """Send transform update to server.
        
        Args:
            client_transform: Transform data to send
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self.is_connected():
            return False
        
        # Rate limiting
        now = time.time()
        if now - self._last_transform_send < (1.0 / self._transform_send_rate):
            return False
        
        try:
            # Convert to wire format and serialize
            wire_data = client_transform_to_wire(client_transform)
            message = bs.serialize_client_transform(wire_data)
            
            # Send via DEALER socket
            self.dealer_socket.send(message, zmq.NOBLOCK)
            self._last_transform_send = now
            return True
            
        except Exception as e:
            logger.error(f"Failed to send transform: {e}")
            return False
    
    def rpc(self, function_name: str, args: List[Any]) -> bool:
        """Send RPC message to all clients in room.
        
        Args:
            function_name: Name of function to call
            args: Arguments to pass to function
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self.is_connected():
            return False
        
        try:
            # Serialize arguments to JSON
            args_json = json.dumps(args)
            
            # Create RPC message
            rpc_data = {
                'senderClientNo': self._client_no,
                'functionName': function_name,
                'argumentsJson': args_json
            }
            
            message = bs.serialize_rpc_message(rpc_data)
            self.dealer_socket.send(message, zmq.NOBLOCK)
            return True
            
        except Exception as e:
            logger.error(f"Failed to send RPC: {e}")
            return False
    
    def set_global_variable(self, name: str, value: str) -> bool:
        """Set a global network variable.
        
        Args:
            name: Variable name (max 64 characters)
            value: Variable value (max 1024 characters)
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self.is_connected():
            return False
        
        try:
            var_data = {
                'senderClientNo': self._client_no,
                'variableName': name[:64],
                'variableValue': value[:1024],
                'timestamp': time.time()
            }
            
            message = bs.serialize_global_var_set(var_data)
            self.dealer_socket.send(message, zmq.NOBLOCK)
            return True
            
        except Exception as e:
            logger.error(f"Failed to set global variable: {e}")
            return False
    
    def set_client_variable(self, target_client_no: int, name: str, value: str) -> bool:
        """Set a client-specific network variable.
        
        Args:
            target_client_no: Client number to set variable for
            name: Variable name (max 64 characters) 
            value: Variable value (max 1024 characters)
            
        Returns:
            True if sent successfully, False otherwise
        """
        if not self.is_connected():
            return False
        
        try:
            var_data = {
                'senderClientNo': self._client_no,
                'targetClientNo': target_client_no,
                'variableName': name[:64],
                'variableValue': value[:1024],
                'timestamp': time.time()
            }
            
            message = bs.serialize_client_var_set(var_data)
            self.dealer_socket.send(message, zmq.NOBLOCK)
            return True
            
        except Exception as e:
            logger.error(f"Failed to set client variable: {e}")
            return False
    
    def get_global_variable(self, name: str, default: str = "") -> str:
        """Get a global network variable value.
        
        Args:
            name: Variable name
            default: Default value if variable not found
            
        Returns:
            Variable value or default
        """
        with self._lock:
            if name in self._global_variables:
                return self._global_variables[name].value
            return default
    
    def get_client_variable(self, client_no: int, name: str, default: str = "") -> str:
        """Get a client-specific network variable value.
        
        Args:
            client_no: Client number
            name: Variable name
            default: Default value if variable not found
            
        Returns:
            Variable value or default
        """
        with self._lock:
            if client_no in self._client_variables:
                client_vars = self._client_variables[client_no]
                if name in client_vars:
                    return client_vars[name].value
            return default
    
    def get_device_mapping(self, client_no: int) -> Optional[DeviceMapping]:
        """Get device mapping for a client number.
        
        Args:
            client_no: Client number to look up
            
        Returns:
            DeviceMapping or None if not found
        """
        with self._lock:
            return self._device_mappings.get(client_no)
    
    def is_client_stealth_mode(self, client_no: int) -> bool:
        """Check if a client is in stealth mode.
        
        Args:
            client_no: Client number to check
            
        Returns:
            True if client is in stealth mode, False otherwise
        """
        mapping = self.get_device_mapping(client_no)
        if mapping:
            return mapping.is_stealth_mode
        
        # Fallback: check transform data
        client_transform = self.latest_client_transform(client_no)
        if client_transform:
            return is_stealth_client(client_transform)
        
        return False
    
    def subscribe(self, event_name: str, callback) -> None:
        """Subscribe to an event.
        
        Args:
            event_name: Name of event (use Events constants)
            callback: Function to call when event occurs
        """
        self.events.subscribe(event_name, callback)
    
    def unsubscribe(self, event_name: str, callback) -> bool:
        """Unsubscribe from an event.
        
        Args:
            event_name: Name of event
            callback: Function to remove
            
        Returns:
            True if callback was removed, False if not found
        """
        return self.events.unsubscribe(event_name, callback)
    
    def set_transform_send_rate(self, rate_hz: float) -> None:
        """Set the maximum rate for sending transform updates.
        
        Args:
            rate_hz: Maximum sends per second (1-120 Hz)
        """
        self._transform_send_rate = max(1.0, min(120.0, rate_hz))
    
    def _discover_server(self) -> bool:
        """Attempt UDP server discovery.
        
        Returns:
            True if server discovered and host/ports updated, False otherwise
        """
        try:
            # Create UDP socket for discovery
            udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            udp_socket.settimeout(5.0)  # 5 second timeout
            
            # Send discovery request
            discovery_msg = b"STYLY-NETSYNC-DISCOVER"
            udp_socket.sendto(discovery_msg, ('<broadcast>', self.beacon_port))
            udp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            
            # Wait for response
            data, addr = udp_socket.recvfrom(1024)
            response = data.decode('utf-8')
            
            # Parse response: "STYLY-NETSYNC|dealerPort|subPort|serverName"
            if response.startswith("STYLY-NETSYNC|"):
                parts = response.split("|")
                if len(parts) >= 4:
                    self.server_host = addr[0]
                    self.dealer_port = int(parts[1])
                    self.sub_port = int(parts[2])
                    server_name = parts[3]
                    logger.info(f"Discovered server '{server_name}' at {addr[0]}")
                    return True
            
            udp_socket.close()
            return False
            
        except Exception as e:
            logger.debug(f"UDP discovery failed: {e}")
            return False
    
    def _send_initial_handshake(self) -> None:
        """Send initial handshake to join room."""
        if self.stealth_mode:
            # Send stealth handshake with NaN values
            stealth_transform = create_stealth_client_transform(self.device_id)
            self.send_transform(stealth_transform)
        else:
            # Send zero transform as initial handshake
            initial_transform = ClientTransform(
                client_no=0,  # Will be assigned by server
                device_id=self.device_id,
                physical=Transform(),
                head=Transform(),
                right_hand=Transform(),
                left_hand=Transform(),
                virtuals=[]
            )
            self.send_transform(initial_transform)
    
    def _receive_loop(self) -> None:
        """Main receive thread loop."""
        logger.debug("Receive thread started")
        
        while self._receive_thread_running:
            try:
                # Poll with timeout to allow graceful shutdown
                if self.sub_socket.poll(timeout=100):  # 100ms timeout
                    message = self.sub_socket.recv(zmq.NOBLOCK)
                    self._process_message(message)
                    
            except zmq.Again:
                # No message available, continue
                continue
            except Exception as e:
                if self._receive_thread_running:
                    logger.error(f"Error in receive loop: {e}")
                    self.events.emit(Events.ERROR, str(e))
                break
        
        logger.debug("Receive thread stopped")
    
    def _process_message(self, message: bytes) -> None:
        """Process incoming message from server."""
        try:
            msg_type, data, _ = bs.deserialize(message)
            
            if msg_type == bs.MSG_ROOM_TRANSFORM:
                self._handle_room_transform(data)
            elif msg_type == bs.MSG_RPC:
                self._handle_rpc_message(data)
            elif msg_type == bs.MSG_DEVICE_ID_MAPPING:
                self._handle_device_mappings(data)
            elif msg_type == bs.MSG_GLOBAL_VAR_SYNC:
                self._handle_global_variables(data)
            elif msg_type == bs.MSG_CLIENT_VAR_SYNC:
                self._handle_client_variables(data)
                
        except Exception as e:
            logger.error(f"Error processing message: {e}")
    
    def _handle_room_transform(self, data: Dict[str, Any]) -> None:
        """Handle room transform update."""
        if not data:
            return
        
        # Convert to RoomSnapshot
        room = room_snapshot_from_wire(data)
        
        # Check for new/removed clients
        old_clients = set()
        new_clients = set(room.clients.keys())
        
        with self._lock:
            if self._latest_room:
                old_clients = set(self._latest_room.clients.keys())
            
            self._latest_room = room
        
        # Emit client join/leave events
        joined = new_clients - old_clients
        left = old_clients - new_clients
        
        for client_no in joined:
            self.events.emit(Events.CLIENT_JOINED, client_no, room.clients[client_no])
        
        for client_no in left:
            self.events.emit(Events.CLIENT_LEFT, client_no)
        
        # Emit room update event
        self.events.emit(Events.ROOM_UPDATED, room)
    
    def _handle_rpc_message(self, data: Dict[str, Any]) -> None:
        """Handle RPC message."""
        if not data:
            return
        
        rpc = rpc_message_from_wire(data)
        
        # Parse arguments from JSON
        try:
            args = json.loads(rpc.arguments_json)
        except json.JSONDecodeError:
            args = []
        
        self.events.emit(Events.RPC_RECEIVED, rpc.sender_client_no, rpc.function_name, args)
    
    def _handle_device_mappings(self, data: Dict[str, Any]) -> None:
        """Handle device ID mappings update."""
        if not data:
            return
        
        mappings = device_mappings_from_wire(data)
        
        with self._lock:
            # Update mappings
            for mapping in mappings:
                self._device_mappings[mapping.client_no] = mapping
                
                # Check if this is our client number
                if mapping.device_id == self.device_id:
                    self._client_no = mapping.client_no
        
        self.events.emit(Events.DEVICE_MAPPINGS_UPDATED, mappings)
    
    def _handle_global_variables(self, data: Dict[str, Any]) -> None:
        """Handle global variables update."""
        if not data:
            return
        
        variables = network_variables_from_wire(data)
        
        with self._lock:
            # Update global variables and track changes
            for var in variables:
                old_value = self._global_variables.get(var.name)
                self._global_variables[var.name] = var
                
                # Emit change event
                old_val = old_value.value if old_value else None
                self.events.emit(Events.GLOBAL_VARIABLE_CHANGED, var.name, old_val, var.value)
    
    def _handle_client_variables(self, data: Dict[str, Any]) -> None:
        """Handle client variables update."""
        if not data:
            return
        
        client_vars = client_variables_from_wire(data)
        
        with self._lock:
            # Update client variables and track changes
            for client_no, variables in client_vars.items():
                if client_no not in self._client_variables:
                    self._client_variables[client_no] = {}
                
                for var in variables:
                    old_value = self._client_variables[client_no].get(var.name)
                    self._client_variables[client_no][var.name] = var
                    
                    # Emit change event
                    old_val = old_value.value if old_value else None
                    self.events.emit(Events.CLIENT_VARIABLE_CHANGED, client_no, var.name, old_val, var.value)


def create_manager(
    room_id: str = "default_room",
    device_id: Optional[str] = None,
    server_host: str = "localhost",
    dealer_port: int = 5555,
    sub_port: int = 5556,
    **kwargs
) -> NetSyncManager:
    """Create a new NetSyncManager instance.
    
    Convenience function for creating clients with common parameters.
    
    Args:
        room_id: Room identifier for multiplayer session
        device_id: Unique device identifier (auto-generated if None)
        server_host: Server hostname/IP
        dealer_port: Server DEALER socket port
        sub_port: Server PUB socket port
        **kwargs: Additional arguments passed to NetSyncManager
        
    Returns:
        Configured NetSyncManager instance
    """
    return NetSyncManager(
        room_id=room_id,
        device_id=device_id,
        server_host=server_host,
        dealer_port=dealer_port,
        sub_port=sub_port,
        **kwargs
    )