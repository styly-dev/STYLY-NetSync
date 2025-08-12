#!/usr/bin/env python3
"""
Test client for STYLY-NetSync server
Tests all features: Transform, Network Variables, and RPC

This test client simulates a single client connection to the STYLY-NetSync server
and exercises all major features of the multiplayer framework:

1. Transform Synchronization:
   - Simulates circular movement at 50Hz update rate
   - Sends physical transform (X, Z position, Y rotation in local space)
   - Sends head and hand transforms in world space
   - Position moves in a circular pattern with radius 5 units

2. Network Variables:
   - Global Variable: 'TimeGlobal' - Updates every minute with current time (HH:MM format)
   - Client Variable: 'TimeLocal' - Updates every minute with client's time (HH:MM format)
   - Both variables are synchronized across all clients in the group

3. Remote Procedure Calls (RPC):
   - Broadcast RPC: Sends 'BroadcastRPC' to all clients every 10 seconds
   - Server RPC: Sends 'ServerRPC' to server every 10 seconds
   - Client-to-Client RPC: Sends 'ClientToClientRPC' to itself every 10 seconds
   - All RPCs include current timestamp (HH:MM:SS format) as arguments

The client logs all activities including:
- Connection status and client number assignment
- Transform updates (debug level)
- Network variable changes
- RPC transmissions
- Received updates from other clients

Usage:
  python test_client.py [options]

Options:
  --dealer-port PORT  Server DEALER port (default: 5555)
  --sub-port PORT     Server PUB port (default: 5556)
  --server ADDRESS    Server address (default: localhost)
  --group GROUP_ID    Group ID to join (default: test_group)

Example:
  python test_client.py --server 192.168.1.100 --group vr_room_1
"""

import zmq
import time
import json
import math
import uuid
import logging
from datetime import datetime
from threading import Thread, Event
from styly_netsync.binary_serializer import (
    serialize_client_transform,
    serialize_global_var_set,
    serialize_client_var_set,
    serialize_rpc_message,
    serialize_rpc_request,
    serialize_rpc_client_message,
    deserialize,
    MSG_GROUP_TRANSFORM,
    MSG_DEVICE_ID_MAPPING,
    MSG_GLOBAL_VAR_SYNC,
    MSG_CLIENT_VAR_SYNC
)

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='[%(asctime)s] %(levelname)s: %(message)s',
    datefmt='%H:%M:%S'
)
logger = logging.getLogger(__name__)

class TestClient:
    def __init__(self, dealer_port=5555, sub_port=5556, server_address='localhost', group_id='test_group'):
        self.context = zmq.Context()
        self.dealer_socket = None
        self.sub_socket = None
        self.device_id = str(uuid.uuid4())
        self.client_no = None
        self.group_id = group_id
        self.server_address = server_address
        self.dealer_port = dealer_port
        self.sub_port = sub_port
        self.running = False
        self.stop_event = Event()
        
        # Transform state
        self.position_x = 0
        self.position_z = 0
        self.rotation_y = 0
        self.time = 0
        
        # Time tracking for periodic updates
        self.last_variable_update = 0
        self.last_rpc_update = 0
        
        logger.info(f"Test client initialized with device ID: {self.device_id}")

    def connect(self):
        """Connect to the server"""
        try:
            # Connect DEALER socket
            self.dealer_socket = self.context.socket(zmq.DEALER)
            self.dealer_socket.connect(f"tcp://{self.server_address}:{self.dealer_port}")
            logger.info(f"Connected DEALER socket to tcp://{self.server_address}:{self.dealer_port}")
            
            # Connect SUB socket
            self.sub_socket = self.context.socket(zmq.SUB)
            self.sub_socket.connect(f"tcp://{self.server_address}:{self.sub_port}")
            self.sub_socket.setsockopt_string(zmq.SUBSCRIBE, self.group_id)
            logger.info(f"Connected SUB socket to tcp://{self.server_address}:{self.sub_port}")
            
            return True
        except Exception as e:
            logger.error(f"Failed to connect: {e}")
            return False

    def disconnect(self):
        """Disconnect from the server"""
        self.running = False
        self.stop_event.set()
        
        if self.dealer_socket:
            self.dealer_socket.close()
        if self.sub_socket:
            self.sub_socket.close()
        self.context.term()
        logger.info("Disconnected from server")

    def send_transform(self):
        """Send transform update with simple movement simulation"""
        # Simple circular movement
        self.time += 0.02  # 50Hz update
        radius = 5.0
        self.position_x = radius * math.cos(self.time)
        self.position_z = radius * math.sin(self.time)
        self.rotation_y = math.degrees(math.atan2(self.position_z, self.position_x))
        
        # Build transform data
        transform_data = {
            'deviceId': self.device_id,
            'physical': {
                'posX': self.position_x,
                'posY': 0,
                'posZ': self.position_z,
                'rotX': 0,
                'rotY': self.rotation_y,
                'rotZ': 0,
                'isLocalSpace': True
            },
            'head': {
                'posX': self.position_x,
                'posY': 1.6,  # Head height
                'posZ': self.position_z,
                'rotX': 0,
                'rotY': self.rotation_y,
                'rotZ': 0,
                'isLocalSpace': False
            },
            'rightHand': {
                'posX': self.position_x + 0.3,
                'posY': 1.2,
                'posZ': self.position_z,
                'rotX': 0,
                'rotY': 0,
                'rotZ': 0,
                'isLocalSpace': False
            },
            'leftHand': {
                'posX': self.position_x - 0.3,
                'posY': 1.2,
                'posZ': self.position_z,
                'rotX': 0,
                'rotY': 0,
                'rotZ': 0,
                'isLocalSpace': False
            },
            'virtuals': []  # No virtual objects for this test
        }
        
        # Serialize and send
        message = serialize_client_transform(transform_data)
        self.dealer_socket.send_multipart([self.group_id.encode('utf-8'), message])
        logger.debug(f"Sent transform: pos({self.position_x:.2f}, {self.position_z:.2f}), rot({self.rotation_y:.2f})")

    def update_network_variables(self):
        """Update network variables every minute"""
        current_time = time.time()
        if current_time - self.last_variable_update < 60:  # 1 minute interval
            return
        
        self.last_variable_update = current_time
        current_time_str = datetime.now().strftime("%H:%M")
        
        # Set global variable
        global_var_data = {
            'senderClientNo': self.client_no or 0,
            'variableName': 'TimeGlobal',
            'variableValue': current_time_str,
            'timestamp': current_time
        }
        message = serialize_global_var_set(global_var_data)
        self.dealer_socket.send_multipart([self.group_id.encode('utf-8'), message])
        logger.info(f"Set global variable TimeGlobal = {current_time_str}")
        
        # Set client variable (for self)
        if self.client_no is not None:
            client_var_data = {
                'senderClientNo': self.client_no,
                'targetClientNo': self.client_no,  # Set for self
                'variableName': 'TimeLocal',
                'variableValue': current_time_str,
                'timestamp': current_time
            }
            message = serialize_client_var_set(client_var_data)
            self.dealer_socket.send_multipart([self.group_id.encode('utf-8'), message])
            logger.info(f"Set client variable TimeLocal = {current_time_str}")

    def send_rpcs(self):
        """Send RPC messages every 10 seconds"""
        current_time = time.time()
        if current_time - self.last_rpc_update < 10:  # 10 second interval
            return
        
        self.last_rpc_update = current_time
        current_time_str = datetime.now().strftime("%H:%M:%S")
        
        if self.client_no is None:
            return
        
        # Broadcast RPC
        broadcast_data = {
            'senderClientNo': self.client_no,
            'functionName': 'BroadcastRPC',
            'argumentsJson': json.dumps(['BroadcastRPC', current_time_str])
        }
        message = serialize_rpc_message(broadcast_data)
        self.dealer_socket.send_multipart([self.group_id.encode('utf-8'), message])
        logger.info(f"Sent broadcast RPC: BroadcastRPC({current_time_str})")
        
        # Server RPC
        server_data = {
            'senderClientNo': self.client_no,
            'functionName': 'ServerRPC',
            'argumentsJson': json.dumps(['ServerRPC', current_time_str])
        }
        message = serialize_rpc_request(server_data)
        self.dealer_socket.send_multipart([self.group_id.encode('utf-8'), message])
        logger.info(f"Sent server RPC: ServerRPC({current_time_str})")
        
        # Client-to-Client RPC (to self)
        client_data = {
            'senderClientNo': self.client_no,
            'targetClientNo': self.client_no,  # Send to self
            'functionName': 'ClientToClientRPC',
            'argumentsJson': json.dumps(['ClientToClientRPC', current_time_str])
        }
        message = serialize_rpc_client_message(client_data)
        self.dealer_socket.send_multipart([self.group_id.encode('utf-8'), message])
        logger.info(f"Sent client-to-client RPC to self: ClientToClientRPC({current_time_str})")

    def receive_messages(self):
        """Receive and process messages from the server"""
        poller = zmq.Poller()
        poller.register(self.sub_socket, zmq.POLLIN)
        
        while self.running:
            try:
                socks = dict(poller.poll(100))  # 100ms timeout
                
                if self.sub_socket in socks:
                    message_parts = self.sub_socket.recv_multipart()
                    if len(message_parts) >= 2:
                        group_id = message_parts[0].decode('utf-8')
                        data = message_parts[1]
                        
                        msg_type, msg_data, _ = deserialize(data)
                        
                        if msg_type == MSG_DEVICE_ID_MAPPING:
                            # Update our client number
                            for mapping in msg_data['mappings']:
                                if mapping['deviceId'] == self.device_id:
                                    self.client_no = mapping['clientNo']
                                    logger.info(f"Received client number: {self.client_no}")
                        
                        elif msg_type == MSG_GROUP_TRANSFORM:
                            logger.debug(f"Received group transform with {len(msg_data['clients'])} clients")
                        
                        elif msg_type == MSG_GLOBAL_VAR_SYNC:
                            for var in msg_data['variables']:
                                logger.info(f"Global variable update: {var['name']} = {var['value']} (by client {var['lastWriterClientNo']})")
                        
                        elif msg_type == MSG_CLIENT_VAR_SYNC:
                            for client_no, variables in msg_data['clientVariables'].items():
                                for var in variables:
                                    logger.info(f"Client {client_no} variable update: {var['name']} = {var['value']}")
                        
                        # RPC messages would be received here if server forwarded them
                        
            except zmq.Again:
                pass
            except Exception as e:
                logger.error(f"Error receiving message: {e}")

    def send_loop(self):
        """Main sending loop"""
        while self.running:
            try:
                # Send transform at 50Hz
                self.send_transform()
                
                # Update network variables every minute
                self.update_network_variables()
                
                # Send RPCs every 10 seconds
                self.send_rpcs()
                
                # Sleep for 20ms (50Hz)
                time.sleep(0.02)
                
            except Exception as e:
                logger.error(f"Error in send loop: {e}")

    def run(self):
        """Run the test client"""
        if not self.connect():
            return
        
        self.running = True
        
        # Start receive thread
        receive_thread = Thread(target=self.receive_messages)
        receive_thread.daemon = True
        receive_thread.start()
        
        logger.info("Test client started. Press Ctrl+C to stop.")
        logger.info("Features being tested:")
        logger.info("- Transform: Circular movement at 50Hz")
        logger.info("- Global variable 'TimeGlobal': Updated every minute")
        logger.info("- Client variable 'TimeLocal': Updated every minute")
        logger.info("- RPCs: Broadcast, Server, and Client-to-Client every 10 seconds")
        
        try:
            # Run send loop in main thread
            self.send_loop()
        except KeyboardInterrupt:
            logger.info("Stopping test client...")
        finally:
            self.disconnect()

def main():
    import argparse
    
    parser = argparse.ArgumentParser(description='STYLY-NetSync Test Client')
    parser.add_argument('--dealer-port', type=int, default=5555, help='Server DEALER port')
    parser.add_argument('--sub-port', type=int, default=5556, help='Server PUB port')
    parser.add_argument('--server', type=str, default='localhost', help='Server address')
    parser.add_argument('--group', type=str, default='test_group', help='Group ID')
    
    args = parser.parse_args()
    
    client = TestClient(
        dealer_port=args.dealer_port,
        sub_port=args.sub_port,
        server_address=args.server,
        group_id=args.group
    )
    
    client.run()

if __name__ == '__main__':
    main()