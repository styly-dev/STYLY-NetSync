#!/usr/bin/env python3
"""
Basic test for the NetSync Python client implementation.

Tests import functionality and basic object creation without requiring
a running server.
"""

import sys
import os

# Add src to path so we can import without installation
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'src'))

def test_imports():
    """Test that all public API imports work correctly."""
    print("Testing imports...")
    
    try:
        from styly_netsync import create_manager, net_sync_manager, transform, client_transform, room_snapshot
        print("✓ Main imports successful")
    except ImportError as e:
        print(f"✗ Import error: {e}")
        return False
    
    try:
        from styly_netsync import NetSyncServer, main, get_version
        print("✓ Server imports successful")
    except ImportError as e:
        print(f"✗ Server import error: {e}")
        return False
    
    return True

def test_data_structures():
    """Test that data structures can be created and used."""
    print("\nTesting data structures...")
    
    try:
        from styly_netsync import transform, client_transform, room_snapshot
        
        # Test transform creation
        t = transform(pos_x=1.0, pos_y=2.0, pos_z=3.0, rot_x=0.1, rot_y=0.2, rot_z=0.3)
        assert t.pos_x == 1.0
        assert t.pos_y == 2.0
        assert t.pos_z == 3.0
        print("✓ transform dataclass works")
        
        # Test client_transform creation
        ct = client_transform(
            client_no=123,
            device_id="test-device-id",
            physical=t,
            head=transform(pos_y=1.6),
            virtuals=[transform(pos_z=5.0)]
        )
        assert ct.client_no == 123
        assert ct.device_id == "test-device-id"
        assert ct.physical.pos_x == 1.0
        assert ct.head.pos_y == 1.6
        assert len(ct.virtuals) == 1
        print("✓ client_transform dataclass works")
        
        # Test room_snapshot creation
        rs = room_snapshot(room_id="test_room", clients={123: ct})
        assert rs.room_id == "test_room"
        assert 123 in rs.clients
        assert rs.clients[123].client_no == 123
        print("✓ room_snapshot dataclass works")
        
        return True
        
    except Exception as e:
        print(f"✗ Data structure error: {e}")
        return False

def test_manager_creation():
    """Test that manager can be created (but not started)."""
    print("\nTesting manager creation...")
    
    try:
        from styly_netsync import create_manager
        
        # Test factory function
        manager = create_manager(
            server="tcp://localhost",
            dealer_port=5555,
            sub_port=5556,
            room="test_room",
            auto_dispatch=False,
            queue_max=1000
        )
        
        # Verify properties
        assert manager.server == "tcp://localhost"
        assert manager.dealer_port == 5555
        assert manager.sub_port == 5556
        assert manager.room == "test_room"
        assert not manager.is_running
        print("✓ Manager creation works")
        
        # Test that we can create transforms for sending
        from styly_netsync import transform, client_transform
        
        tx = client_transform(
            physical=transform(pos_x=0.0, pos_y=0.0, pos_z=0.0, is_local_space=True),
            head=transform(pos_x=0.0, pos_y=1.6, pos_z=0.0),
        )
        
        # These methods should not crash (but will fail due to no connection)
        result = manager.send_stealth_handshake()  # Should return False
        assert result is False
        print("✓ Manager methods callable")
        
        return True
        
    except Exception as e:
        print(f"✗ Manager creation error: {e}")
        return False

def test_adapters():
    """Test the adapter layer conversion functions."""
    print("\nTesting adapter layer...")
    
    try:
        from styly_netsync.adapters import (
            transform_to_wire, transform_from_wire,
            client_transform_to_wire, client_transform_from_wire,
            create_stealth_handshake
        )
        from styly_netsync import transform, client_transform
        
        # Test transform conversion
        t = transform(pos_x=1.0, pos_y=2.0, pos_z=3.0, rot_x=0.1, rot_y=0.2, rot_z=0.3)
        wire_t = transform_to_wire(t)
        
        # Check wire format has camelCase keys
        assert 'posX' in wire_t
        assert 'posY' in wire_t
        assert 'posZ' in wire_t
        assert wire_t['posX'] == 1.0
        print("✓ transform_to_wire works")
        
        # Test reverse conversion
        back_t = transform_from_wire(wire_t)
        assert back_t.pos_x == 1.0
        assert back_t.pos_y == 2.0
        assert back_t.pos_z == 3.0
        print("✓ transform_from_wire works")
        
        # Test stealth handshake creation
        stealth = create_stealth_handshake()
        assert stealth.physical is not None
        assert stealth.head is not None
        assert stealth.right_hand is not None
        assert stealth.left_hand is not None
        assert stealth.virtuals == []
        
        # Verify NaN values
        import math
        assert math.isnan(stealth.physical.pos_x)
        assert math.isnan(stealth.head.pos_y)
        print("✓ create_stealth_handshake works")
        
        return True
        
    except Exception as e:
        print(f"✗ Adapter layer error: {e}")
        return False

def main():
    """Run all basic tests."""
    print("Running basic NetSync Python client tests...")
    print("=" * 50)
    
    tests = [
        test_imports,
        test_data_structures,
        test_manager_creation,
        test_adapters
    ]
    
    passed = 0
    failed = 0
    
    for test in tests:
        try:
            if test():
                passed += 1
            else:
                failed += 1
        except Exception as e:
            print(f"✗ Test {test.__name__} crashed: {e}")
            failed += 1
    
    print("\n" + "=" * 50)
    print(f"Results: {passed} passed, {failed} failed")
    
    if failed == 0:
        print("🎉 All basic tests passed! Client implementation looks good.")
        return 0
    else:
        print("❌ Some tests failed. Check implementation.")
        return 1

if __name__ == "__main__":
    sys.exit(main())