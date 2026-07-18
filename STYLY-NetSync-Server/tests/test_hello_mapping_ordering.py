"""
Tests that a client hello enqueues its ID mapping before the NV snapshot.

On (re)connect the client's local ClientNo is assigned by the ID mapping
message, while NV-changed events fire as soon as the NV snapshot arrives. If the
NV snapshot reached the client first, an NV-changed handler running before
OnReady would read ClientNo == 0. These tests lock down that the server enqueues
the mapping to the ROUTER control queue *before* the NV sync for the connecting
identity, for both the new-client and reconnect paths.
"""

from __future__ import annotations

import time

from styly_netsync import NetSyncServer, binary_serializer

ROOM = "hello_order_test"
DEVICE_ID = "device-order-0001"


def _make_server() -> NetSyncServer:
    """Return an unstarted server (no sockets, no threads) for direct calls."""
    return NetSyncServer(
        dealer_port=15720,
        pub_port=15721,
        enable_server_discovery=False,
    )


def _register_client(server: NetSyncServer, identity: bytes) -> int:
    """Register a client in ROOM with the given control identity."""
    with server._rooms_lock:
        server._initialize_room(ROOM)
        client_no = server._get_or_assign_client_no(ROOM, DEVICE_ID)
        server.rooms[ROOM][DEVICE_ID] = {
            "control_identity": identity,
            "transform_identity": None,
            "last_update": time.monotonic(),
            "transform_data": None,
            "client_no": client_no,
            "is_stealth": False,
        }
    return client_no


def _spy_enqueue(server: NetSyncServer) -> list[int]:
    """Replace _enqueue_router with a spy that records message types in order."""
    recorded: list[int] = []

    def spy(identity: bytes, room_id: str, message_bytes: bytes) -> None:
        # First byte is the message type (see binary_serializer.serialize_*).
        recorded.append(message_bytes[0])

    server._enqueue_router = spy  # type: ignore[method-assign]
    return recorded


def test_reconnect_enqueues_mapping_before_nv() -> None:
    """Reconnect hello sends the ID mapping before global and client NV snapshots.

    The reported symptom is client-variable-specific: an NV-changed handler that
    runs before OnReady reads ClientNo == 0 and GetClientVariable(name) silently
    returns the default. Assert the mapping precedes MSG_CLIENT_VAR_SYNC (and the
    global sync) so the client number is already assigned when the events fire.
    """
    server = _make_server()
    client_no = _register_client(server, identity=b"old-identity")

    # State to resync so both a global and a client NV snapshot are produced.
    assert server._apply_global_var_set(ROOM, client_no, "k", "v")
    assert server._apply_client_var_set(ROOM, client_no, client_no, "ck", "cv")

    recorded = _spy_enqueue(server)

    # Reconnect: same device_id, different control identity.
    server._handle_client_hello(
        b"new-identity", ROOM, {"deviceId": DEVICE_ID, "isStealthMode": False}
    )

    mapping_idx = recorded.index(binary_serializer.MSG_DEVICE_ID_MAPPING)
    assert binary_serializer.MSG_GLOBAL_VAR_SYNC in recorded
    assert binary_serializer.MSG_CLIENT_VAR_SYNC in recorded
    assert mapping_idx < recorded.index(
        binary_serializer.MSG_GLOBAL_VAR_SYNC
    ), f"mapping must be enqueued before global NV sync, got order: {recorded}"
    assert mapping_idx < recorded.index(
        binary_serializer.MSG_CLIENT_VAR_SYNC
    ), f"mapping must be enqueued before client NV sync, got order: {recorded}"


def test_new_client_enqueues_mapping_before_nv() -> None:
    """First-connect hello sends the ID mapping before the global NV snapshot."""
    server = _make_server()

    # Seed a global NV from another client so the new client receives a snapshot.
    with server._rooms_lock:
        server._initialize_room(ROOM)
        other_no = server._get_or_assign_client_no(ROOM, "other-device")
        server.rooms[ROOM]["other-device"] = {
            "control_identity": b"other-identity",
            "transform_identity": None,
            "last_update": time.monotonic(),
            "transform_data": None,
            "client_no": other_no,
            "is_stealth": False,
        }
    assert server._apply_global_var_set(ROOM, other_no, "k", "v")

    recorded = _spy_enqueue(server)

    # First connect for DEVICE_ID (not yet in the room).
    server._handle_client_hello(
        b"fresh-identity", ROOM, {"deviceId": DEVICE_ID, "isStealthMode": False}
    )

    assert binary_serializer.MSG_DEVICE_ID_MAPPING in recorded
    assert binary_serializer.MSG_GLOBAL_VAR_SYNC in recorded
    assert recorded.index(binary_serializer.MSG_DEVICE_ID_MAPPING) < recorded.index(
        binary_serializer.MSG_GLOBAL_VAR_SYNC
    ), f"mapping must be enqueued before NV sync, got order: {recorded}"
