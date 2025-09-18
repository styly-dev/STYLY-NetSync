#!/usr/bin/env python3
"""
Test script to verify Locust command-line argument parsing.
"""

import sys
import os
sys.path.insert(0, os.path.dirname(__file__))

# Simulate what Locust does when it imports the locustfile
def test_locust_args():
    print("Testing Locust argument parsing...")
    
    # Test environment variable method
    print("\n1. Testing environment variable:")
    os.environ['STYLY_CLIENT_TYPE'] = 'netsync_manager'
    
    # Re-import config to pick up new env var
    import importlib
    import benchmark_config
    importlib.reload(benchmark_config)
    
    print(f"✓ Environment variable STYLY_CLIENT_TYPE=netsync_manager")
    print(f"  Config client_type: {benchmark_config.config.client_type}")
    
    # Test factory with environment config
    from client_factory import ClientFactory, ClientType
    client_type = ClientType.from_string(benchmark_config.config.client_type)
    print(f"  Parsed to ClientType: {client_type.value}")
    
    # Test client creation
    try:
        client = ClientFactory.create_client(client_type, user_id="env_test")
        print(f"✓ Successfully created: {client.__class__.__name__}")
        client.disconnect()  # cleanup
    except Exception as e:
        print(f"✗ Failed to create client: {e}")
        return False
    
    # Reset environment
    del os.environ['STYLY_CLIENT_TYPE']
    importlib.reload(benchmark_config)
    
    print(f"\n2. After reset:")
    print(f"  Config client_type: {benchmark_config.config.client_type}")
    
    print("\n✓ All argument tests passed!")
    return True

if __name__ == "__main__":
    success = test_locust_args()
    sys.exit(0 if success else 1)