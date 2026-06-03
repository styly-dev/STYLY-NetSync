"""Tests for split control/transform transport behavior."""

from __future__ import annotations

from unittest.mock import MagicMock

import zmq

from styly_netsync import binary_serializer
from styly_netsync.server import NetSyncServer


def test_router_control_drain_retries_blocked_packet_fifo() -> None:
    """A would-block control unicast is retained and retried before later packets."""
    srv = NetSyncServer(enable_server_discovery=False)
    router = MagicMock()
    router.send_multipart.side_effect = [zmq.Again(), None]
    srv.control_router = router
    srv.ROUTER_CTRL_DRAIN_BATCH = 1

    first = (b"ident-a", b"room", b"first")
    second = (b"ident-b", b"room", b"second")
    srv._router_queue_ctrl.put_nowait(first)
    srv._router_queue_ctrl.put_nowait(second)

    srv._drain_router_ctrl_queue()
    assert srv.ctrl_unicast_wouldblock == 1
    assert srv.ctrl_unicast_sent == 0
    assert srv._blocked_router_packet == first

    srv._drain_router_ctrl_queue()
    assert srv.ctrl_unicast_sent == 1
    assert srv._blocked_router_packet is None
    assert router.send_multipart.call_args_list[1].args[0] == [
        first[0],
        first[1],
        first[2],
    ]
    assert srv._router_queue_ctrl.get_nowait() == second


def test_transform_identity_does_not_overwrite_control_identity() -> None:
    """Transform reconnects must not replace the control identity used for RPC/NV."""
    srv = NetSyncServer(enable_server_discovery=False)
    room_id = "split-room"
    device_id = "device-a"

    hello = binary_serializer.serialize_client_hello(device_id)
    msg_type, hello_data, _ = binary_serializer.deserialize(hello)
    assert msg_type == binary_serializer.MSG_CLIENT_HELLO
    assert hello_data is not None
    srv._handle_client_hello(b"control-1", room_id, hello_data)

    pose = binary_serializer.serialize_client_transform(
        {
            "deviceId": device_id,
            "flags": 0,
            "head": {},
            "right": {},
            "left": {},
            "physical": {},
            "virtuals": [],
        }
    )
    msg_type, pose_data, raw = binary_serializer.deserialize(pose)
    assert msg_type == binary_serializer.MSG_CLIENT_POSE
    assert pose_data is not None

    srv._handle_client_transform(b"transform-1", room_id, pose_data, raw)
    srv._handle_client_transform(b"transform-2", room_id, pose_data, raw)

    with srv._rooms_lock:
        client_data = srv.rooms[room_id][device_id]
        assert client_data["control_identity"] == b"control-1"
        assert client_data["transform_identity"] == b"transform-2"


def test_wrong_lane_message_is_dropped() -> None:
    """Control lane drops transform messages instead of dispatching them."""
    srv = NetSyncServer(enable_server_discovery=False)

    srv._handle_control_message(
        b"control-1",
        "room",
        binary_serializer.MSG_CLIENT_POSE,
        {"deviceId": "device-a"},
    )

    assert srv.wrong_lane_dropped == 1
    with srv._rooms_lock:
        assert "room" not in srv.rooms


def test_rpc_target_fanout_enqueues_all_ten_targets() -> None:
    """Targeted RPC fanout should enqueue one control unicast per target."""
    srv = NetSyncServer(enable_server_discovery=False)
    room_id = "rpc-room"
    srv._initialize_room(room_id)

    for client_no in range(1, 12):
        device_id = f"device-{client_no}"
        srv.rooms[room_id][device_id] = {
            "control_identity": f"control-{client_no}".encode(),
            "transform_identity": f"transform-{client_no}".encode(),
            "last_update": 0.0,
            "transform_data": None,
            "client_no": client_no,
            "is_stealth": False,
        }
        srv.room_device_id_to_client_no[room_id][device_id] = client_no
        srv.room_client_no_to_device_id[room_id][client_no] = device_id

    srv._send_rpc_to_room(
        room_id,
        {
            "senderClientNo": 11,
            "targetClientNos": list(range(1, 11)),
            "functionName": "Burst",
            "argumentsJson": "[]",
        },
    )

    queued = []
    while not srv._router_queue_ctrl.empty():
        queued.append(srv._router_queue_ctrl.get_nowait())

    assert len(queued) == 10
    assert {item[0] for item in queued} == {
        f"control-{client_no}".encode() for client_no in range(1, 11)
    }
    assert srv.ctrl_unicast_dropped == 0
