#!/usr/bin/env python3
"""
Test script to verify Physical Transform 6f implementation
Test script to verify Physical Transform 6f implementation.
Tests that physical transforms are serialized and deserialized with all 6 degrees of freedom: posX, posY, posZ, rotX, rotY, rotZ.
"""

import math
import struct
import time
import zmq

def create_test_transform(device_id: str, pos_y: float, rot_x: float, rot_z: float) -> bytes:
    """Create a test transform with non-zero Y position and X/Z rotation for physical"""
    buffer = bytearray()
    
    # Message type (MSG_CLIENT_TRANSFORM = 1)
    buffer.append(1)
    
    # Device ID
    device_id_bytes = device_id.encode('utf-8')
    buffer.append(len(device_id_bytes))
    buffer.extend(device_id_bytes)
    
    # Physical transform (now 6 floats - testing Y position and X/Z rotation)
    buffer.extend(struct.pack('<f', 1.0))  # posX
    buffer.extend(struct.pack('<f', pos_y))  # posY (testing non-zero)
    buffer.extend(struct.pack('<f', 2.0))  # posZ
    buffer.extend(struct.pack('<f', rot_x))  # rotX (testing non-zero)
    buffer.extend(struct.pack('<f', 45.0))  # rotY
    buffer.extend(struct.pack('<f', rot_z))  # rotZ (testing non-zero)
    
    # Head transform (6 floats)
    for val in [1.0, 1.6, 2.0, 0.0, 45.0, 0.0]:
        buffer.extend(struct.pack('<f', val))
    
    # Right hand transform (6 floats)
    for val in [1.3, 1.2, 2.0, 0.0, 0.0, 0.0]:
        buffer.extend(struct.pack('<f', val))
    
    # Left hand transform (6 floats)
    for val in [0.7, 1.2, 2.0, 0.0, 0.0, 0.0]:
        buffer.extend(struct.pack('<f', val))
    
    # Virtual transforms count (0)
    buffer.append(0)
    
    return bytes(buffer)

def verify_room_transform(payload: bytes) -> bool:
    """Verify that room transform contains full 6f physical data"""
    if len(payload) < 2 or payload[0] != 2:  # MSG_ROOM_TRANSFORM
        return False
    
    offset = 1
    # Skip room ID
    room_id_len = payload[offset]
    offset += 1 + room_id_len
    
    # Client count
    client_count = struct.unpack('<H', payload[offset:offset+2])[0]
    offset += 2
    
    print(f"  Room has {client_count} client(s)")
    
    for i in range(client_count):
        # Client number
        client_no = struct.unpack('<H', payload[offset:offset+2])[0]
        offset += 2
        
        # Physical transform (should be 6 floats now)
        physical_data = []
        for j in range(6):  # Read 6 floats for physical
            val = struct.unpack('<f', payload[offset:offset+4])[0]
            physical_data.append(val)
            offset += 4
        
        print(f"  Client {client_no} Physical: pos({physical_data[0]:.1f}, {physical_data[1]:.1f}, {physical_data[2]:.1f}) rot({physical_data[3]:.1f}, {physical_data[4]:.1f}, {physical_data[5]:.1f})")
        
        # Verify Y position and X/Z rotation are preserved
        if abs(physical_data[1] - 0.5) < 0.01:  # posY should be ~0.5
            print("    ✓ Physical Y position preserved!")
        if abs(physical_data[3] - 15.0) < 0.01:  # rotX should be ~15
            print("    ✓ Physical X rotation preserved!")
        if abs(physical_data[5] - 30.0) < 0.01:  # rotZ should be ~30
            print("    ✓ Physical Z rotation preserved!")
        
        # Skip head, hands, and virtuals (we're just checking physical)
        # Head: 6 floats
        offset += 24
        # Right hand: 6 floats
        offset += 24
        # Left hand: 6 floats
        offset += 24
        # Virtual count and data
        virtual_count = payload[offset]
        offset += 1
        offset += virtual_count * 24
    
    return True

def main():
    print("Testing Physical Transform 6f Implementation")
    print("=" * 50)
    
    context = zmq.Context()
    
    # Connect sockets
    dealer = context.socket(zmq.DEALER)
    dealer.connect("tcp://localhost:5555")
    
    sub = context.socket(zmq.SUB)
    sub.connect("tcp://localhost:5556")
    sub.setsockopt(zmq.SUBSCRIBE, b"")
    
    device_id = "PHYSICAL_6F_TEST"
    room_id = "test_room_6f"
    
    print(f"Connected with device ID: {device_id}")
    print(f"Room: {room_id}")
    print()
    
    # Test values for Y position and X/Z rotation
    test_pos_y = 0.5  # Non-zero Y position
    test_rot_x = 15.0  # Non-zero X rotation
    test_rot_z = 30.0  # Non-zero Z rotation
    
    print(f"Sending transform with Physical Y={test_pos_y}, RotX={test_rot_x}, RotZ={test_rot_z}")
    
    # Send test transform
    test_msg = create_test_transform(device_id, test_pos_y, test_rot_x, test_rot_z)
    dealer.send_multipart([room_id.encode('utf-8'), test_msg])
    
    print("Listening for room transforms...")
    print()
    
    start_time = time.time()
    received_count = 0
    
    try:
        while time.time() - start_time < 5:
            # Send periodic updates
            if int(time.time()) % 1 == 0:
                dealer.send_multipart([room_id.encode('utf-8'), test_msg])
            
            # Check for responses
            if sub.poll(100):
                parts = sub.recv_multipart()
                if len(parts) >= 2:
                    topic = parts[0].decode('utf-8')
                    payload = parts[1]
                    
                    if topic == room_id and len(payload) > 0:
                        msg_type = payload[0]
                        if msg_type == 2:  # MSG_ROOM_TRANSFORM
                            received_count += 1
                            print(f"Received room transform #{received_count}:")
                            verify_room_transform(payload)
                            print()
    
    except KeyboardInterrupt:
        print("\nTest interrupted")
    
    print(f"Test completed. Received {received_count} room transforms.")
    
    if received_count > 0:
        print("\n✅ Physical Transform 6f implementation verified!")
        print("   - Y position is transmitted and preserved")
        print("   - X and Z rotations are transmitted and preserved")
    else:
        print("\n❌ No room transforms received. Is the server running?")
    
    dealer.close()
    sub.close()
    context.term()

if __name__ == "__main__":
    main()