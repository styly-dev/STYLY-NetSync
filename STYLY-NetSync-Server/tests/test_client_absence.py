"""Tests for two-stage client expiry (absent -> removed) and join sync scope.

A transient link stall used to be indistinguishable from a real departure:
the client entry was deleted on timeout, so the returning client re-joined as
a brand new client and every join re-broadcast the full room snapshot to
everyone. Both effects scale with room size, so a few flapping devices could
saturate the control lane. These tests pin the current behavior.
"""

from __future__ import annotations

import pytest

from styly_netsync import binary_serializer
from styly_netsync.server import NetSyncServer


@pytest.fixture
def server() -> NetSyncServer:
    return NetSyncServer(enable_server_discovery=False)


ROOM = "absence_room"
DEVICE = "device-a"
PEER = "device-b"


def _join(srv: NetSyncServer, device_id: str, identity: bytes) -> None:
    srv._handle_client_hello(
        identity, ROOM, {"deviceId": device_id, "isStealthMode": True}
    )


def _drain_router_queue(srv: NetSyncServer) -> list[tuple[bytes, bytes, bytes]]:
    drained: list[tuple[bytes, bytes, bytes]] = []
    while not srv._router_queue_ctrl.empty():
        drained.append(srv._router_queue_ctrl.get_nowait())
    return drained


def _msg_types(packets: list[tuple[bytes, bytes, bytes]]) -> list[int]:
    return [binary_serializer.deserialize(p[2])[0] for p in packets]


class TestTwoStageExpiry:
    def test_timeout_marks_absent_and_retains_registration(
        self, server: NetSyncServer
    ) -> None:
        """A timed-out client keeps its entry, control identity and client number."""
        _join(server, DEVICE, b"ident-a")
        entry = server.rooms[ROOM][DEVICE]
        client_no = entry["client_no"]

        entry["last_update"] -= server.CLIENT_TIMEOUT + 1
        server._cleanup_clients(entry["last_update"] + server.CLIENT_TIMEOUT + 1)

        assert DEVICE in server.rooms[ROOM], "entry must survive the timeout"
        assert server.rooms[ROOM][DEVICE]["is_present"] is False
        assert server.rooms[ROOM][DEVICE]["control_identity"] == b"ident-a"
        assert server.rooms[ROOM][DEVICE]["client_no"] == client_no

    def test_absent_client_is_excluded_from_id_mapping(
        self, server: NetSyncServer
    ) -> None:
        """Peers must still see an absent client leave, so it drops out of the mapping."""
        _join(server, DEVICE, b"ident-a")
        _join(server, PEER, b"ident-b")

        payload = server._build_id_mapping_payload(ROOM)
        assert payload is not None
        _, data, _ = binary_serializer.deserialize(payload)
        assert data is not None
        assert len(data["mappings"]) == 2

        entry = server.rooms[ROOM][DEVICE]
        entry["last_update"] -= server.CLIENT_TIMEOUT + 1
        server._cleanup_clients(entry["last_update"] + server.CLIENT_TIMEOUT + 1)

        payload = server._build_id_mapping_payload(ROOM)
        assert payload is not None
        _, data, _ = binary_serializer.deserialize(payload)
        assert data is not None
        assert [m["deviceId"] for m in data["mappings"]] == [PEER]

    def test_absent_client_receives_no_control_messages(
        self, server: NetSyncServer
    ) -> None:
        """An unreachable client must not be sent control traffic."""
        _join(server, DEVICE, b"ident-a")
        _join(server, PEER, b"ident-b")

        entry = server.rooms[ROOM][DEVICE]
        entry["last_update"] -= server.CLIENT_TIMEOUT + 1
        server._cleanup_clients(entry["last_update"] + server.CLIENT_TIMEOUT + 1)
        _drain_router_queue(server)

        server._send_ctrl_to_room_via_router(ROOM, b"payload")
        recipients = {p[0] for p in _drain_router_queue(server)}
        assert recipients == {b"ident-b"}

    def test_return_within_retention_resumes_existing_registration(
        self, server: NetSyncServer
    ) -> None:
        """A returning client resumes its entry instead of re-joining as new."""
        _join(server, DEVICE, b"ident-a")
        _join(server, PEER, b"ident-b")
        client_no = server.rooms[ROOM][DEVICE]["client_no"]

        entry = server.rooms[ROOM][DEVICE]
        entry["last_update"] -= server.CLIENT_TIMEOUT + 1
        server._cleanup_clients(entry["last_update"] + server.CLIENT_TIMEOUT + 1)
        _drain_router_queue(server)

        _join(server, DEVICE, b"ident-a")

        assert server.rooms[ROOM][DEVICE]["is_present"] is True
        assert (
            server.rooms[ROOM][DEVICE]["client_no"] == client_no
        ), "the client number must be stable across an absence"
        # Everything queued goes to the returning client only; the peer is not
        # made to re-download room state because someone else flapped.
        assert {p[0] for p in _drain_router_queue(server)} == {b"ident-a"}

    def test_entry_removed_after_retention_window(self, server: NetSyncServer) -> None:
        """The entry is dropped once the absence outlasts ABSENT_CLIENT_RETENTION."""
        _join(server, DEVICE, b"ident-a")
        entry = server.rooms[ROOM][DEVICE]
        base = entry["last_update"]

        entry["last_update"] = base - server.CLIENT_TIMEOUT - 1
        server._cleanup_clients(base)
        assert DEVICE in server.rooms[ROOM]

        # Still inside the retention window.
        server._cleanup_clients(base + server.ABSENT_CLIENT_RETENTION - 1)
        assert DEVICE in server.rooms[ROOM]

        server._cleanup_clients(base + server.ABSENT_CLIENT_RETENTION + 2)
        assert DEVICE not in server.rooms[ROOM]

    def test_absent_client_is_excluded_from_pose_broadcast(
        self, server: NetSyncServer
    ) -> None:
        """An absent client's last pose must not keep being replayed to the room."""
        server._handle_client_transform(
            b"tident-a",
            ROOM,
            {"deviceId": DEVICE, "flags": binary_serializer.POSE_FLAG_HEAD_VALID},
            b"\x01\x02\x03",
        )
        client_no = server.rooms[ROOM][DEVICE]["client_no"]
        assert client_no in server.client_transform_body_cache

        entry = server.rooms[ROOM][DEVICE]
        entry["last_update"] -= server.CLIENT_TIMEOUT + 1
        server._cleanup_clients(entry["last_update"] + server.CLIENT_TIMEOUT + 1)

        assert client_no not in server.client_transform_body_cache


class TestJoinSyncScope:
    def test_join_unicasts_room_snapshot_to_joiner_only(
        self, server: NetSyncServer
    ) -> None:
        """A join must not make the whole room re-download the variable snapshot."""
        _join(server, PEER, b"ident-b")
        server.upsert_client_variables_for_device(ROOM, PEER, {"battery": "0.9"})
        _drain_router_queue(server)

        _join(server, DEVICE, b"ident-a")

        recipients = {p[0] for p in _drain_router_queue(server)}
        assert recipients == {
            b"ident-a"
        }, "the existing peer already holds this state and must not be re-sent it"

    def test_join_announces_only_the_joiner_own_preseeded_variables(
        self, server: NetSyncServer
    ) -> None:
        """Variables seeded before a device connects are new to peers, so they ship."""
        _join(server, PEER, b"ident-b")
        server.upsert_client_variables_for_device(ROOM, DEVICE, {"experience": "exp-1"})
        _drain_router_queue(server)

        _join(server, DEVICE, b"ident-a")

        to_peer = [p for p in _drain_router_queue(server) if p[0] == b"ident-b"]
        assert binary_serializer.MSG_CLIENT_VAR_SYNC in _msg_types(to_peer)

        joiner_no = server.rooms[ROOM][DEVICE]["client_no"]
        for packet in to_peer:
            msg_type, data, _ = binary_serializer.deserialize(packet[2])
            if msg_type != binary_serializer.MSG_CLIENT_VAR_SYNC:
                continue
            assert data is not None
            assert list(data["clientVariables"].keys()) == [
                str(joiner_no)
            ], "peers must only be told about the joiner, not the whole room"
