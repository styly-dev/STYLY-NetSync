"""Tests for device-keyed Client Network Variable storage."""

from __future__ import annotations

import time
from collections.abc import Iterator
from unittest.mock import MagicMock

import pytest
from fastapi.testclient import TestClient

from styly_netsync import binary_serializer
from styly_netsync.client import net_sync_manager
from styly_netsync.rest_bridge import create_app
from styly_netsync.server import NetSyncServer


def _map_device(
    server: NetSyncServer, room_id: str, device_id: str, client_no: int
) -> None:
    server._initialize_room(room_id)
    server.room_device_id_to_client_no[room_id][device_id] = client_no
    server.room_client_no_to_device_id[room_id][client_no] = device_id
    server.device_id_last_seen[device_id] = time.monotonic()


def _decode_client_variables(payload: bytes) -> dict[str, object]:
    msg_type, data, _ = binary_serializer.deserialize(payload)
    assert msg_type == binary_serializer.MSG_CLIENT_VAR_SYNC
    assert data is not None
    return data["clientVariables"]


@pytest.fixture()
def server() -> Iterator[NetSyncServer]:
    srv = NetSyncServer(enable_server_discovery=False)
    srv._send_ctrl_to_room_via_router = MagicMock()  # type: ignore[method-assign]
    yield srv
    srv.context.term()


class TestServerClientVariableDeviceStore:
    def test_client_no_write_stores_by_device_id(self, server: NetSyncServer) -> None:
        _map_device(server, "room1", "device-a", 7)

        applied = server._apply_client_var_set(
            "room1", 2, 7, "score", "100", time.time()
        )

        assert applied is True
        assert server.client_variables["room1"]["device-a"]["score"]["value"] == "100"
        assert 7 not in server.client_variables["room1"]

    def test_sync_payload_keeps_client_no_wire_format(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)
        server._apply_client_var_set("room1", 2, 7, "score", "100", time.time())

        payload = server._build_client_var_sync_payload("room1")
        assert payload is not None
        client_variables = _decode_client_variables(payload)

        assert set(client_variables) == {"7"}
        assert client_variables["7"][0]["name"] == "score"
        assert client_variables["7"][0]["value"] == "100"

    def test_full_sync_includes_empty_mapped_client_snapshot(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)

        payload = server._build_client_var_sync_payload("room1")

        assert payload is not None
        client_variables = _decode_client_variables(payload)
        assert client_variables == {"7": []}

    def test_unmapped_device_variables_are_stored_but_not_broadcast(
        self, server: NetSyncServer
    ) -> None:
        client_no, statuses = server.upsert_client_variables_for_device(
            "room1", "device-a", {"experience-id": "exp-1"}
        )

        assert client_no is None
        assert statuses == {"experience-id": "queued"}
        assert (
            server.client_variables["room1"]["device-a"]["experience-id"]["value"]
            == "exp-1"
        )
        assert server._build_client_var_sync_payload("room1") is None

    def test_expired_device_cleanup_removes_client_variables(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)
        server._apply_client_var_set("room1", 2, 7, "score", "100", time.time())
        server.device_id_last_seen["device-a"] = (
            time.monotonic() - server.DEVICE_ID_EXPIRY_TIME - 1.0
        )

        server._cleanup_expired_device_id_mappings(time.monotonic())

        assert "device-a" not in server.client_variables["room1"]
        assert "device-a" not in server.room_device_id_to_client_no["room1"]
        assert 7 not in server.room_client_no_to_device_id["room1"]

    def test_reusable_client_no_cleanup_removes_client_variables(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)
        server.client_transform_body_cache[7] = b"old"
        server._apply_client_var_set("room1", 2, 7, "score", "100", time.time())
        server.device_id_last_seen["device-a"] = (
            time.monotonic() - server.DEVICE_ID_EXPIRY_TIME - 1.0
        )

        reusable = server._find_reusable_client_no("room1")

        assert reusable == 7
        assert "device-a" not in server.client_variables["room1"]
        assert 7 not in server.client_transform_body_cache


class TestRestClientVariableDeviceStore:
    def _client(self, server: NetSyncServer) -> TestClient:
        return TestClient(create_app("localhost", 5555, 5556, server=server))

    def test_post_unmapped_device_queues_and_get_reads_store(
        self, server: NetSyncServer
    ) -> None:
        tc = self._client(server)

        post = tc.post(
            "/v1/rooms/room1/devices/device-a/client-variables",
            json={"variables": {"experience-id": "exp-1"}},
        )
        assert post.status_code == 200
        assert post.json()["mapping"] == {"clientNo": None}
        assert post.json()["result"] == {"experience-id": {"state": "queued"}}

        get = tc.get("/v1/rooms/room1/devices/device-a/client-variables")
        assert get.status_code == 200
        assert get.json() == {
            "clientNo": None,
            "variables": {"experience-id": "exp-1"},
        }

    def test_post_mapped_device_applies_and_broadcasts(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)
        tc = self._client(server)

        response = tc.post(
            "/v1/rooms/room1/devices/device-a/client-variables",
            json={"variables": {"experience-id": "exp-1"}},
        )

        assert response.status_code == 200
        assert response.json()["mapping"] == {"clientNo": 7}
        assert response.json()["result"] == {"experience-id": {"state": "applied"}}
        server._send_ctrl_to_room_via_router.assert_called_once()
        _, payload = server._send_ctrl_to_room_via_router.call_args.args
        client_variables = _decode_client_variables(payload)
        assert set(client_variables) == {"7"}

    def test_get_single_unmapped_device_reads_saved_value(
        self, server: NetSyncServer
    ) -> None:
        server.upsert_client_variables_for_device(
            "room1", "device-a", {"experience-id": "exp-1"}
        )
        tc = self._client(server)

        response = tc.get(
            "/v1/rooms/room1/devices/device-a/client-variables/experience-id"
        )

        assert response.status_code == 200
        assert response.json() == {"clientNo": None, "value": "exp-1"}

    def test_delete_single_and_all_broadcast_mapped_snapshot(
        self, server: NetSyncServer
    ) -> None:
        _map_device(server, "room1", "device-a", 7)
        server.upsert_client_variables_for_device(
            "room1", "device-a", {"a": "1", "b": "2"}
        )
        server._send_ctrl_to_room_via_router.reset_mock()
        tc = self._client(server)

        delete_one = tc.delete("/v1/rooms/room1/devices/device-a/client-variables/a")
        assert delete_one.status_code == 200
        assert delete_one.json() == {"clientNo": 7, "deleted": True}
        _, payload = server._send_ctrl_to_room_via_router.call_args.args
        client_variables = _decode_client_variables(payload)
        assert [var["name"] for var in client_variables["7"]] == ["b"]

        server._send_ctrl_to_room_via_router.reset_mock()
        delete_all = tc.delete("/v1/rooms/room1/devices/device-a/client-variables")
        assert delete_all.status_code == 200
        assert delete_all.json() == {"clientNo": 7, "deleted": 1}
        _, payload = server._send_ctrl_to_room_via_router.call_args.args
        client_variables = _decode_client_variables(payload)
        assert client_variables["7"] == []

    def test_post_too_many_client_variables_returns_409(
        self, server: NetSyncServer
    ) -> None:
        server.MAX_CLIENT_VARS = 1
        tc = self._client(server)

        first = tc.post(
            "/v1/rooms/room1/devices/device-a/client-variables",
            json={"variables": {"a": "1"}},
        )
        overflow = tc.post(
            "/v1/rooms/room1/devices/device-a/client-variables",
            json={"variables": {"b": "2"}},
        )

        assert first.status_code == 200
        assert overflow.status_code == 409


class TestPythonClientClientVariableSnapshots:
    def test_client_variable_sync_replaces_included_client_snapshot(self) -> None:
        manager = net_sync_manager()
        events: list[tuple[int, str, str | None, str | None]] = []
        manager.on_client_variable_changed.add_listener(
            lambda client_no, name, old_value, new_value: events.append(
                (client_no, name, old_value, new_value)
            )
        )

        manager._process_client_var_sync(
            {
                "clientVariables": {
                    "7": [
                        {"name": "a", "value": "1"},
                        {"name": "b", "value": "2"},
                    ]
                }
            }
        )
        manager._process_client_var_sync(
            {"clientVariables": {"7": [{"name": "b", "value": "3"}]}}
        )

        assert manager.get_all_client_variables(7) == {"b": "3"}
        assert (7, "a", "1", None) in events
        assert (7, "b", "2", "3") in events
