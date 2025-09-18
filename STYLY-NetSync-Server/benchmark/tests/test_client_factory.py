#!/usr/bin/env python3
"""
Quick test script to verify the client factory functionality.
"""

import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

import time
from src.client_factory import ClientFactory, ClientType
from src.benchmark_config import config

def test_client_creation():
    """Test that both client types can be created successfully."""
    print("Testing Client Factory...")
    
    # Test Raw ZMQ client creation
    print("\n1. Testing RawZMQClient creation:")
    try:
        raw_client = ClientFactory.create_client(ClientType.RAW_ZMQ, user_id="test_raw")
        print(f"✓ Created {raw_client.__class__.__name__}")
        print(f"  User ID: {raw_client.user_id}")
        print(f"  Device ID: {raw_client.device_id[:8]}...")
        print(f"  Connected: {raw_client.connected}")
    except Exception as e:
        print(f"✗ Failed to create RawZMQClient: {e}")
        return False
    
    # Test NetSyncManager client creation
    print("\n2. Testing NetSyncManagerClient creation:")
    try:
        manager_client = ClientFactory.create_client(ClientType.NETSYNC_MANAGER, user_id="test_manager")
        print(f"✓ Created {manager_client.__class__.__name__}")
        print(f"  User ID: {manager_client.user_id}")
        print(f"  Device ID: {manager_client.device_id[:8]}...")
        print(f"  Connected: {manager_client.connected}")
    except Exception as e:
        print(f"✗ Failed to create NetSyncManagerClient: {e}")
        return False
    
    # Test string conversion
    print("\n3. Testing ClientType.from_string():")
    test_cases = [
        ("raw_zmq", ClientType.RAW_ZMQ),
        ("raw", ClientType.RAW_ZMQ),
        ("netsync_manager", ClientType.NETSYNC_MANAGER), 
        ("manager", ClientType.NETSYNC_MANAGER),
        ("package", ClientType.NETSYNC_MANAGER),
        ("unknown", ClientType.RAW_ZMQ),  # Should default to RAW_ZMQ
    ]
    
    for input_str, expected in test_cases:
        result = ClientType.from_string(input_str)
        if result == expected:
            print(f"✓ '{input_str}' -> {result.value}")
        else:
            print(f"✗ '{input_str}' -> {result.value} (expected {expected.value})")
            return False
    
    # Test configuration
    print(f"\n4. Current configuration:")
    print(f"  Client Type: {config.client_type}")
    print(f"  Server: {config.server_address}:{config.dealer_port}")
    print(f"  Room: {config.room_id}")
    
    print("\n✓ All tests passed!")
    return True

if __name__ == "__main__":
    success = test_client_creation()
    sys.exit(0 if success else 1)