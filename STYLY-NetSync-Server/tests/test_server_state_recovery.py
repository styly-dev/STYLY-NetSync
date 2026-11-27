"""Recovery under burst joins, backpressure, and concurrent state changes."""

from __future__ import annotations

import time
from collections.abc import Iterator
from queue import Full
from unittest.mock import MagicMock

import pytest
import zmq

from styly_netsync import binary_serializer as wire
from styly_netsync.control_outbox import ControlEventQueue
from styly_netsync.server import NetSyncServer


@pytest.fixture()
def server() -> Iterator[NetSyncServer]:
    srv = NetSyncServer(enable_server_discovery=False)
    srv.control_router = MagicMock()
    yield srv
    srv.context.term()


def hello(srv: NetSyncServer, device: str, identity: bytes | None = None) -> None:
    srv._handle_client_hello(
        identity or device.encode(), "room", {"deviceId": device, "isStealthMode": True}
    )


def frames(srv: NetSyncServer) -> list[list[bytes]]:
    return [call.args[0] for call in srv.control_router.send_multipart.call_args_list]


def drain(srv: NetSyncServer) -> None:
    for _ in range(100):
        srv._drain_router_ctrl_queue()
        if (
            not srv.state_sync_stats()["pending_receivers"]
            and srv._router_queue_ctrl.empty()
        ):
            return
    pytest.fail("State did not converge")


def test_initial_sync_survives_full_event_queue(server: NetSyncServer) -> None:
    for _ in range(server.ROUTER_CTRL_QUEUE_MAXSIZE):
        server._enqueue_router(b"slow", "room", b"event")
    hello(server, "new-peer")
    server._apply_global_var_set("room", 1, "route", "current")
    server._broadcast_global_var_sync("room")
    assert server.ctrl_unicast_dropped == 0
    server._drain_router_ctrl_queue()
    sent = [p for p in frames(server) if p[0] == b"new-peer"]
    assert [p[2][0] for p in sent] == [
        wire.MSG_DEVICE_ID_MAPPING,
        wire.MSG_GLOBAL_VAR_SYNC,
        wire.MSG_CLIENT_VAR_SYNC,
    ]
    assert server.state_sync_stats()["pending_receivers"] == 0
    assert len(frames(server)) <= server.ROUTER_CTRL_DRAIN_BATCH


@pytest.mark.parametrize("error", [zmq.Again(), zmq.ZMQError(zmq.EHOSTUNREACH)])
def test_same_identity_hello_recovers_without_socket_recreation(
    server: NetSyncServer, error: zmq.ZMQError
) -> None:
    hello(server, "new-peer")
    server.control_router.send_multipart.side_effect = error
    server._drain_router_ctrl_queue()
    assert server.state_sync_stats()["pending_receivers"] == 1
    assert server.ctrl_unicast_dropped == 0
    hello(server, "new-peer")
    server.control_router.send_multipart.side_effect = None
    server._state_sync[("room", "new-peer")].retry_at = 0
    drain(server)
    assert frames(server)[-1][2][0] == wire.MSG_CLIENT_VAR_SYNC
    assert server.state_sync_retries == 1


def test_one_unreachable_receiver_does_not_block_others(server: NetSyncServer) -> None:
    for device in ["slow", "healthy"]:
        hello(server, device)

    def send(parts: list[bytes], **_: object) -> None:
        if parts[0] == b"slow":
            raise zmq.Again()

    server.control_router.send_multipart.side_effect = send
    server._drain_router_ctrl_queue()
    assert ("room", "healthy") not in server._pending_state_sync
    assert ("room", "slow") in server._pending_state_sync
    assert server._control_poll_timeout() > 0


def test_reconnect_moves_pending_sync_to_new_identity(server: NetSyncServer) -> None:
    hello(server, "device", b"old")
    server.control_router.send_multipart.side_effect = zmq.Again()
    server._drain_router_ctrl_queue()
    server.control_router.reset_mock(side_effect=True)
    hello(server, "device", b"new")
    drain(server)
    assert all(p[0] == b"new" for p in frames(server))
    assert frames(server)[0][2][0] == wire.MSG_DEVICE_ID_MAPPING


def test_multiple_partial_updates_and_empty_snapshot_are_preserved(
    server: NetSyncServer,
) -> None:
    hello(server, "a")
    hello(server, "b")
    drain(server)
    server.control_router.reset_mock()
    server._apply_global_var_set("room", 1, "one", "1")
    server._broadcast_global_var_sync("room")
    server._apply_global_var_set("room", 1, "two", "2")
    server._broadcast_global_var_sync("room")
    server._apply_client_var_set("room", 1, 1, "hp", "10")
    server._broadcast_client_var_sync("room", {1})
    server._broadcast_client_var_sync("room", {2})
    drain(server)
    for identity in (b"a", b"b"):
        messages = [
            wire.deserialize(p[2])[:2] for p in frames(server) if p[0] == identity
        ]
        global_data = next(d for t, d in messages if t == wire.MSG_GLOBAL_VAR_SYNC)
        assert {v["name"]: v["value"] for v in global_data["variables"]} == {
            "one": "1",
            "two": "2",
        }
        client_data = next(d for t, d in messages if t == wire.MSG_CLIENT_VAR_SYNC)
        assert client_data["clientVariables"]["1"][0]["value"] == "10"
        assert client_data["clientVariables"]["2"] == []


def test_concurrent_new_revision_remains_pending_after_send(
    server: NetSyncServer,
) -> None:
    hello(server, "a")
    server._apply_global_var_set("room", 1, "route", "old")
    server._broadcast_global_var_sync("room")
    changed = False

    def send(parts: list[bytes], **_: object) -> None:
        nonlocal changed
        if not changed:
            changed = True
            server._apply_global_var_set("room", 1, "route", "new")
            server._broadcast_global_var_sync("room")

    server.control_router.send_multipart.side_effect = send
    server._drain_router_ctrl_queue()
    assert server.state_sync_stats()["pending_receivers"] == 1
    drain(server)
    payloads = [
        wire.deserialize(p[2])[1]
        for p in frames(server)
        if p[2][0] == wire.MSG_GLOBAL_VAR_SYNC
    ]
    assert [d["variables"][0]["value"] for d in payloads] == ["old", "new"]


def test_existing_clients_get_mapping_before_new_peer_variables(
    server: NetSyncServer,
) -> None:
    hello(server, "existing-peer")
    drain(server)
    server.control_router.reset_mock()
    hello(server, "new-peer")
    server._apply_global_var_set("room", 2, "target", "new-peer:2")
    server._broadcast_global_var_sync("room")
    drain(server)
    sent = [
        wire.deserialize(p[2])[:2] for p in frames(server) if p[0] == b"existing-peer"
    ]
    assert sent[0][0] == wire.MSG_DEVICE_ID_MAPPING
    assert any(m["deviceId"] == "new-peer" for m in sent[0][1]["mappings"])
    assert sent[1][0] == wire.MSG_GLOBAL_VAR_SYNC


def test_join_does_not_resend_full_global_snapshot_to_peers(
    server: NetSyncServer,
) -> None:
    hello(server, "existing")
    server._apply_global_var_set("room", 1, "route", "value")
    server._broadcast_global_var_sync("room")
    drain(server)
    server.control_router.reset_mock()
    server.upsert_client_variables_for_device("room", "joining", {"version": "1"})
    hello(server, "joining")
    drain(server)
    for identity in (b"existing", b"joining"):
        sent = [wire.deserialize(p[2])[:2] for p in frames(server) if p[0] == identity]
        assert any(t == wire.MSG_GLOBAL_VAR_SYNC for t, _ in sent) == (
            identity == b"joining"
        )
        data = next(d for t, d in sent if t == wire.MSG_CLIENT_VAR_SYNC)
        assert data["clientVariables"]["2"][0]["value"] == "1"
        if identity == b"existing":
            assert set(data["clientVariables"]) == {"2"}


def test_burst_joins_coalesce_to_bounded_receiver_state(server: NetSyncServer) -> None:
    client_count = 100
    for i in range(client_count):
        hello(server, f"client-{i}")
    assert server._router_queue_ctrl.empty()
    assert server.state_sync_stats()["pending_receivers"] == client_count
    assert server.ctrl_unicast_dropped == 0
    drain(server)
    # One mapping and one client snapshot each.
    assert server.state_sync_sent == client_count * 2


def test_timeout_removes_retry_state(server: NetSyncServer) -> None:
    hello(server, "a")
    server._cleanup_clients(time.monotonic() + server.CLIENT_TIMEOUT + 1)
    assert not server._state_sync
    assert not server._pending_state_sync
    server._cleanup_clients(time.monotonic() + server.EMPTY_ROOM_EXPIRY_TIME + 100)
    assert "room" not in server._mapping_generation


def test_receive_loop_yields_after_batch_limit(
    server: NetSyncServer, monkeypatch: pytest.MonkeyPatch
) -> None:
    router = MagicMock()
    router.recv_multipart.return_value = [
        b"identity",
        b"room",
        wire.serialize_client_hello("device", True),
    ]
    monkeypatch.setattr("styly_netsync.server.time.monotonic", lambda: 100)
    server._drain_incoming_router(router, "control")
    assert router.recv_multipart.call_count == server.ROUTER_RECV_BATCH
    assert server.state_sync_stats()["pending_receivers"] == 1


def test_receive_time_budget_yields_even_before_batch_limit(
    server: NetSyncServer, monkeypatch: pytest.MonkeyPatch
) -> None:
    router = MagicMock()
    router.recv_multipart.return_value = []
    server._handle_incoming_router_message = MagicMock()
    monkeypatch.setattr(
        "styly_netsync.server.time.monotonic", MagicMock(side_effect=[0, 0.003])
    )
    server._drain_incoming_router(router, "control")
    assert router.recv_multipart.call_count == 1


def test_event_retry_reserves_capacity_during_concurrent_enqueue() -> None:
    queue = ControlEventQueue(2)
    first, second = (b"a", b"room", b"1"), (b"a", b"room", b"2")
    queue.put_nowait(first)
    queue.put_nowait(second)
    batch = queue.take_batch(1)
    with pytest.raises(Full):
        queue.put_nowait((b"b", b"room", b"new"))
    queue.finish_batch(len(batch), batch)
    assert queue.qsize() == 2
    assert queue.get_nowait() == first
    assert queue.get_nowait() == second


def test_large_blocked_event_backlog_does_not_starve_another_identity(
    server: NetSyncServer,
) -> None:
    for _ in range(100):
        server._enqueue_router(b"slow", "room", b"event")
    server._enqueue_router(b"healthy", "room", b"event")

    def send(parts: list[bytes], **_: object) -> None:
        if parts[0] == b"slow":
            raise zmq.Again()

    server.control_router.send_multipart.side_effect = send
    server._drain_router_ctrl_queue()
    assert server.ctrl_unicast_sent == 1
    assert server._router_queue_ctrl.qsize() == 100
    assert server.ctrl_unicast_dropped == 0


def test_bootstrap_retry_keeps_events_behind_initial_variables(
    server: NetSyncServer,
) -> None:
    hello(server, "a")
    server._enqueue_router(b"a", "room", b"event")

    def send(parts: list[bytes], **_: object) -> None:
        if parts[2][0] == wire.MSG_CLIENT_VAR_SYNC:
            raise zmq.Again()

    server.control_router.send_multipart.side_effect = send
    server._drain_router_ctrl_queue()
    assert not any(p[2] == b"event" for p in frames(server))
    server.control_router.send_multipart.side_effect = None
    server._state_sync[("room", "a")].retry_at = 0
    drain(server)
    assert frames(server)[-1][2] == b"event"
