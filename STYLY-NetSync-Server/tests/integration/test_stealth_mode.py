#!/usr/bin/env python3
"""Test script for stealth client functionality"""

import sys
import time
import struct
import math
import zmq

def create_stealth_handshake(device_id: str) -> bytes:
    """Create a NaN handshake message for stealth mode"""
    buffer = bytearray()
    
    # Message type (MSG_CLIENT_TRANSFORM = 1)
    buffer.append(1)
    
    # Device ID
    device_id_bytes = device_id.encode('utf-8')
    buffer.append(len(device_id_bytes))
    buffer.extend(device_id_bytes)
    
    # Physical transform with NaN values (3 floats)
    buffer.extend(struct.pack('<f', float('nan')))  # posX
    buffer.extend(struct.pack('<f', float('nan')))  # posZ
    buffer.extend(struct.pack('<f', float('nan')))  # rotY
    
    # Head transform with NaN values (6 floats)
    for _ in range(6):
        buffer.extend(struct.pack('<f', float('nan')))
    
    # Right hand transform with NaN values (6 floats)
    for _ in range(6):
        buffer.extend(struct.pack('<f', float('nan')))
    
    # Left hand transform with NaN values (6 floats)
    for _ in range(6):
        buffer.extend(struct.pack('<f', float('nan')))
    
    # Virtual transforms count (0 for stealth)
    buffer.append(0)
    
    return bytes(buffer)

def test_stealth_client():
    """Test stealth client connection and message sending"""
    context = zmq.Context()
    
    # Connect as stealth client
    dealer = context.socket(zmq.DEALER)
    dealer.connect("tcp://localhost:5555")
    
    # Subscribe to all groups (empty subscription)
    sub = context.socket(zmq.SUB)
    sub.connect("tcp://localhost:5556")
    sub.setsockopt(zmq.SUBSCRIBE, b"")  # Subscribe to all topics
    
    device_id = "STEALTH_TEST_CLIENT"
    group_id = "default_group"
    
    print(f"Stealth client started with device ID: {device_id}")
    print("Sending NaN handshake to register as stealth client...")
    
    # Send stealth handshake
    handshake = create_stealth_handshake(device_id)
    dealer.send_multipart([group_id.encode('utf-8'), handshake])
    print("Stealth handshake sent")
    
    # Test RPC broadcast
    time.sleep(1)
    print("\nTesting RPC broadcast from stealth client...")
    
    # Create RPC broadcast message (MSG_RPC_BROADCAST = 3)
    rpc_buffer = bytearray()
    rpc_buffer.append(3)  # MSG_RPC_BROADCAST
    
    # Sender client number (will be overwritten by server)
    rpc_buffer.extend(struct.pack('<H', 0))
    
    # Function name
    func_name = "StealthTest"
    func_bytes = func_name.encode('utf-8')
    rpc_buffer.extend(struct.pack('<H', len(func_bytes)))
    rpc_buffer.extend(func_bytes)
    
    # Arguments JSON
    args_json = '["Hello from stealth client"]'
    args_bytes = args_json.encode('utf-8')
    rpc_buffer.extend(struct.pack('<H', len(args_bytes)))
    rpc_buffer.extend(args_bytes)
    
    dealer.send_multipart([group_id.encode('utf-8'), bytes(rpc_buffer)])
    print("RPC broadcast sent")
    
    # Listen for messages
    print("\nListening for group transforms (should not see stealth client)...")
    
    start_time = time.time()
    message_count = 0
    
    try:
        while time.time() - start_time < 10:
            # Send periodic handshake to maintain connection
            if int(time.time()) % 1 == 0:
                dealer.send_multipart([group_id.encode('utf-8'), handshake])
            
            # Check for incoming messages
            if sub.poll(100):
                parts = sub.recv_multipart()
                if len(parts) >= 2:
                    topic = parts[0].decode('utf-8')
                    payload = parts[1]
                    
                    if len(payload) > 0:
                        msg_type = payload[0]
                        if msg_type == 2:  # MSG_GROUP_TRANSFORM
                            message_count += 1
                            # Parse to check if stealth client appears
                            offset = 1
                            # Skip group ID
                            if offset < len(payload):
                                group_id_len = payload[offset]
                                offset += 1 + group_id_len
                                
                                # Client count
                                if offset < len(payload):
                                    client_count = payload[offset]
                                    print(f"  Group transform received for {topic}: {client_count} clients")
                                    
                                    # Check if our device ID appears (it shouldn't)
                                    if device_id.encode('utf-8') in payload:
                                        print("  ⚠️  WARNING: Stealth client visible in group transform!")
                                    else:
                                        print("  ✓ Stealth client not visible")
                        elif msg_type == 3:  # MSG_RPC_BROADCAST
                            print(f"  RPC broadcast received on {topic}")
                        elif msg_type == 6:  # MSG_DEVICE_ID_MAPPING
                            print(f"  ID mapping received on {topic}")
                            # Check if stealth client appears in mapping
                            if device_id.encode('utf-8') in payload:
                                print("  ⚠️  WARNING: Stealth client visible in ID mapping!")
                            else:
                                print("  ✓ Stealth client not in ID mapping")
                
    except KeyboardInterrupt:
        print("\nTest interrupted")
    
    print(f"\nTest completed. Received {message_count} group transforms.")
    print("Stealth client test finished.")
    
    dealer.close()
    sub.close()
    context.term()

if __name__ == "__main__":
    test_stealth_client()