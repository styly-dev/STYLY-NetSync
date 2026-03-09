"""Unit tests for the REST bridge, including the global-variables endpoint."""

from __future__ import annotations

from unittest.mock import MagicMock, patch

import pytest
from fastapi.testclient import TestClient

from styly_netsync.rest_bridge import (
    MAX_GLOBAL_VARS,
    GlobalVarStore,
    RoomBridge,
    create_app,
)

# ---------------------------------------------------------------------------
# GlobalVarStore tests
# ---------------------------------------------------------------------------


class TestGlobalVarStore:
    def setup_method(self) -> None:
        self.store = GlobalVarStore()

    def test_upsert_and_get(self) -> None:
        result = self.store.upsert("room1", {"score": "10"})
        assert result == {"score": "10"}
        assert self.store.get("room1") == {"score": "10"}

    def test_upsert_merges_keys(self) -> None:
        self.store.upsert("room1", {"a": "1"})
        self.store.upsert("room1", {"b": "2"})
        assert self.store.get("room1") == {"a": "1", "b": "2"}

    def test_upsert_updates_existing_key(self) -> None:
        self.store.upsert("room1", {"score": "10"})
        self.store.upsert("room1", {"score": "20"})
        assert self.store.get("room1") == {"score": "20"}

    def test_get_unknown_room_returns_empty(self) -> None:
        assert self.store.get("unknown") == {}

    def test_rooms_are_isolated(self) -> None:
        self.store.upsert("room1", {"x": "1"})
        self.store.upsert("room2", {"y": "2"})
        assert self.store.get("room1") == {"x": "1"}
        assert self.store.get("room2") == {"y": "2"}

    def test_upsert_raises_when_limit_exceeded(self) -> None:
        kvs = {f"key{i}": "v" for i in range(MAX_GLOBAL_VARS)}
        self.store.upsert("room1", kvs)
        with pytest.raises(ValueError, match="Too many global variables"):
            self.store.upsert("room1", {"overflow": "x"})

    def test_pop_returns_data_and_removes(self) -> None:
        self.store.upsert("room1", {"a": "1", "b": "2"})
        result = self.store.pop("room1")
        assert result == {"a": "1", "b": "2"}
        assert self.store.get("room1") == {}

    def test_pop_unknown_room_returns_empty(self) -> None:
        assert self.store.pop("unknown") == {}


# ---------------------------------------------------------------------------
# RoomBridge global variable method tests
# ---------------------------------------------------------------------------


def _make_bridge(client_no: int | None = None) -> RoomBridge:
    """Create a RoomBridge with a mocked net_sync_manager."""
    mock_manager = MagicMock()
    mock_manager.client_no = client_no

    with patch("styly_netsync.rest_bridge.net_sync_manager", return_value=mock_manager):
        bridge = RoomBridge("localhost", 5555, 5556, "test_room")

    bridge._manager = mock_manager
    return bridge


class TestRoomBridgeGlobalVars:
    def test_apply_global_now_or_queue_queues_when_not_connected(self) -> None:
        bridge = _make_bridge(client_no=None)
        statuses = bridge.apply_global_now_or_queue({"k": "v"})
        assert statuses == {"k": "queued"}

    def test_apply_global_now_or_queue_applies_when_connected(self) -> None:
        bridge = _make_bridge(client_no=1)
        bridge._manager.set_global_variable.return_value = True
        statuses = bridge.apply_global_now_or_queue({"k": "v"})
        assert statuses == {"k": "applied"}
        bridge._manager.set_global_variable.assert_called_once_with("k", "v")

    def test_apply_global_marks_failed_when_set_returns_false(self) -> None:
        bridge = _make_bridge(client_no=1)
        bridge._manager.set_global_variable.return_value = False
        statuses = bridge.apply_global_now_or_queue({"k": "v"})
        assert statuses == {"k": "failed"}

    def test_apply_global_returns_empty_when_no_client_no(self) -> None:
        bridge = _make_bridge(client_no=None)
        applied = bridge._apply_global({"k": "v"})
        assert applied == set()

    def test_flush_global_vars_calls_apply_when_pending(self) -> None:
        bridge = _make_bridge(client_no=1)
        bridge._manager.set_global_variable.return_value = True

        from styly_netsync import rest_bridge

        original_store = rest_bridge.global_store
        test_store = GlobalVarStore()
        test_store.upsert("test_room", {"k": "v"})
        rest_bridge.global_store = test_store
        try:
            bridge.flush_global_vars()
        finally:
            rest_bridge.global_store = original_store

        bridge._manager.set_global_variable.assert_called_once_with("k", "v")
        # Store should be cleared after successful flush
        assert test_store.get("test_room") == {}

    def test_flush_global_vars_requeues_failed(self) -> None:
        bridge = _make_bridge(client_no=1)
        bridge._manager.set_global_variable.side_effect = [True, False]

        from styly_netsync import rest_bridge

        original_store = rest_bridge.global_store
        test_store = GlobalVarStore()
        test_store.upsert("test_room", {"ok": "1", "fail": "2"})
        rest_bridge.global_store = test_store
        try:
            bridge.flush_global_vars()
        finally:
            rest_bridge.global_store = original_store

        # Only the failed variable should remain in the store
        assert test_store.get("test_room") == {"fail": "2"}

    def test_flush_global_vars_skips_when_empty(self) -> None:
        bridge = _make_bridge(client_no=1)
        from styly_netsync import rest_bridge

        original_store = rest_bridge.global_store
        test_store = GlobalVarStore()
        rest_bridge.global_store = test_store
        try:
            bridge.flush_global_vars()
        finally:
            rest_bridge.global_store = original_store

        bridge._manager.set_global_variable.assert_not_called()


# ---------------------------------------------------------------------------
# Endpoint tests via TestClient
# ---------------------------------------------------------------------------


@pytest.fixture()
def client() -> TestClient:
    """Create a TestClient with BridgeManager fully mocked out."""
    with patch("styly_netsync.rest_bridge.BridgeManager") as MockBridgeManager:
        mock_bridge = MagicMock()
        mock_bridge.apply_global_now_or_queue.return_value = {"score": "queued"}
        MockBridgeManager.return_value.get.return_value = mock_bridge

        app = create_app("localhost", 5555, 5556)
        return TestClient(app)


class TestGlobalVariablesEndpoint:
    def test_post_returns_200(self, client: TestClient) -> None:
        resp = client.post(
            "/v1/rooms/room1/global-variables",
            json={"vars": {"score": "42"}},
        )
        assert resp.status_code == 200

    def test_post_response_structure(self, client: TestClient) -> None:
        resp = client.post(
            "/v1/rooms/room1/global-variables",
            json={"vars": {"score": "42"}},
        )
        body = resp.json()
        assert body["roomId"] == "room1"
        assert "result" in body
        assert "score" in body["result"]

    def test_post_empty_vars_returns_400(self, client: TestClient) -> None:
        resp = client.post(
            "/v1/rooms/room1/global-variables",
            json={"vars": {}},
        )
        assert resp.status_code == 400

    def test_post_missing_vars_field_returns_400(self, client: TestClient) -> None:
        resp = client.post("/v1/rooms/room1/global-variables", json={})
        assert resp.status_code == 400

    def test_post_var_name_too_long_returns_422(self, client: TestClient) -> None:
        long_name = "x" * 65
        resp = client.post(
            "/v1/rooms/room1/global-variables",
            json={"vars": {long_name: "v"}},
        )
        assert resp.status_code == 422

    def test_post_var_value_too_long_returns_422(self, client: TestClient) -> None:
        resp = client.post(
            "/v1/rooms/room1/global-variables",
            json={"vars": {"k": "x" * 1025}},
        )
        assert resp.status_code == 422

    def test_post_too_many_global_vars_returns_409(self) -> None:
        """Filling the store then adding one more queued var should yield 409."""
        from styly_netsync import rest_bridge

        original_store = rest_bridge.global_store
        rest_bridge.global_store = GlobalVarStore()
        try:
            # Fill up the store with queued vars
            kvs = {f"key{i}": "v" for i in range(MAX_GLOBAL_VARS)}
            rest_bridge.global_store.upsert("room_full", kvs)

            with patch("styly_netsync.rest_bridge.BridgeManager") as MockBM:
                mock_bridge = MagicMock()
                # Return "queued" matching the actual input key
                mock_bridge.apply_global_now_or_queue.return_value = {
                    "overflow": "queued"
                }
                MockBM.return_value.get.return_value = mock_bridge

                app = create_app("localhost", 5555, 5556)
                tc = TestClient(app)
                resp = tc.post(
                    "/v1/rooms/room_full/global-variables",
                    json={"vars": {"overflow": "x"}},
                )
                assert resp.status_code == 409
        finally:
            rest_bridge.global_store = original_store

    def test_post_applied_vars_not_stored(self) -> None:
        """Variables that are applied immediately should NOT be stored."""
        from styly_netsync import rest_bridge

        original_store = rest_bridge.global_store
        rest_bridge.global_store = GlobalVarStore()
        try:
            with patch("styly_netsync.rest_bridge.BridgeManager") as MockBM:
                mock_bridge = MagicMock()
                # All vars applied immediately
                mock_bridge.apply_global_now_or_queue.return_value = {"k": "applied"}
                MockBM.return_value.get.return_value = mock_bridge

                app = create_app("localhost", 5555, 5556)
                tc = TestClient(app)
                resp = tc.post(
                    "/v1/rooms/room1/global-variables",
                    json={"vars": {"k": "v"}},
                )
                assert resp.status_code == 200
                # Store should remain empty since the var was applied
                assert rest_bridge.global_store.get("room1") == {}
        finally:
            rest_bridge.global_store = original_store
