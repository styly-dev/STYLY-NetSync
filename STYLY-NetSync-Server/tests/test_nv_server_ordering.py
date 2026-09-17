"""Server-authoritative Network Variable ordering (issues #448, #485).

Last-writer-wins is ordered by server application order instead of
client-supplied timestamps, so skewed device clocks on offline LAN deployments
cannot freeze or steal Network Variables. There is no per-write sequence
number: ordering relies on _rooms_lock serialization, latest-wins pending
buffers, and immediate-apply paths pruning superseded buffered writes (see
``NetSyncServer._flush_nv_drain``).
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

    def test_same_value_write_is_a_no_op(self, server: NetSyncServer) -> None:
        server._initialize_room("room1")
        assert server._apply_global_var_set("room1", 1, "a", "1") is True

        # Same value -> no-op, returns False and keeps the original last writer
        assert server._apply_global_var_set("room1", 2, "a", "1") is False
        assert server.global_variables["room1"]["a"]["lastWriterClientNo"] == 1


class TestClientVariableServerOrdering:
    def test_last_applied_write_wins(self, server: NetSyncServer) -> None:
        _map_device(server, "room1", "device-a", 7)

        assert server._apply_client_var_set("room1", 2, 7, "hp", "10") is True
        assert server._apply_client_var_set("room1", 3, 7, "hp", "20") is True

        stored = server.client_variables["room1"]["device-a"]["hp"]
        assert stored["value"] == "20"
        assert stored["lastWriterClientNo"] == 3

    def test_rest_write_after_live_write_wins(self, server: NetSyncServer) -> None:
        _map_device(server, "room1", "device-a", 7)

        server._apply_client_var_set("room1", 2, 7, "hp", "10")
        server.upsert_client_variables_for_device("room1", "device-a", {"hp": "30"})

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
