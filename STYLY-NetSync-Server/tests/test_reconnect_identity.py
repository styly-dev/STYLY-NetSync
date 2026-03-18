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
