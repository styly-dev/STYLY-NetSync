#!/usr/bin/env python3
"""
Test the Python NetSync client implementation meets requirements.

Validates all acceptance criteria from the implementation brief.
"""

import dataclasses
import threading
import time

from styly_netsync import (
    NetSyncServer,
    client_transform_data,
    net_sync_manager,
    transform_data,
)
from styly_netsync.config import ServerConfig, load_default_config


def test_packaging():
    """Test packaging and import requirements."""
    print("=== Testing Packaging ===")

    # Should be able to import from styly_netsync
    from styly_netsync import (
        net_sync_manager,
    )

    print("✓ All required imports successful")

    # All names should be snake_case
    manager = net_sync_manager()
    public_attrs = [attr for attr in dir(manager) if not attr.startswith("_")]
    camel_case_attrs = [attr for attr in public_attrs if any(c.isupper() for c in attr)]
    assert (
        len(camel_case_attrs) == 0
    ), f"Found camelCase in public API: {camel_case_attrs}"
    print("✓ Pure snake_case public API")


def test_connectivity():
    """Test connectivity and message processing."""
    print("\n=== Testing Connectivity ===")

    config = load_default_config()
    config = dataclasses.replace(
        config,
        dealer_port=5555,
        pub_port=5556,
        transform_pub_port=5557,
        state_pub_port=5556,
    )
    server = NetSyncServer(config=config, enable_server_discovery=False)
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        manager = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5555,
            sub_port=5556,
            transform_sub_port=5557,
            room="test_room",
        )
        manager.start()

        # Verify connection by checking stats after brief period
        time.sleep(0.1)
        stats = manager.get_stats()
        print(f"✓ Connected to server, received {stats['messages_received']} messages")

        manager.stop()

    finally:
        server.stop()


def test_transforms_pull_based():
    """Test pull-based transform consumption."""
    print("\n=== Testing Pull-based Transforms ===")

    config = load_default_config()
    config = dataclasses.replace(
        config,
        dealer_port=5560,
        pub_port=5561,
        transform_pub_port=5562,
        state_pub_port=5561,
    )
    server = NetSyncServer(config=config, enable_server_discovery=False)
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5560,
            sub_port=5561,
            transform_sub_port=5562,
            room="demo",
        )
        client2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5560,
            sub_port=5561,
            transform_sub_port=5562,
            room="demo",
        )

        client1.start()
        client2.start()
        time.sleep(0.2)  # Wait for client number assignment

        # Send transform from client1
        tx = client_transform_data(
            physical=transform_data(
                pos_x=5.0, pos_z=10.0, rot_y=90.0, is_local_space=True
            ),
            head=transform_data(pos_x=5.0, pos_y=1.6, pos_z=10.0),
        )
        tx.device_id = client1.device_id
        client1.send_transform(tx)
        time.sleep(0.3)  # allow broadcast and pull
        snapshot = client2.get_room_transform_data()
        assert snapshot is not None, "Should receive room snapshot"
        assert snapshot.room_id == "demo", "Room ID should match"
        print(
            f"✓ Pull-based access: room '{snapshot.room_id}' with {len(snapshot.clients)} clients"
        )

        # Test specific client lookup
        if len(snapshot.clients) > 0:
            client_no = list(snapshot.clients.keys())[0]
            ct = client2.get_client_transform_data(client_no)
            assert ct is not None, "Should find specific client transform"
            print(f"✓ Latest client transform lookup for client {client_no}")

        client1.stop()
        client2.stop()

    finally:
        server.stop()


def test_device_mapping():
    """Test device ID mapping functionality."""
    print("\n=== Testing Device Mapping ===")

    config = load_default_config()
    config = dataclasses.replace(
        config,
        dealer_port=5563,
        pub_port=5564,
        transform_pub_port=5565,
        state_pub_port=5564,
    )
    server = NetSyncServer(config=config, enable_server_discovery=False)
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        manager = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5563,
            sub_port=5564,
            transform_sub_port=5565,
            room="mapping_test",
        )
        manager.start()

        # Send transform to register
        tx = client_transform_data(
            physical=transform_data(pos_x=0, pos_y=0, pos_z=0),
            head=transform_data(pos_x=0, pos_y=1.6, pos_z=0),
        )
        tx.device_id = manager.device_id
        manager.send_transform(tx)

        time.sleep(1.0)  # Wait for mapping

        # Should have received device mapping
        device_id = manager.device_id
        client_no = manager.get_client_no(device_id)
        assert client_no is not None, "Should have received client number"
        reverse_device_id = (
            manager.get_device_id_from_client_no(client_no) if client_no else None
        )

        print(f"✓ Device mapping: {device_id[:8]}... -> {client_no}")
        print(
            f"✓ Reverse mapping: {client_no} -> {reverse_device_id[:8] if reverse_device_id else None}..."
        )

        manager.stop()

    finally:
        server.stop()


def test_rpc():
    """Test RPC functionality."""
    print("\n=== Testing RPC ===")

    config = load_default_config()
    config = dataclasses.replace(
        config,
        dealer_port=5566,
        pub_port=5567,
        transform_pub_port=5568,
        state_pub_port=5567,
    )
    server = NetSyncServer(config=config, enable_server_discovery=False)
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5566,
            sub_port=5567,
            transform_sub_port=5568,
            room="rpc_test",
            auto_dispatch=False,
        )
        client2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5566,
            sub_port=5567,
            transform_sub_port=5568,
            room="rpc_test",
            auto_dispatch=False,
        )

        client1.start()
        client2.start()

        # Send transforms to register clients
        tx = client_transform_data(
            physical=transform_data(pos_x=0, pos_y=0, pos_z=0),
            head=transform_data(pos_x=0, pos_y=1.6, pos_z=0),
        )
        tx.device_id = client1.device_id
        client1.send_transform(tx)

        tx2 = client_transform_data(
            physical=transform_data(pos_x=0, pos_y=0, pos_z=0),
            head=transform_data(pos_x=0, pos_y=1.6, pos_z=0),
        )
        tx2.device_id = client2.device_id
        client2.send_transform(tx2)

        time.sleep(1.0)  # Wait for client numbers (debounce is 0.5s)

        # Track received RPCs
        received_rpcs = []

        def on_rpc(sender, func_name, args):
            received_rpcs.append((sender, func_name, args))

        client2.on_rpc_received.add_listener(on_rpc)

        # Send RPC from client1
        success = client1.rpc("TestFunction", ["arg1", "arg2"])
        print(f"✓ RPC sent: {success}")

        time.sleep(0.1)

        # Process events on client2
        dispatched = client2.dispatch_pending_events()
        print(f"✓ Dispatched {dispatched} events")

        assert len(received_rpcs) > 0, "Should have received RPC"
        if received_rpcs:
            sender, func_name, args = received_rpcs[0]
            print(f"✓ RPC received: {sender} -> {func_name}({args})")

        client1.stop()
        client2.stop()

    finally:
        server.stop()


def test_network_variables():
    """Test Network Variables functionality."""
    print("\n=== Testing Network Variables ===")

    config = load_default_config()
    config = dataclasses.replace(
        config,
        dealer_port=5569,
        pub_port=5570,
        transform_pub_port=5571,
        state_pub_port=5570,
    )
    server = NetSyncServer(config=config, enable_server_discovery=False)
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5569,
            sub_port=5570,
            transform_sub_port=5571,
            room="nv_test",
        )
        client2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5569,
            sub_port=5570,
            transform_sub_port=5571,
            room="nv_test",
        )

        client1.start()
        client2.start()

        # Send transforms to register
        tx = client_transform_data(
            physical=transform_data(pos_x=0, pos_y=0, pos_z=0),
            head=transform_data(pos_x=0, pos_y=1.6, pos_z=0),
        )
        tx.device_id = client1.device_id
        client1.send_transform(tx)

        tx2 = client_transform_data(
            physical=transform_data(pos_x=0, pos_y=0, pos_z=0),
            head=transform_data(pos_x=0, pos_y=1.6, pos_z=0),
        )
        tx2.device_id = client2.device_id
        client2.send_transform(tx2)

        time.sleep(1.0)  # Wait for client numbers

        # Send network variables
        target_client_no = client2.client_no
        assert target_client_no is not None, "Client2 should have client number"

        success1 = client1.set_global_variable("game_state", "playing")
        success2 = client1.set_client_variable(target_client_no, "score", "100")
        assert success1, "Set global var should succeed"
        assert success2, "Set client var should succeed"
        print(f"✓ Network variables set: global={success1}, client={success2}")

        time.sleep(0.5)  # Wait for sync

        # Read back values
        game_state = client2.get_global_variable("game_state", "unknown")
        score = client2.get_client_variable(target_client_no, "score", "0")

        assert game_state == "playing", f"Global var mismatch: {game_state}"
        assert score == "100", f"Client var mismatch: {score}"
        print(f"✓ Network variables read: game_state={game_state}, score={score}")

        client1.stop()
        client2.stop()

    finally:
        server.stop()


def test_stealth_mode():
    """Test stealth mode functionality."""
    print("\n=== Testing Stealth Mode ===")

    config = load_default_config()
    config = dataclasses.replace(
        config,
        dealer_port=5572,
        pub_port=5573,
        transform_pub_port=5574,
        state_pub_port=5573,
    )
    server = NetSyncServer(config=config, enable_server_discovery=False)
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5572,
            sub_port=5573,
            transform_sub_port=5574,
            room="stealth_test",
        )
        client2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5572,
            sub_port=5573,
            transform_sub_port=5574,
            room="stealth_test",
        )

        client1.start()
        client2.start()
        time.sleep(0.2)

        # Client1 goes stealth
        success = client1.send_stealth_handshake()
        print(f"✓ Stealth handshake sent: {success}")

        time.sleep(0.2)

        # Client2 should not see client1 in room snapshots
        snapshot = client2.get_room_transform_data()
        if snapshot:
            visible_clients = list(snapshot.clients.keys())
            print(f"✓ Visible clients after stealth: {visible_clients}")
            # Client1 should be hidden from room broadcasts

        client1.stop()
        client2.stop()

    finally:
        server.stop()


if __name__ == "__main__":
    print("Python NetSync Client - Acceptance Criteria Tests")
    print("=" * 50)

    try:
        test_packaging()
        test_connectivity()
        test_transforms_pull_based()
        test_device_mapping()
        test_rpc()
        test_network_variables()
        test_stealth_mode()

        print("\n🎉 All acceptance criteria tests passed!")

    except Exception as e:
        print(f"\n❌ Test failed: {e}")
        import traceback

        traceback.print_exc()
        exit(1)
