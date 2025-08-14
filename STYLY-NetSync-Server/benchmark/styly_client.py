"""
STYLY NetSync client implementation for Locust benchmarking.

This module provides a ZeroMQ-based client that communicates with the STYLY NetSync server
and integrates with Locust for load testing. It includes transform synchronization,
network variables, and RPC functionality with comprehensive metrics collection.
"""

import json
import logging
import math
import time
import uuid
import threading
from typing import Dict, List, Optional, Tuple, Any
from datetime import datetime

#import zmq (zmq.green: green thread version so that it is compatible with Locust)
import zmq.green as zmq

# Import STYLY NetSync modules (from parent directory)
import sys
import os
sys.path.append(os.path.join(os.path.dirname(__file__), '..', 'src'))

from styly_netsync.binary_serializer import (
    MSG_DEVICE_ID_MAPPING,
    MSG_ROOM_TRANSFORM,
    MSG_RPC_BROADCAST,
    MSG_RPC_CLIENT,
    deserialize,
    serialize_client_transform,
    serialize_rpc_client_message,
    serialize_rpc_message,
    serialize_rpc_request,
)

from benchmark_config import config
from metrics_collector import MetricsCollector

logger = logging.getLogger(__name__)


class STYLYNetSyncClient:
    """
    STYLY NetSync client for Locust benchmarking.
    
    This client simulates a VR/MR user in a STYLY NetSync session, sending transform
    updates, network variables, and RPC messages while measuring performance metrics.
    """
    
    def __init__(self, user_id: Optional[str] = None, metrics_collector: Optional[MetricsCollector] = None):
        self.user_id = user_id or f"benchmark_user_{uuid.uuid4().hex[:8]}"
        self.device_id = str(uuid.uuid4())
        self.client_no: Optional[int] = None
        self.metrics = metrics_collector or MetricsCollector()
        
        # ZMQ setup
        self.context = zmq.Context()
        self.dealer_socket: Optional[zmq.Socket] = None
        self.sub_socket: Optional[zmq.Socket] = None
        self.poller = zmq.Poller()
        
        # Connection state
        self.connected = False
        self.last_connection_attempt = 0
        self.connection_retry_interval = 5.0  # seconds
        
        # Transform simulation state
        self.position_x = 0.0
        self.position_z = 0.0
        self.rotation_y = 0.0
        self.simulation_time = 0.0
        
        # Timing for periodic updates
        self.last_transform_update = 0.0
        self.last_rpc_update = 0.0
        
        # Message tracking for latency measurement
        self.sent_message_ids: Dict[str, Tuple[float, str]] = {}
        
        # Received data tracking
        self.received_transforms: List[Dict] = []
        self.received_rpcs: List[Dict] = []
        
        # Threading
        self.receive_thread: Optional[threading.Thread] = None
        self.running = False
        self._lock = threading.RLock()
        
        # Callback for metrics recording
        self.on_transform_received = None
        
        logger.info(f"STYLYNetSyncClient initialized: user_id={self.user_id}, device_id={self.device_id[:8]}...")
    
    def connect(self) -> bool:
        """Connect to the STYLY NetSync server."""
        try:
            if self.connected:
                return True
            
            current_time = time.time()
            if current_time - self.last_connection_attempt < self.connection_retry_interval:
                return False
            
            self.last_connection_attempt = current_time
            
            # Create and connect DEALER socket
            if self.dealer_socket:
                self.dealer_socket.close()
            
            self.dealer_socket = self.context.socket(zmq.DEALER)
            self.dealer_socket.setsockopt(zmq.LINGER, 0)
            self.dealer_socket.setsockopt(zmq.RCVTIMEO, 1000)  # 1 second timeout
            self.dealer_socket.setsockopt(zmq.SNDTIMEO, 1000)  # 1 second send timeout
            
            dealer_endpoint = f"tcp://{config.server_address}:{config.dealer_port}"
            
            # Connect with timeout
            self.dealer_socket.connect(dealer_endpoint)
            
            # Create and connect SUB socket
            if self.sub_socket:
                self.sub_socket.close()
            
            self.sub_socket = self.context.socket(zmq.SUB)
            self.sub_socket.setsockopt(zmq.LINGER, 0)
            self.sub_socket.setsockopt_string(zmq.SUBSCRIBE, config.room_id)
            
            sub_endpoint = f"tcp://{config.server_address}:{config.sub_port}"
            self.sub_socket.connect(sub_endpoint)
            
            # Give sockets time to establish connections
            time.sleep(1.0)
            
            # Setup poller for receiving
            self.poller.register(self.sub_socket, zmq.POLLIN)
            
            # Set connection state BEFORE starting thread
            self.connected = True
            self.running = True
            
            # Start receive thread ONLY after successful connection
            self.receive_thread = threading.Thread(target=self._receive_loop, daemon=True)
            self.receive_thread.start()
            
            logger.info(f"Connected to STYLY NetSync server: {dealer_endpoint}, {sub_endpoint}")
            return True
            
        except Exception as e:
            logger.error(f"Failed to connect to server: {e}")
            self.metrics.record_connection_error()
            self.connected = False
            return False
    
    def disconnect(self):
        """Disconnect from the server."""
        self.running = False
        self.connected = False
        
        if self.receive_thread and self.receive_thread.is_alive():
            self.receive_thread.join(timeout=2.0)
        
        if self.dealer_socket:
            self.dealer_socket.close()
            self.dealer_socket = None
        
        if self.sub_socket:
            self.sub_socket.close()
            self.sub_socket = None
        
        logger.info(f"Disconnected from server: {self.user_id}")
    
    def send_transform_update(self) -> bool:
        """Send a transform update to the server."""

        if not self.connected or not self.dealer_socket:
            return False
        
        try:
            current_time = time.time()
            
            # Update simulation time and position
            dt = current_time - self.last_transform_update if self.last_transform_update > 0 else 1/config.transform_update_rate
            self.simulation_time += dt
            
            # Circular movement simulation
            angle = self.simulation_time * config.movement_speed
            self.position_x = config.movement_radius * math.cos(angle)
            self.position_z = config.movement_radius * math.sin(angle)
            self.rotation_y = math.degrees(angle)
            
            # Build transform data
            transform_data = {
                'deviceId': self.device_id,
                'physical': {
                    'posX': self.position_x,
                    'posY': 0.0,
                    'posZ': self.position_z,
                    'rotX': 0.0,
                    'rotY': self.rotation_y,
                    'rotZ': 0.0,
                    'isLocalSpace': True
                },
                'head': {
                    'posX': self.position_x,
                    'posY': 1.6,  # Head height
                    'posZ': self.position_z,
                    'rotX': 0.0,
                    'rotY': self.rotation_y,
                    'rotZ': 0.0,
                    'isLocalSpace': False
                },
                'rightHand': {
                    'posX': self.position_x + 0.3,
                    'posY': 1.2,
                    'posZ': self.position_z,
                    'rotX': 0.0,
                    'rotY': 0.0,
                    'rotZ': 0.0,
                    'isLocalSpace': False
                },
                'leftHand': {
                    'posX': self.position_x - 0.3,
                    'posY': 1.2,
                    'posZ': self.position_z,
                    'rotX': 0.0,
                    'rotY': 0.0,
                    'rotZ': 0.0,
                    'isLocalSpace': False
                },
                'virtuals': []
            }
            
            # Serialize and send
            message_bytes = serialize_client_transform(transform_data)
            message_id = f"transform_{self.device_id}_{current_time}"
            self.dealer_socket.send_multipart([
                config.room_id.encode('utf-8'),
                message_bytes,
            ], flags=zmq.NOBLOCK)

            # Record metrics
            self.metrics.record_message_sent(message_id, "transform", len(message_bytes))
            self.last_transform_update = current_time
            
            if config.detailed_logging:
                logger.info(f"Sent transform: pos=({self.position_x:.2f}, {self.position_z:.2f}), rot={self.rotation_y:.1f}")
            
            return True
            
        except zmq.Again:
            logger.error(f"Failed to send transform update: {e}")
            self.metrics.record_connection_error()
            return False

        except Exception as e:
            logger.error(f"Failed to send transform update: {e}")
            self.metrics.record_connection_error()
            return False
    
    def send_rpc(self, function_name: str, args: List[Any], rpc_type: str = "broadcast") -> bool:
        """Send an RPC message."""
        if not self.connected or not self.dealer_socket or not self.client_no:
            return False
        
        try:
            current_time = time.time()
            
            if rpc_type == "broadcast":
                # Broadcast RPC
                rpc_data = {
                    'senderClientNo': self.client_no,
                    'functionName': function_name,
                    'argumentsJson': json.dumps(args)
                }
                message_bytes = serialize_rpc_message(rpc_data)
            elif rpc_type == "server":
                # Server RPC
                rpc_data = {
                    'senderClientNo': self.client_no,
                    'functionName': function_name,
                    'argumentsJson': json.dumps(args)
                }
                message_bytes = serialize_rpc_request(rpc_data)
            elif rpc_type == "client":
                # Client-to-client RPC (to self for testing)
                rpc_data = {
                    'senderClientNo': self.client_no,
                    'targetClientNo': self.client_no,
                    'functionName': function_name,
                    'argumentsJson': json.dumps(args)
                }
                message_bytes = serialize_rpc_client_message(rpc_data)
            else:
                logger.error(f"Unknown RPC type: {rpc_type}")
                return False
            
            message_id = f"rpc_{rpc_type}_{self.device_id}_{current_time}"
            
            self.dealer_socket.send_multipart([
                config.room_id.encode('utf-8'),
                message_bytes
            ])
            
            # Record metrics
            self.metrics.record_message_sent(message_id, f"rpc_{rpc_type}", len(message_bytes))
            
            if config.detailed_logging:
                logger.debug(f"Sent {rpc_type} RPC: {function_name}({args})")
            
            return True
            
        except Exception as e:
            logger.error(f"Failed to send RPC: {e}")
            self.metrics.record_connection_error()
            return False
    
    def _receive_loop(self):
        """Background thread for receiving messages from the server."""
        logger.info(f"Receive loop started for {self.user_id}")
        
        while self.running:
            try:
                # Poll for messages with timeout
                socks = dict(self.poller.poll(100))  # 100ms timeout
                
                if self.sub_socket in socks and socks[self.sub_socket] == zmq.POLLIN:
                    # Receive message
                    message_parts = self.sub_socket.recv_multipart(zmq.NOBLOCK)
                    
                    if len(message_parts) >= 2:
                        # room_id = message_parts[0].decode('utf-8')  # Room ID for filtering
                        message_data = message_parts[1]
                        
                        # Deserialize and process
                        self._process_received_message(message_data)
                
            except zmq.Again:
                # No message available, continue
                continue
            except KeyboardInterrupt:
                # Handle Ctrl+C gracefully
                logger.info(f"Receive loop interrupted for {self.user_id}")
                break
            except Exception as e:
                if self.running:  # Only log if we're still supposed to be running
                    logger.error(f"Error in receive loop: {e}")
                    self.metrics.record_connection_error()
        
        logger.info(f"Receive loop ended for {self.user_id}")
    
    def _process_received_message(self, message_data: bytes):
        """Process a received message from the server."""
        try:
            msg_type, msg_data, _ = deserialize(message_data)
            current_time = time.time()
            
            # Record received message
            self.metrics.record_message_received(None, f"received_{msg_type}", len(message_data))
            
            if msg_type == MSG_DEVICE_ID_MAPPING:
                # Device ID mapping - find our client number
                for mapping in msg_data.get('mappings', []):
                    if mapping.get('deviceId') == self.device_id:
                        self.client_no = mapping.get('clientNo')
                        logger.info(f"Received client number: {self.client_no} for {self.user_id}")
                        break
            
            elif msg_type == MSG_ROOM_TRANSFORM:
                # Room transform data
                clients = msg_data.get('clients', [])
                with self._lock:
                    self.received_transforms = clients
                
                # Call metrics recording callback if available
                if self.on_transform_received:
                    try:
                        self.on_transform_received(len(clients))
                    except Exception as e:
                        logger.warning(f"Error in transform received callback: {e}")
                
                if config.detailed_logging:
                    logger.debug(f"Received room transform with {len(clients)} clients")
            
            elif msg_type in [MSG_RPC_BROADCAST, MSG_RPC_CLIENT]:
                # RPC messages
                with self._lock:
                    self.received_rpcs.append({
                        'timestamp': current_time,
                        'type': msg_type,
                        'data': msg_data
                    })
                
                if config.detailed_logging:
                    function_name = msg_data.get('functionName', 'unknown')
                    logger.debug(f"Received RPC: {function_name}")
            
        except Exception as e:
            logger.error(f"Error processing received message: {e}")
    
    def get_received_data_summary(self) -> Dict[str, Any]:
        """Get a summary of received data for analysis."""
        with self._lock:
            return {
                'transforms_count': len(self.received_transforms),
                'rpcs_count': len(self.received_rpcs),
                'client_no': self.client_no,
                'connected': self.connected,
                'position': {
                    'x': self.position_x,
                    'z': self.position_z,
                    'rotation_y': self.rotation_y
                }
            }
    
    def perform_periodic_updates(self):
        """Perform periodic RPC updates."""
        current_time = time.time()
        
        # RPC updates
        if (current_time - self.last_rpc_update) >= config.rpc_send_interval:
            timestamp = datetime.now().strftime("%H:%M:%S")
            self.send_rpc("BenchmarkRPC", ["benchmark", timestamp, self.user_id], "broadcast")
            self.send_rpc("ServerRPC", ["server_test", timestamp], "server")
            self.send_rpc("ClientRPC", ["client_test", timestamp], "client")
            self.last_rpc_update = current_time
    
    def __del__(self):
        """Cleanup when the client is destroyed."""
        try:
            self.disconnect()
            if self.context:
                self.context.term()
        except:
            pass
