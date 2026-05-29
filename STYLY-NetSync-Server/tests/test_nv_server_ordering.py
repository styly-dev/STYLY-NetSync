"""Server-authoritative Network Variable ordering (issue #448).

The server assigns a per-room monotonic write sequence for last-writer-wins
instead of trusting client-supplied timestamps, so skewed device clocks on
offline LAN deployments cannot freeze or steal Network Variables.
"""

from __future__ import annotations

import time
from collections.abc import Iterator
from unittest.mock import MagicMock

import pytest

from styly_netsync.server import NetSyncServer


@pytest.fixture()
def server() -> Iterator[NetSyncServer]:
    srv = NetSyncServer(enable_server_discovery=False)
    srv._send_ctrl_to_room_via_router = MagicMock()  # type: ignore[method-assign]
    yield srv
    srv.context.term()


def _map_device(
    srv: NetSyncServer, room_id: str, device_id: str, client_no: int
) -> None:
    srv._initialize_room(room_id)
    srv.room_device_id_to_client_no[room_id][device_id] = client_no
    srv.room_client_no_to_device_id[room_id][client_no] = device_id
    srv.device_id_last_seen[device_id] = time.monotonic()


class TestGlobalVariableServerOrdering:
    def test_last_applied_write_wins(self, server: NetSyncServer) -> None:
        server._initialize_room("room1")

        assert server._apply_global_var_set("room1", 1, "score", "100") is True
        assert server._apply_global_var_set("room1", 2, "score", "200") is True

        stored = server.global_variables["room1"]["score"]
        assert stored["value"] == "200"
        assert stored["lastWriterClientNo"] == 2

    def test_write_sequence_is_monotonic_and_server_assigned(
        self, server: NetSyncServer
    ) -> None:
        server._initialize_room("room1")
        assert server.nv_write_seq["room1"] == 0

        server._apply_global_var_set("room1", 1, "a", "1")
        server._apply_global_var_set("room1", 1, "b", "2")

        assert server.global_variables["room1"]["a"]["version"] == 1
        assert server.global_variables["room1"]["b"]["version"] == 2
        assert server.nv_write_seq["room1"] == 2

    def test_no_op_value_does_not_consume_sequence(self, server: NetSyncServer) -> None:
        server._initialize_room("room1")
        assert server._apply_global_var_set("room1", 1, "a", "1") is True
        seq_after_first = server.nv_write_seq["room1"]

        # Same value -> no-op, returns False and must not bump the sequence
        assert server._apply_global_var_set("room1", 2, "a", "1") is False
        assert server.nv_write_seq["room1"] == seq_after_first


class TestClientVariableServerOrdering:
    def test_last_applied_write_wins(self, server: NetSyncServer) -> None:
        _map_device(server, "room1", "device-a", 7)

        assert server._apply_client_var_set("room1", 2, 7, "hp", "10") is True
        assert server._apply_client_var_set("room1", 3, 7, "hp", "20") is True

        stored = server.client_variables["room1"]["device-a"]["hp"]
        assert stored["value"] == "20"
        assert stored["lastWriterClientNo"] == 3

    def test_rest_and_live_writes_share_one_sequence_domain(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)

        server._apply_client_var_set("room1", 2, 7, "hp", "10")
        live_seq = server.client_variables["room1"]["device-a"]["hp"]["version"]

        server.upsert_client_variables_for_device("room1", "device-a", {"hp": "30"})
        rest_seq = server.client_variables["room1"]["device-a"]["hp"]["version"]

        assert rest_seq > live_seq
        assert server.client_variables["room1"]["device-a"]["hp"]["value"] == "30"


class TestLiveVsRestOrderingRegression:
    """Regression: a newer REST write must not be clobbered by an older live
    write that was still buffered when the REST write arrived.

    Live socket writes are coalesced into a pending buffer and applied later by
    ``_flush_nv_drain``; REST writes apply immediately. Once client timestamps
    were removed, an out-of-order application no longer self-rejects, so the
    REST path must prune any superseded buffered write for the same key.
    """

    def test_buffered_live_write_does_not_overwrite_newer_rest_write(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)

        # An older live client write is buffered, awaiting the next flush.
        server._buffer_client_var_set(
            "room1",
            {
                "senderClientNo": 7,
                "targetClientNo": 7,
                "variableName": "hp",
                "variableValue": "10",
            },
        )

        # A REST upsert applies a newer value immediately.
        server.upsert_client_variables_for_device("room1", "device-a", {"hp": "20"})

        # Draining must not resurrect the stale buffered "10" over the REST "20".
        server._flush_nv_drain("room1")

        assert server.client_variables["room1"]["device-a"]["hp"]["value"] == "20"


class TestRoomCleanupReleasesNvWriteSeq:
    """Regression: room cleanup must drop nv_write_seq so per-room sequence
    entries do not accumulate under room churn."""

    def test_nv_write_seq_entry_is_removed_on_room_cleanup(
        self, server: NetSyncServer
    ) -> None:
        server._initialize_room("room1")
        server._apply_global_var_set("room1", 1, "a", "1")
        assert "room1" in server.nv_write_seq

        # Room has been empty long enough to be reclaimed by the real cleanup.
        server.room_empty_since["room1"] = 0.0
        server._cleanup_clients(server.EMPTY_ROOM_EXPIRY_TIME + 100.0)

        assert "room1" not in server.rooms
        assert "room1" not in server.nv_write_seq
