"""
NetSync Manager-based client implementation for STYLY NetSync benchmarking.

This module provides a client that uses the styly_netsync package's net_sync_manager
class, wrapped to be compatible with the Locust benchmarking framework.
"""

import json
import logging
import math
import time
import uuid
import threading
from typing import Dict, List, Optional, Any

# Import STYLY NetSync package modules
import sys
import os
sys.path.append(os.path.join(os.path.dirname(__file__), '..', '..', '..', 'src'))

from styly_netsync.client import net_sync_manager
from styly_netsync.types import client_transform_data, transform_data

from src.benchmark_config import config
from src.metrics_collector import MetricsCollector
from src.clients.client_interface import INetSyncClient

logger = logging.getLogger(__name__)


class NetSyncManagerClient(INetSyncClient):
    """
    NetSync Manager-based implementation of the NetSync client interface.
    
    This client uses the styly_netsync package's net_sync_manager class
    for communication with the server, providing a higher-level API.
    """
    
    def __init__(self, user_id: Optional[str] = None, metrics_collector: Optional[MetricsCollector] = None):
        self.user_id = user_id or f"benchmark_user_{uuid.uuid4().hex[:8]}"
        self.device_id = str(uuid.uuid4())
        self.client_no: Optional[int] = None
        self.metrics = metrics_collector or MetricsCollector()
        
        # NetSync manager instance
        self.manager: Optional[net_sync_manager] = None
        
        # Connection state
        self.connected = False
        
        # Transform simulation state (same as raw client)
        self.position_x = 0.0
        self.position_z = 0.0
        self.rotation_y = 0.0
        self.simulation_time = 0.0
        
        # Timing for periodic updates
        self.last_transform_update = 0.0
        
        # Message tracking for latency measurement
        self.sent_message_ids: Dict[str, tuple[float, str]] = {}
        
        # Received data tracking
        self.received_transforms: List[Dict] = []
        self.received_rpcs: List[Dict] = []
        
        # Threading
        self._lock = threading.RLock()
        self._polling_thread: Optional[threading.Thread] = None
        self._polling_running = False
        
        # Callbacks for metrics recording
        self.on_transform_received = None
        self.on_rpc_response_received = None
        
        logger.info(f"NetSyncManagerClient initialized: user_id={self.user_id}, device_id={self.device_id[:8]}...")
    
    def connect(self) -> bool:
        """Connect to the STYLY NetSync server using the net_sync_manager API."""
        try:
            if self.connected:
                return True
            
            # Create manager instance with server configuration
            self.manager = net_sync_manager(
                server=f"tcp://{config.server_address}",
                dealer_port=config.dealer_port,
                sub_port=config.sub_port,
                room=config.room_id,
                auto_dispatch=False  # We'll handle callbacks manually
            )
            
            # Override device_id to match our simulation
            self.manager._device_id = self.device_id
            
            # Start the connection
            self.manager.start()
            
            # Wait a bit for connection to establish
            time.sleep(1.0)
            
            # Setup event handlers
            self._setup_event_handlers()
            
            # Start polling thread for transform updates
            self._polling_running = True
            self._polling_thread = threading.Thread(target=self._polling_loop, daemon=True)
            self._polling_thread.start()
            
            self.connected = True
            
            logger.info(f"Connected to STYLY NetSync server via net_sync_manager")
            return True
            
        except Exception as e:
            logger.error(f"Failed to connect to server: {e}")
            self.metrics.record_connection_error()
            self.connected = False
            return False
    
    def disconnect(self):
        """Disconnect from the server."""
        self._polling_running = False
        
        if self._polling_thread and self._polling_thread.is_alive():
            self._polling_thread.join(timeout=2.0)
        
        if self.manager:
            self.manager.stop()
            self.manager = None
        
        self.connected = False
        logger.info(f"Disconnected from server: {self.user_id}")
    
    def send_transform_update(self) -> bool:
        """Send a transform update to the server."""
        if not self.connected or not self.manager or not self.manager.is_running:
            return False
        
        try:
            current_time = time.time()
            
            # Update simulation time and position
            dt = current_time - self.last_transform_update if self.last_transform_update > 0 else 1/config.transform_update_rate
            self.simulation_time += dt
            
            # Circular movement simulation (same as raw client)
            angle = self.simulation_time * config.movement_speed
            self.position_x = config.movement_radius * math.cos(angle)
            self.position_z = config.movement_radius * math.sin(angle)
            self.rotation_y = math.degrees(angle)
            
            # Build transform data using the net_sync_manager's data structure
            tx = client_transform_data(
                device_id=self.device_id,
                client_no=self.client_no,
                physical=transform_data(
                    pos_x=self.position_x,
                    pos_y=0.0,
                    pos_z=self.position_z,
                    rot_x=0.0,
                    rot_y=self.rotation_y,
                    rot_z=0.0,
                    is_local_space=True
                ),
                head=transform_data(
                    pos_x=self.position_x,
                    pos_y=1.6,  # Head height
                    pos_z=self.position_z,
                    rot_x=0.0,
                    rot_y=self.rotation_y,
                    rot_z=0.0,
                    is_local_space=False
                ),
                right_hand=transform_data(
                    pos_x=self.position_x + 0.3,
                    pos_y=1.2,
                    pos_z=self.position_z,
                    rot_x=0.0,
                    rot_y=0.0,
                    rot_z=0.0,
                    is_local_space=False
                ),
                left_hand=transform_data(
                    pos_x=self.position_x - 0.3,
                    pos_y=1.2,
                    pos_z=self.position_z,
                    rot_x=0.0,
                    rot_y=0.0,
                    rot_z=0.0,
                    is_local_space=False
                ),
                virtuals=[]
            )
            
            # Send using net_sync_manager API
            success = self.manager.send_transform(tx)
            
            if success:
                # Record metrics
                message_id = f"transform_{self.device_id}_{current_time}"
                self.metrics.record_message_sent(message_id, "transform", 0)  # Size not available from manager API
                self.last_transform_update = current_time
                
                if config.detailed_logging:
                    logger.debug(f"Sent transform: pos=({self.position_x:.2f}, {self.position_z:.2f}), rot={self.rotation_y:.1f}")
            
            return success
            
        except Exception as e:
            logger.error(f"Failed to send transform update: {e}")
            self.metrics.record_connection_error()
            return False
    
    def send_rpc(self, function_name: str, args: List[Any]) -> bool:
        """Send an RPC message."""
        if not self.connected or not self.manager or not self.manager.is_running:
            return False
        
        try:
            current_time = time.time()
            
            # Generate unique message_id for latency tracking
            message_id = f"rpc_{self.device_id}_{uuid.uuid4().hex[:8]}_{int(current_time * 1000)}"
            
            # Add message_id as the first argument for latency tracking
            args_with_message_id = [message_id] + list(args)
            
            # Convert args to string format as expected by the net_sync_manager API
            args_str = [str(arg) for arg in args_with_message_id]
            
            # Send using net_sync_manager API
            success = self.manager.rpc(function_name, args_str)
            
            if success:
                # Record metrics
                self.metrics.record_message_sent(message_id, "rpc", 0)  # Size not available
                
                if config.detailed_logging:
                    logger.debug(f"Sent RPC: {function_name}({args_with_message_id}) with message_id={message_id}")
            
            return success
            
        except Exception as e:
            logger.error(f"Failed to send RPC: {e}")
            self.metrics.record_connection_error()
            return False
    
    def get_received_data_summary(self) -> Dict[str, Any]:
        """Get a summary of received data for analysis."""
        with self._lock:
            # Get stats from the net_sync_manager if available
            manager_stats = {}
            if self.manager:
                manager_stats = self.manager.get_stats()
            
            return {
                'transforms_count': len(self.received_transforms),
                'rpcs_count': len(self.received_rpcs),
                'client_no': self.client_no,
                'connected': self.connected,
                'position': {
                    'x': self.position_x,
                    'z': self.position_z,
                    'rotation_y': self.rotation_y
                },
                'manager_stats': manager_stats
            }
    
    def _setup_event_handlers(self):
        """Setup event handlers for the net_sync_manager."""
        if not self.manager:
            return
        
        # RPC handler
        def handle_rpc(sender_client_no: int, function_name: str, args: List[Any]):
            try:
                current_time = time.time()
                
                # Try to extract message_id for latency tracking
                message_id = None
                if args and isinstance(args[0], str) and args[0].startswith('rpc_'):
                    message_id = args[0]
                
                # Record metrics
                if message_id:
                    result = self.metrics.record_message_received(message_id, "rpc", 0)
                    if self.on_rpc_response_received and result.latency_ms is not None:
                        self.on_rpc_response_received(result.latency_ms, function_name, message_id)
                
                # Store received RPC
                with self._lock:
                    self.received_rpcs.append({
                        'timestamp': current_time,
                        'sender_client_no': sender_client_no,
                        'function_name': function_name,
                        'args': args,
                        'message_id': message_id
                    })
                
                if config.detailed_logging:
                    logger.debug(f"Received RPC: {function_name} from client {sender_client_no}")
                    
            except Exception as e:
                logger.error(f"Error handling RPC: {e}")
        
        self.manager.on_rpc_received.add_listener(handle_rpc)
        
        # Avatar connected handler
        def handle_avatar_connected(client_no: int):
            try:
                if config.detailed_logging:
                    logger.debug(f"Avatar connected: client {client_no}")
            except Exception as e:
                logger.error(f"Error handling avatar connected: {e}")
        
        self.manager.on_avatar_connected.add_listener(handle_avatar_connected)
        
        # Client disconnected handler  
        def handle_client_disconnected(client_no: int):
            try:
                if config.detailed_logging:
                    logger.debug(f"Client disconnected: client {client_no}")
            except Exception as e:
                logger.error(f"Error handling client disconnected: {e}")
        
        self.manager.on_client_disconnected.add_listener(handle_client_disconnected)
    
    def _polling_loop(self):
        """Background thread for polling transform updates and dispatching events."""
        logger.debug(f"Polling loop started for {self.user_id}")
        
        while self._polling_running:
            try:
                if not self.manager or not self.manager.is_running:
                    break
                
                # Poll for transform updates
                room_snapshot = self.manager.get_room_transform_data()
                if room_snapshot:
                    # Update client number if not set
                    if self.client_no is None:
                        self.client_no = self.manager.client_no
                        if self.client_no:
                            logger.info(f"Received client number: {self.client_no} for {self.user_id}")
                    
                    # Record transform reception
                    client_count = len(room_snapshot.clients)
                    with self._lock:
                        self.received_transforms = list(room_snapshot.clients.values())
                    
                    # Call metrics recording callback if available
                    if self.on_transform_received:
                        try:
                            self.on_transform_received(client_count)
                        except Exception as e:
                            logger.warning(f"Error in transform received callback: {e}")
                    
                    # Record in metrics collector
                    self.metrics.record_message_received(None, "room_transform", 0)
                    
                    if config.detailed_logging:
                        logger.debug(f"Received room transform with {client_count} clients")
                
                # Dispatch pending events (RPC, NV, etc.)
                self.manager.dispatch_pending_events(max_items=10)
                
                # Small sleep to prevent busy waiting
                time.sleep(0.01)
                
            except Exception as e:
                if self._polling_running:
                    logger.error(f"Error in polling loop: {e}")
                    self.metrics.record_connection_error()
        
        logger.debug(f"Polling loop ended for {self.user_id}")
    
    def __del__(self):
        """Cleanup when the client is destroyed."""
        try:
            self.disconnect()
        except:
            pass