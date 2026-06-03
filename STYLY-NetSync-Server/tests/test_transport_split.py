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


def test_hello_after_transform_first_syncs_objects() -> None:
    """A transform-before-hello join must still receive the object-ownership sync.

    With split sockets a client's first pose can reach the transform lane before
    its hello reaches the control lane. The transform handler creates the room
    entry with no control identity and skips object sync; the later hello must
    detect this first control-lane bind and run the new-client object sync.
    """
    srv = NetSyncServer(enable_server_discovery=False)
    room_id = "order-room"
    device_id = "device-order"

    # Spy on object sync to observe exactly when/with what identity it runs.
    srv._sync_objects_to_new_client = MagicMock()  # type: ignore[method-assign]

    # Transform arrives first -> entry created with control_identity=None.
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

    # No control identity yet, so the transform path must not object-sync.
    srv._sync_objects_to_new_client.assert_not_called()
    with srv._rooms_lock:
        assert srv.rooms[room_id][device_id]["control_identity"] is None

    # Hello arrives afterwards -> control lane binds for the first time.
    hello = binary_serializer.serialize_client_hello(device_id)
    msg_type, hello_data, _ = binary_serializer.deserialize(hello)
    assert msg_type == binary_serializer.MSG_CLIENT_HELLO
    assert hello_data is not None
    srv._handle_client_hello(b"control-1", room_id, hello_data)

    # Object ownership sync must run exactly once, on the control identity.
    srv._sync_objects_to_new_client.assert_called_once_with(b"control-1", room_id)
    with srv._rooms_lock:
        assert srv.rooms[room_id][device_id]["control_identity"] == b"control-1"


def test_hello_reconnect_after_bind_does_not_resync_objects() -> None:
    """A genuine control-lane reconnect (already bound) must not re-run object sync."""
    srv = NetSyncServer(enable_server_discovery=False)
    room_id = "rebind-room"
    device_id = "device-rebind"

    hello = binary_serializer.serialize_client_hello(device_id)
    _, hello_data, _ = binary_serializer.deserialize(hello)
    assert hello_data is not None

    # First hello: brand-new client, object sync expected once.
    srv._sync_objects_to_new_client = MagicMock()  # type: ignore[method-assign]
    srv._handle_client_hello(b"control-1", room_id, hello_data)
    srv._sync_objects_to_new_client.assert_called_once_with(b"control-1", room_id)

    # Second hello with a new identity: reconnect, but control was already bound,
    # so object sync must NOT run again.
    srv._sync_objects_to_new_client.reset_mock()
    srv._handle_client_hello(b"control-2", room_id, hello_data)
    srv._sync_objects_to_new_client.assert_not_called()


def test_object_pose_attributed_by_payload_device_id() -> None:
    """A stealth owner's object pose is applied even without a transform_identity.

    Stealth clients register only on the control lane (hello) and never send
    MSG_CLIENT_POSE, so they never bind a transform_identity. Object poses must
    be attributed via the deviceId carried in the MSG_OBJECT_POSE payload.
    """
    srv = NetSyncServer(enable_server_discovery=False)
    room_id = "obj-room"
    device_id = "stealth-owner"

    # Stealth hello on the control lane only — no transform pose is ever sent.
    hello = binary_serializer.serialize_client_hello(device_id, is_stealth=True)
    _, hello_data, _ = binary_serializer.deserialize(hello)
    assert hello_data is not None
    srv._handle_client_hello(b"control-1", room_id, hello_data)
    client_no = srv._get_client_no_for_device_id(room_id, device_id)
    assert client_no != 0
    with srv._rooms_lock:
        assert srv.rooms[room_id][device_id]["transform_identity"] is None

    # The stealth client owns and moves an object -> MSG_OBJECT_POSE on transform lane.
    object_id = 0xABCDEF01
    pose = binary_serializer.serialize_object_pose(
        {
            "deviceId": device_id,
            "objectId": object_id,
            "poseSeq": 3,
            "posX": 1.0,
            "posY": 2.0,
            "posZ": 3.0,
            "rotX": 0.0,
            "rotY": 0.0,
            "rotZ": 0.0,
            "rotW": 1.0,
        }
    )
    msg_type, pose_data, _ = binary_serializer.deserialize(pose)
    assert msg_type == binary_serializer.MSG_OBJECT_POSE
    assert pose_data is not None
    assert pose_data["deviceId"] == device_id

    # The transform-lane socket identity (b"transform-x") was never registered for
    # this device, yet the pose must still be attributed to the stealth owner.
    srv._handle_transform_message(b"transform-x", room_id, msg_type, pose_data, pose)

    with srv._rooms_lock:
        obj_state = srv.room_objects[room_id][object_id]
    assert obj_state["owner_client_no"] == client_no
    assert obj_state["pose_seq"] == 3


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
