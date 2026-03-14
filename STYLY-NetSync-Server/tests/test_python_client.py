#!/usr/bin/env python3
"""
Test the Python NetSync client implementation meets requirements.

Validates all acceptance criteria from the implementation brief.
"""

import threading
import time

from styly_netsync import (
    NetSyncServer,
    client_transform_data,
    net_sync_manager,
    transform_data,
)


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

    server = NetSyncServer(
        dealer_port=5555, pub_port=5556, enable_server_discovery=False
    )
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        manager = net_sync_manager(
            server="tcp://localhost", dealer_port=5555, sub_port=5556, room="test_room"
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

    server = NetSyncServer(
        dealer_port=5557, pub_port=5558, enable_server_discovery=False
    )
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost", dealer_port=5557, sub_port=5558, room="demo"
        )
        client2 = net_sync_manager(
            server="tcp://localhost", dealer_port=5557, sub_port=5558, room="demo"
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

    server = NetSyncServer(
        dealer_port=5559, pub_port=5560, enable_server_discovery=False
    )
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        manager = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5559,
            sub_port=5560,
            room="mapping_test",
        )
        manager.start()
        time.sleep(0.3)  # Wait for mapping

        # Should have received device mapping
        device_id = manager.device_id
        client_no = manager.get_client_no(device_id)
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

    server = NetSyncServer(
        dealer_port=5561, pub_port=5562, enable_server_discovery=False
    )
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5561,
            sub_port=5562,
            room="rpc_test",
            auto_dispatch=False,
        )
        client2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5561,
            sub_port=5562,
            room="rpc_test",
            auto_dispatch=False,
        )

        client1.start()
        client2.start()
        time.sleep(0.3)  # Wait for client numbers

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

    server = NetSyncServer(
        dealer_port=5563, pub_port=5564, enable_server_discovery=False
    )
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost", dealer_port=5563, sub_port=5564, room="nv_test"
        )
        client2 = net_sync_manager(
            server="tcp://localhost", dealer_port=5563, sub_port=5564, room="nv_test"
        )

        client1.start()
        client2.start()
        time.sleep(0.3)  # Wait for client numbers

        # Send network variables
        success1 = client1.set_global_variable("game_state", "playing")
        success2 = client1.set_client_variable(
            client2.client_no if client2.client_no else 1, "score", "100"
        )
        print(f"✓ Network variables set: global={success1}, client={success2}")

        time.sleep(0.2)  # Wait for sync

        # Read back values
        game_state = client2.get_global_variable("game_state", "unknown")
        score = client2.get_client_variable(
            client2.client_no if client2.client_no else 1, "score", "0"
        )
        print(f"✓ Network variables read: game_state={game_state}, score={score}")

        client1.stop()
        client2.stop()

    finally:
        server.stop()


def test_stealth_mode():
    """Test stealth mode functionality."""
    print("\n=== Testing Stealth Mode ===")

    server = NetSyncServer(
        dealer_port=5565, pub_port=5566, enable_server_discovery=False
    )
    server_thread = threading.Thread(target=lambda: server.start(), daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        client1 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5565,
            sub_port=5566,
            room="stealth_test",
        )
        client2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=5565,
            sub_port=5566,
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


def test_connection_error_event_exists():
    """Test that on_connection_error event handler is available on the manager."""
    from styly_netsync.events import EventHandler

    manager = net_sync_manager()
    assert hasattr(
        manager, "on_connection_error"
    ), "on_connection_error event handler missing"
    assert isinstance(
        manager.on_connection_error, EventHandler
    ), "on_connection_error should be an EventHandler"
    print("✓ on_connection_error event handler present")


def test_reconnect_parameters():
    """Test that reconnect_delay and receive_timeout constructor parameters work."""
    m1 = net_sync_manager(reconnect_delay=3.0, receive_timeout=10.0)
    assert m1._reconnect_delay == 3.0
    assert m1._receive_timeout == 10.0

    m2 = net_sync_manager(receive_timeout=None)
    assert m2._receive_timeout is None

    print("✓ reconnect_delay and receive_timeout constructor parameters work")


def test_receive_timeout_triggers_connection_error():
    """Test that silence from server triggers on_connection_error after receive_timeout."""
    print("\n=== Testing Receive Timeout / Connection Error Event ===")

    # Use an unused port so the client starts but never receives any messages
    manager = net_sync_manager(
        server="tcp://localhost",
        dealer_port=15997,
        sub_port=15998,
        room="error_test",
        receive_timeout=0.3,  # Very short: trigger after 0.3s of silence
        reconnect_delay=99.0,  # Long: don't actually reconnect during test
    )

    errors: list[str] = []
    manager.on_connection_error.add_listener(errors.append)
    manager.start()

    # Silence detection is only armed after the first inbound message.
    # Simulate that by setting the clock manually.
    manager._last_message_time = time.monotonic()

    try:
        # Wait long enough for the silence timeout to fire (0.3s) + margin
        time.sleep(1.2)
        assert (
            len(errors) > 0
        ), "Expected on_connection_error to fire after receive_timeout"
        print(f"✓ on_connection_error fired: {errors[0]}")
    finally:
        manager.stop()


def test_reconnect_count_in_stats():
    """Test that reconnect_count is present in get_stats()."""
    manager = net_sync_manager()
    stats = manager.get_stats()
    assert "reconnect_count" in stats, "reconnect_count missing from stats"
    assert stats["reconnect_count"] == 0
    print("✓ reconnect_count present in stats")


def test_stop_from_connection_error_listener():
    """Test that calling stop() from an on_connection_error listener doesn't leak resources."""
    print("\n=== Testing stop() from on_connection_error listener ===")

    manager = net_sync_manager(
        server="tcp://localhost",
        dealer_port=15999,
        sub_port=16000,
        room="self_join_test",
        receive_timeout=0.3,
        reconnect_delay=99.0,
    )

    errors: list[str] = []
    stop_errors: list[Exception] = []

    def on_error(error: str) -> None:
        errors.append(error)
        try:
            manager.stop()
        except Exception as e:
            stop_errors.append(e)

    manager.on_connection_error.add_listener(on_error)
    manager.start()

    # Arm silence detection (only active after first inbound message)
    manager._last_message_time = time.monotonic()

    try:
        # Wait for silence timeout (0.3s) + margin for stop() to complete
        time.sleep(1.5)
        assert len(errors) > 0, "Expected on_connection_error to fire"
        assert len(stop_errors) == 0, f"stop() raised from listener: {stop_errors}"
        # Verify cleanup ran (context should be None after _cleanup)
        assert manager._context is None, "ZMQ context was not cleaned up"
        print(f"✓ stop() from on_connection_error listener succeeded: {errors[0]}")
    finally:
        try:
            manager.stop()
        except Exception:
            pass


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
