"""
Tests for client reconnect identity update and NV resync.

Verifies that when a client reconnects (same device_id, new DEALER socket /
ZMQ identity) before the server timeout evicts it, the server:
  1. Updates the stored ROUTER identity.
  2. Marks the room ID mapping as dirty.
  3. Unicasts NV state to the reconnected client.
"""

from __future__ import annotations

import threading
import time

from styly_netsync import (
    NetSyncServer,
    client_transform_data,
    net_sync_manager,
    transform_data,
)

DEALER_PORT = 15700
PUB_PORT = 15701
ROOM = "reconnect_test"


def _make_transform(device_id: str) -> client_transform_data:
    """Return a minimal client transform for the given device."""
    return client_transform_data(
        head=transform_data(pos_x=0.0, pos_y=1.6, pos_z=0.0),
        device_id=device_id,
    )


def test_reconnect_updates_identity_and_resyncs_nv() -> None:
    """A reconnecting client gets an updated identity and receives NV state."""
    server = NetSyncServer(
        dealer_port=DEALER_PORT,
        pub_port=PUB_PORT,
        enable_server_discovery=False,
    )
    server_thread = threading.Thread(target=server.start, daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        # --- Phase 1: initial connection ---
        client = net_sync_manager(
            server="tcp://localhost",
            dealer_port=DEALER_PORT,
            sub_port=PUB_PORT,
            room=ROOM,
        )
        device_id = client._device_id
        client.start()

        # Send a transform so the server registers the client
        client.send_transform(_make_transform(device_id))
        time.sleep(0.5)

        # Capture the original identity stored by the server
        with server._rooms_lock:
            original_identity = server.rooms[ROOM][device_id]["identity"]

        assert original_identity is not None, "Server should store client identity"

        # Set a global NV so there is state to resync
        client.set_global_variable("test_key", "test_value")
        time.sleep(0.3)

        # --- Phase 2: simulate sleep/wake (stop + restart with same device_id) ---
        client.stop()
        time.sleep(0.1)

        # Create a new client with the SAME device_id (simulates new DEALER socket)
        client2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=DEALER_PORT,
            sub_port=PUB_PORT,
            room=ROOM,
        )
        client2._device_id = device_id  # reuse same device_id
        client2.start()

        # Send a transform to trigger reconnect detection
        client2.send_transform(_make_transform(device_id))
        time.sleep(0.5)

        # --- Assertions ---

        # 1. Server should have updated the identity
        with server._rooms_lock:
            new_identity = server.rooms[ROOM][device_id]["identity"]
        assert (
            new_identity != original_identity
        ), "Server should update identity on reconnect"

        # 2. The reconnected client should have received the NV state
        nv_value = client2.get_global_variable("test_key")
        assert (
            nv_value == "test_value"
        ), f"Reconnected client should receive NV resync, got: {nv_value}"

    finally:
        try:
            client.stop()
        except Exception:
            pass
        try:
            client2.stop()
        except Exception:
            pass
        server.stop()


def test_reconnect_receives_nv_updated_during_sleep() -> None:
    """NVs updated by another client while a client is sleeping are received on reconnect."""
    server = NetSyncServer(
        dealer_port=DEALER_PORT,
        pub_port=PUB_PORT,
        enable_server_discovery=False,
    )
    server_thread = threading.Thread(target=server.start, daemon=True)
    server_thread.start()
    time.sleep(0.5)

    client_a: net_sync_manager | None = None
    client_b: net_sync_manager | None = None
    client_b2: net_sync_manager | None = None

    try:
        # --- Phase 1: two clients join, client A sets an NV ---
        client_a = net_sync_manager(
            server="tcp://localhost",
            dealer_port=DEALER_PORT,
            sub_port=PUB_PORT,
            room=ROOM,
        )
        client_b = net_sync_manager(
            server="tcp://localhost",
            dealer_port=DEALER_PORT,
            sub_port=PUB_PORT,
            room=ROOM,
        )
        device_id_b = client_b._device_id

        client_a.start()
        client_b.start()

        client_a.send_transform(_make_transform(client_a._device_id))
        client_b.send_transform(_make_transform(device_id_b))
        time.sleep(0.5)

        # Client A sets initial NV value
        client_a.set_global_variable("score", "10")
        time.sleep(0.3)

        # Verify client B received it
        assert (
            client_b.get_global_variable("score") == "10"
        ), "Client B should have initial NV"

        # --- Phase 2: client B goes to sleep ---
        client_b.stop()
        time.sleep(0.1)

        # --- Phase 3: client A updates the NV while client B is sleeping ---
        client_a.set_global_variable("score", "42")
        time.sleep(0.3)

        # --- Phase 4: client B wakes up (new DEALER socket, same device_id) ---
        client_b2 = net_sync_manager(
            server="tcp://localhost",
            dealer_port=DEALER_PORT,
            sub_port=PUB_PORT,
            room=ROOM,
        )
        client_b2._device_id = device_id_b
        client_b2.start()

        # Send transform to trigger reconnect detection on server
        client_b2.send_transform(_make_transform(device_id_b))
        time.sleep(0.5)

        # --- Assertions ---
        nv_value = client_b2.get_global_variable("score")
        assert (
            nv_value == "42"
        ), f"Reconnected client should see NV updated during sleep, got: {nv_value}"

    finally:
        for c in (client_a, client_b, client_b2):
            if c is not None:
                try:
                    c.stop()
                except Exception:
                    pass
        server.stop()
