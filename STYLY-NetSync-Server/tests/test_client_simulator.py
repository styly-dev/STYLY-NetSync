"""Regression coverage for simulator/Unity connection and liveness parity."""

import threading
import time
from unittest.mock import MagicMock

import pytest
import zmq

from styly_netsync import binary_serializer as wire
from styly_netsync.client_simulator import (
    MovementPattern,
    SimulatedClient,
    SimulationConfig,
    Vector3,
)
from styly_netsync.simulator_transport import NetworkTransport


def make_client(stealth=False):
    transport = MagicMock(spec=NetworkTransport)
    transport.connected = True
    transport.connection_error = None
    transport.recv_dealer_control.return_value = None
    transport.recv_broadcast.return_value = None
    config = SimulationConfig(
        "test-device", MovementPattern.CIRCLE, Vector3(), virtual_count=0
    )
    client = SimulatedClient(
        config, transport, "room", simulate_battery=False, stealth_mode=stealth
    )
    client.start_time = client.last_update_time = 100.0
    client._last_send_time = 100.0
    client._connect(100.0)
    return client, transport


def make_transport():
    transport = NetworkTransport(
        MagicMock(), "tcp://127.0.0.1", 5555, 5557, 5556, "room"
    )
    transport.connected = True
    transport.socket = MagicMock()
    transport.transform_socket = MagicMock()
    return transport


def test_stealth_uses_repeated_control_hello_at_send_rate():
    client, transport = make_client(stealth=True)
    for index in range(1, 11):
        client._tick(100 + index * 0.101)
    calls = transport.send_client_hello.call_args_list
    assert len(calls) == 11  # One initial non-stealth HELLO, then ten stealth HELLOs.
    assert calls[0].args == ("room", "test-device")
    assert all(call.kwargs == {"is_stealth": True} for call in calls[1:])
    transport.send_transform.assert_not_called()


def test_stealth_backpressure_does_not_reconnect_or_retry_before_send_interval():
    client, transport = make_client(stealth=True)
    transport.send_client_hello.return_value = False
    client._tick(100.11)
    client._tick(100.12)
    assert transport.send_client_hello.call_count == 2
    assert client._reconnect_at is None
    transport.disconnect.assert_not_called()


def test_server_silence_does_not_create_application_reconnect():
    client, transport = make_client(stealth=True)
    client._tick(160.0)
    transport.disconnect.assert_not_called()
    assert transport.connect.call_count == 1


def test_network_exception_reconnects_after_ten_seconds_and_resets_readiness():
    client, transport = make_client(stealth=True)
    client.client_number = 7
    client.has_initial_sync = True
    transport.connection_error = "connection failed"
    client._tick(101)
    assert client.client_number == 0
    assert not client.has_initial_sync
    client._tick(110.99)
    assert transport.connect.call_count == 1
    transport.connection_error = None
    client._tick(111)
    assert transport.connect.call_count == 2
    assert client._reconnect_at is None


def test_ready_requires_mapping_and_nv_sync_or_two_second_fallback():
    client, _ = make_client()
    client._process_mapping_message(
        wire.MSG_DEVICE_ID_MAPPING,
        {"mappings": [{"clientNo": 7, "deviceId": "test-device"}]},
        False,
    )
    assert not client.is_ready
    client._tick(101.99)
    assert not client.is_ready
    client._tick(102)
    assert client.is_ready
    client.client_number = 0
    assert not client.is_ready  # NV timeout must never invent a client number.


def test_initial_sync_updates_caches_even_when_receive_statistics_are_disabled():
    client, _ = make_client()
    client.recv_stats = None
    client._handle_broadcast_payload(
        wire.MSG_GLOBAL_VAR_SYNC,
        {"variables": [{"name": "target", "value": "peer"}]},
    )
    assert client.has_initial_sync
    assert client.global_variables["target"] == "peer"
    client._handle_broadcast_payload(
        wire.MSG_CLIENT_VAR_SYNC,
        {"clientVariables": {"7": [{"name": "score", "value": "42"}]}},
    )
    client._handle_broadcast_payload(
        wire.MSG_CLIENT_VAR_SYNC, {"clientVariables": {"7": []}}
    )
    assert client.client_variables[7] == {}


def test_mapping_snapshot_removes_absent_devices_without_inventing_assignment():
    client, _ = make_client()
    client.device_ids = {1: "old-peer", 7: "test-device"}
    client.client_number = 7
    client._process_mapping_message(
        wire.MSG_DEVICE_ID_MAPPING,
        {"mappings": [{"clientNo": 1, "deviceId": "another-client"}]},
        False,
    )
    assert client.device_ids == {1: "another-client"}
    # Unity only updates its local number when the snapshot includes its device.
    assert client.client_number == 7


def test_stationary_visible_pose_has_one_hz_heartbeat_and_monotonic_sequence():
    client, transport = make_client()
    pose = client.transform_builder.build_transform_data(Vector3(), 0, 0, 0)
    client._send_pose(pose, 100)
    client._send_pose(pose, 100.9)
    assert transport.send_transform.call_count == 1
    client._send_pose(pose, 101)
    assert transport.send_transform.call_count == 2
    pose["head"]["posX"] += 0.02
    client._send_pose(pose, 101.1)
    assert transport.send_transform.call_count == 3
    assert client._pose_seq == 3


def test_control_outbox_capacity_counts_queued_messages():
    transport = make_transport()
    for _ in range(256):
        assert transport.send_client_hello("room", "device", True)
    assert not transport.send_client_hello("room", "device", True)
    assert transport.control_queue_drops == 1


def test_control_priority_batch_limit_and_latest_pose_slot():
    transport = make_transport()
    order = []
    transport.socket.send_multipart.side_effect = lambda *a, **k: order.append(
        "control"
    )
    transport.transform_socket.send_multipart.side_effect = (
        lambda *a, **k: order.append("pose")
    )
    for _ in range(70):
        transport.send_client_hello("room", "device", True)
    for seq in (1, 2, 3):
        transport.send_transform(
            "room", {"deviceId": "device", "flags": 0, "poseSeq": seq}
        )
    assert transport._flush_outgoing()
    assert order == ["control"] * 64 + ["pose"]
    assert transport._control_outbox.qsize() == 6
    payload = transport.transform_socket.send_multipart.call_args.args[0][1]
    assert wire.deserialize(payload)[1]["poseSeq"] == 3


def test_control_backpressure_preserves_fifo_and_expires_pending_packet(monkeypatch):
    transport = make_transport()
    monkeypatch.setattr("styly_netsync.simulator_transport.time.time", lambda: 100)
    transport.enqueue_control("room", b"first")
    transport.enqueue_control("room", b"second")
    transport.socket.send_multipart.side_effect = zmq.Again()
    assert not transport._flush_outgoing()
    assert transport._pending_control.payload == b"first"
    assert transport._control_outbox.qsize() == 1
    assert transport.would_block == 1
    monkeypatch.setattr("styly_netsync.simulator_transport.time.time", lambda: 105.001)
    assert not transport._flush_outgoing()
    assert transport.control_expired == 2
    assert transport._pending_control is None


def test_pose_backpressure_retains_latest_until_send_succeeds():
    transport = make_transport()
    transport.send_transform("room", {"deviceId": "device", "flags": 0, "poseSeq": 1})
    transport.transform_socket.send_multipart.side_effect = zmq.Again()
    assert not transport._flush_outgoing()
    assert transport._latest_transform is not None
    transport.send_transform("room", {"deviceId": "device", "flags": 0, "poseSeq": 2})
    transport.transform_socket.send_multipart.side_effect = None
    assert transport._flush_outgoing()
    assert transport._latest_transform is None
    payload = transport.transform_socket.send_multipart.call_args.args[0][1]
    assert wire.deserialize(payload)[1]["poseSeq"] == 2


def test_sub_coalesces_per_exact_topic_and_preserves_object_topic():
    transport = make_transport()
    transport.sub_socket = MagicMock()
    transport.sub_socket.recv_multipart.side_effect = [
        [b"room", b"old"],
        [b"room-other", b"wrong"],
        [b"room", b"new"],
        [b"room\0obj", b"object"],
        zmq.Again(),
    ]
    transport._decode = MagicMock(side_effect=lambda p: (12, {"payload": p}))
    assert transport._receive_broadcasts()
    assert transport._decode.call_count == 2
    assert transport.dropped_transform_frames == 1
    assert transport.recv_broadcast(True)[1]["payload"] == b"new"
    assert transport.recv_broadcast(True)[1]["payload"] == b"object"
    assert transport.recv_broadcast(True) is None


def test_network_receives_control_without_application_time_budget():
    transport = make_transport()
    payload = wire.serialize_client_hello("device", True)
    transport.socket.recv_multipart.side_effect = [[b"room", payload]] * 150 + [
        zmq.Again()
    ]
    assert transport._receive_control()
    assert transport._incoming_control.qsize() == 150


def test_real_tcp_restart_keeps_network_thread_and_rebinds_with_stealth_hello():
    context = zmq.Context()
    router = context.socket(zmq.ROUTER)
    router.setsockopt(zmq.LINGER, 0)
    port = router.bind_to_random_port("tcp://127.0.0.1")
    transform = context.socket(zmq.ROUTER)
    transform_port = transform.bind_to_random_port("tcp://127.0.0.1")
    pub = context.socket(zmq.PUB)
    pub_port = pub.bind_to_random_port("tcp://127.0.0.1")
    transport = NetworkTransport(
        context, "tcp://127.0.0.1", port, transform_port, pub_port, "room"
    )
    config = SimulationConfig("persistent-device", MovementPattern.CIRCLE, Vector3())
    client = SimulatedClient(
        config, transport, "room", simulate_battery=False, stealth_mode=True
    )
    stopped = threading.Event()
    thread = threading.Thread(target=client.run, args=(stopped,))

    def receive_stealth():
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if router.poll(100):
                frames = router.recv_multipart()
                kind, data, _ = wire.deserialize(frames[2])
                if kind == wire.MSG_CLIENT_HELLO and data["isStealthMode"]:
                    assert data["deviceId"] == "persistent-device"
                    return
        pytest.fail("No control-lane stealth HELLO after server bind")

    try:
        thread.start()
        receive_stealth()
        network_thread = transport._thread
        router.close()
        time.sleep(0.3)
        router = context.socket(zmq.ROUTER)
        router.setsockopt(zmq.LINGER, 0)
        router.bind(f"tcp://127.0.0.1:{port}")
        receive_stealth()
        assert transport._thread is network_thread
        assert client._reconnect_at is None
        assert not transform.poll(0)
    finally:
        stopped.set()
        thread.join(5)
        assert not thread.is_alive()
        router.close(0)
        transform.close(0)
        pub.close(0)
        context.term()
