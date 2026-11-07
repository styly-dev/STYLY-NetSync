"""
Tests that a client join sends the NV snapshot only to the joining client.

Regression guard: the server used to re-broadcast the whole room's global and
client variable snapshots to every client on every join. Peers already hold
that state, so a burst of N joins only produced O(N^2) router control messages,
overflowed the bounded router queue (1024 entries) and dropped the ID mapping
and NV snapshots of later joiners, which then became Ready with missing
variables.

These tests pin the scope that prevents that: the joiner gets the full snapshot
via unicast, peers get nothing unless the joiner has client variables of its
own, and the number of control messages per join is constant.
"""

from __future__ import annotations

import time

import pytest

from styly_netsync import NetSyncServer, binary_serializer

ROOM = "join_scope_test"
JOINER_DEVICE = "device-joiner"
JOINER_IDENTITY = b"identity-joiner"


def _make_server() -> NetSyncServer:
    """Return an unstarted server (no sockets, no threads) for direct calls."""
    return NetSyncServer(
        dealer_port=15730,
        pub_port=15731,
        enable_server_discovery=False,
    )


def _register_peers(server: NetSyncServer, count: int) -> list[int]:
    """Register ``count`` connected peers in ROOM and return their client numbers."""
    client_nos: list[int] = []
    with server._rooms_lock:
        server._initialize_room(ROOM)
        for i in range(count):
            device_id = f"device-peer-{i}"
            client_no = server._get_or_assign_client_no(ROOM, device_id)
            server.rooms[ROOM][device_id] = {
                "control_identity": f"identity-peer-{i}".encode(),
                "transform_identity": None,
                "last_update": time.monotonic(),
                "transform_data": None,
                "client_no": client_no,
                "is_stealth": False,
            }
            client_nos.append(client_no)
    return client_nos


def _spy_enqueue(server: NetSyncServer) -> list[tuple[bytes, int, bytes]]:
    """Record (identity, message type, payload) for every router enqueue."""
    recorded: list[tuple[bytes, int, bytes]] = []

    def spy(identity: bytes, room_id: str, message_bytes: bytes) -> None:
        recorded.append((identity, message_bytes[0], message_bytes))

    server._enqueue_router = spy  # type: ignore[method-assign]
    return recorded


def _hello(server: NetSyncServer) -> None:
    server._handle_client_hello(
        JOINER_IDENTITY, ROOM, {"deviceId": JOINER_DEVICE, "isStealthMode": False}
    )


def test_new_client_gets_full_snapshot_and_peers_get_nothing() -> None:
    """The joiner receives mapping + global + client snapshots; peers receive nothing."""
    server = _make_server()
    peer_no = _register_peers(server, 3)[0]
    assert server._apply_global_var_set(ROOM, peer_no, "g", "1")
    assert server._apply_client_var_set(ROOM, peer_no, peer_no, "c", "2")

    recorded = _spy_enqueue(server)
    _hello(server)

    to_joiner = [t for ident, t, _ in recorded if ident == JOINER_IDENTITY]
    assert to_joiner == [
        binary_serializer.MSG_DEVICE_ID_MAPPING,
        binary_serializer.MSG_GLOBAL_VAR_SYNC,
        binary_serializer.MSG_CLIENT_VAR_SYNC,
    ]
    assert all(ident == JOINER_IDENTITY for ident, _, _ in recorded), (
        "peers must not receive any NV snapshot on a join, got: "
        f"{[(i, t) for i, t, _ in recorded]}"
    )


@pytest.mark.parametrize("peer_count", [1, 50])
def test_join_control_traffic_does_not_grow_with_room_size(peer_count: int) -> None:
    """A join enqueues a fixed number of control messages regardless of room size."""
    server = _make_server()
    peer_no = _register_peers(server, peer_count)[0]
    assert server._apply_global_var_set(ROOM, peer_no, "g", "1")

    recorded = _spy_enqueue(server)
    _hello(server)

    # mapping + global snapshot + client snapshot, all unicast to the joiner.
    assert len(recorded) == 3
    assert {ident for ident, _, _ in recorded} == {JOINER_IDENTITY}


def test_joiner_with_queued_variables_is_announced_to_peers() -> None:
    """Variables queued for the device before it joined are sent to peers.

    This is the one piece of state peers cannot already hold, so it is
    broadcast, but scoped to the joiner's client number only.
    """
    server = _make_server()
    _register_peers(server, 2)
    server.upsert_client_variables_for_device(ROOM, JOINER_DEVICE, {"exp": "e1"})

    recorded = _spy_enqueue(server)
    _hello(server)

    with server._rooms_lock:
        joiner_no = server.room_device_id_to_client_no[ROOM][JOINER_DEVICE]

    peer_messages = [
        (ident, t, payload)
        for ident, t, payload in recorded
        if ident != JOINER_IDENTITY
    ]
    assert len(peer_messages) == 2, f"each peer gets exactly one message: {recorded}"
    for _ident, msg_type, payload in peer_messages:
        assert msg_type == binary_serializer.MSG_CLIENT_VAR_SYNC
        _, data, _ = binary_serializer.deserialize(payload)
        assert data is not None
        client_variables = data["clientVariables"]
        assert set(client_variables) == {str(joiner_no)}
        assert client_variables[str(joiner_no)][0]["name"] == "exp"
        assert client_variables[str(joiner_no)][0]["value"] == "e1"


def test_connection_burst_does_not_overflow_router_queue() -> None:
    """Joining many clients in a burst must not drop any initial sync message.

    Uses the real bounded router queue rather than a spy. With the full-room
    re-broadcast, 100 joins enqueued about 10,200 messages and overflowed it.
    """
    server = _make_server()
    with server._rooms_lock:
        server._initialize_room(ROOM)
    assert server._apply_global_var_set(ROOM, 0, "g", "1")

    burst = 100
    assert burst * 3 <= server.ROUTER_CTRL_QUEUE_MAXSIZE
    for i in range(burst):
        server._handle_client_hello(
            f"identity-{i}".encode(),
            ROOM,
            {"deviceId": f"device-{i}", "isStealthMode": False},
        )

    assert server.ctrl_unicast_dropped == 0
    # Exactly mapping + global + client snapshot per joiner, nothing for peers.
    assert server._router_queue_ctrl.qsize() == burst * 3
