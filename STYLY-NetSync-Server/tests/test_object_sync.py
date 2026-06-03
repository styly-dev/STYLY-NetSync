"""Tests for NetSyncObject feature: object sync protocol and server logic."""

import struct
import time
from unittest.mock import MagicMock

import pytest

from styly_netsync import binary_serializer
from styly_netsync.binary_serializer import (
    MSG_OBJECT_OWNERSHIP_CHANGED,
    MSG_OBJECT_OWNERSHIP_REJECTED,
    MSG_OBJECT_OWNERSHIP_REQUEST,
    MSG_OBJECT_POSE,
    MSG_ROOM_OBJECTS,
    deserialize,
    serialize_object_ownership_changed,
    serialize_object_ownership_rejected,
    serialize_object_pose,
    serialize_room_objects,
)

# Arbitrary uint32 ObjectId values used as test fixtures. The client side derives
# these from GlobalObjectId in the editor; for these protocol-level tests any
# stable non-zero uint32 works.
_OBJ_ID_1 = 0xDEADBEEF
_OBJ_ID_A = 0xA0A0A0A0
_OBJ_ID_B = 0xB0B0B0B0


class TestObjectPoseSerialization:
    """Test MSG_OBJECT_POSE (13) serialize/deserialize round-trip."""

    def test_basic_round_trip(self) -> None:
        data = {
            "objectId": _OBJ_ID_1,
            "poseSeq": 42,
            "posX": 1.5,
            "posY": 2.0,
            "posZ": -3.5,
            "rotX": 0.0,
            "rotY": 0.0,
            "rotZ": 0.0,
            "rotW": 1.0,
        }
        raw = serialize_object_pose(data)
        assert raw[0] == MSG_OBJECT_POSE

        msg_type, result, _ = deserialize(raw)
        assert msg_type == MSG_OBJECT_POSE
        assert result is not None
        assert result["objectId"] == _OBJ_ID_1
        assert result["poseSeq"] == 42
        assert abs(result["posX"] - 1.5) < 0.02
        assert abs(result["posY"] - 2.0) < 0.02
        assert abs(result["posZ"] - (-3.5)) < 0.02

    def test_body_bytes_extracted(self) -> None:
        data = {
            "objectId": _OBJ_ID_1,
            "poseSeq": 1,
            "posX": 0.0,
            "posY": 0.0,
            "posZ": 0.0,
            "rotX": 0.0,
            "rotY": 0.0,
            "rotZ": 0.0,
            "rotW": 1.0,
        }
        raw = serialize_object_pose(data)
        _, result, _ = deserialize(raw)
        assert result is not None
        # body_bytes = pos(9) + rot(4) = 13 bytes (poseSeq is separate)
        assert "bodyBytes" in result
        assert len(result["bodyBytes"]) == 13

    def test_object_id_upper_bound(self) -> None:
        """uint32 accepts the full 32-bit range including 0xFFFFFFFF."""
        data = {
            "objectId": 0xFFFFFFFF,
            "poseSeq": 0,
            "posX": 0.0,
            "posY": 0.0,
            "posZ": 0.0,
            "rotX": 0.0,
            "rotY": 0.0,
            "rotZ": 0.0,
            "rotW": 1.0,
        }
        raw = serialize_object_pose(data)
        _, result, _ = deserialize(raw)
        assert result is not None
        assert result["objectId"] == 0xFFFFFFFF


class TestRoomObjectsSerialization:
    """Test MSG_ROOM_OBJECTS (14) serialize/deserialize round-trip."""

    def test_empty_room(self) -> None:
        raw = serialize_room_objects("room1", 123.456, [])
        assert raw[0] == MSG_ROOM_OBJECTS

        msg_type, result, _ = deserialize(raw)
        assert msg_type == MSG_ROOM_OBJECTS
        assert result is not None
        assert abs(result["broadcastTime"] - 123.456) < 1e-6
        assert len(result["objects"]) == 0

    def test_multiple_objects(self) -> None:
        objects = [
            {
                "objectId": _OBJ_ID_A,
                "ownerClientNo": 1,
                "poseSeq": 10,
                "poseTime": 100.0,
                "posX": 1.0,
                "posY": 2.0,
                "posZ": 3.0,
                "rotX": 0.0,
                "rotY": 0.0,
                "rotZ": 0.0,
                "rotW": 1.0,
            },
            {
                "objectId": _OBJ_ID_B,
                "ownerClientNo": 0,
                "poseSeq": 0,
                "poseTime": 0.0,
                "posX": 0.0,
                "posY": 0.0,
                "posZ": 0.0,
                "rotX": 0.0,
                "rotY": 0.0,
                "rotZ": 0.0,
                "rotW": 1.0,
            },
        ]
        raw = serialize_room_objects("room1", 200.0, objects)
        _, result, _ = deserialize(raw)
        assert result is not None
        assert len(result["objects"]) == 2
        assert result["objects"][0]["objectId"] == _OBJ_ID_A
        assert result["objects"][0]["ownerClientNo"] == 1
        assert result["objects"][0]["poseSeq"] == 10
        assert result["objects"][1]["objectId"] == _OBJ_ID_B
        assert result["objects"][1]["ownerClientNo"] == 0

    def test_with_body_bytes(self) -> None:
        """Test that bodyBytes field is used directly when present."""
        # Build body_bytes manually: 3 int24 + 1 uint32 = 13 bytes
        body = bytearray()
        # posX=0, posY=0, posZ=0
        body.extend(b"\x00\x00\x00")  # int24 = 0
        body.extend(b"\x00\x00\x00")
        body.extend(b"\x00\x00\x00")
        # identity quaternion packed
        packed = binary_serializer._compress_quaternion_smallest_three(
            0.0, 0.0, 0.0, 1.0
        )
        body.extend(struct.pack("<I", packed))

        objects = [
            {
                "objectId": _OBJ_ID_1,
                "ownerClientNo": 2,
                "poseSeq": 5,
                "poseTime": 50.0,
                "bodyBytes": bytes(body),
            },
        ]
        raw = serialize_room_objects("room1", 300.0, objects)
        _, result, _ = deserialize(raw)
        assert result is not None
        assert len(result["objects"]) == 1
        assert result["objects"][0]["objectId"] == _OBJ_ID_1


class TestOwnershipRequestSerialization:
    """Test MSG_OBJECT_OWNERSHIP_REQUEST (15) deserialize."""

    def test_release_ownership(self) -> None:
        buf = bytearray()
        buf.append(MSG_OBJECT_OWNERSHIP_REQUEST)
        buf.append(1)  # ReleaseOwnership
        buf.extend(struct.pack("<I", _OBJ_ID_1))

        msg_type, result, _ = deserialize(bytes(buf))
        assert msg_type == MSG_OBJECT_OWNERSHIP_REQUEST
        assert result is not None
        assert result["operationType"] == 1
        assert result["objectId"] == _OBJ_ID_1

    def test_request_ownership(self) -> None:
        buf = bytearray()
        buf.append(MSG_OBJECT_OWNERSHIP_REQUEST)
        buf.append(2)  # RequestOwnership
        buf.extend(struct.pack("<I", _OBJ_ID_1))

        _, result, _ = deserialize(bytes(buf))
        assert result is not None
        assert result["operationType"] == 2


class TestOwnershipChangedSerialization:
    """Test MSG_OBJECT_OWNERSHIP_CHANGED (16) round-trip."""

    def test_round_trip(self) -> None:
        raw = serialize_object_ownership_changed(_OBJ_ID_1, 5, 3)
        assert raw[0] == MSG_OBJECT_OWNERSHIP_CHANGED

        msg_type, result, _ = deserialize(raw)
        assert msg_type == MSG_OBJECT_OWNERSHIP_CHANGED
        assert result is not None
        assert result["objectId"] == _OBJ_ID_1
        assert result["newOwnerClientNo"] == 5
        assert result["previousOwnerClientNo"] == 3


class TestOwnershipRejectedSerialization:
    """Test MSG_OBJECT_OWNERSHIP_REJECTED (17) round-trip."""

    def test_round_trip(self) -> None:
        raw = serialize_object_ownership_rejected(_OBJ_ID_1, 2, 0)
        assert raw[0] == MSG_OBJECT_OWNERSHIP_REJECTED

        msg_type, result, _ = deserialize(raw)
        assert msg_type == MSG_OBJECT_OWNERSHIP_REJECTED
        assert result is not None
        assert result["objectId"] == _OBJ_ID_1
        assert result["currentOwnerClientNo"] == 2
        assert result["reasonCode"] == 0

    def test_not_owner_reason(self) -> None:
        raw = serialize_object_ownership_rejected(_OBJ_ID_1, 1, 1)
        _, result, _ = deserialize(raw)
        assert result is not None
        assert result["reasonCode"] == 1


class TestServerObjectOwnership:
    """Test server-side object ownership logic."""

    @pytest.fixture()
    def server(self):  # type: ignore[no-untyped-def]
        """Create a minimal NetSyncServer for testing."""
        from styly_netsync.server import NetSyncServer

        srv = NetSyncServer.__new__(NetSyncServer)
        # Minimal initialization for object sync testing
        import threading

        srv._rooms_lock = threading.RLock()
        srv._stats_lock = threading.Lock()
        srv.rooms = {}
        srv.room_objects = {}
        srv.room_object_dirty = {}
        srv.room_dirty_flags = {}
        srv.room_last_broadcast = {}
        srv.room_client_no_counters = {}
        srv.room_device_id_to_client_no = {}
        srv.room_client_no_to_device_id = {}
        srv.device_id_last_seen = {}
        srv.global_variables = {}
        srv.client_variables = {}
        srv.nv_write_seq = {}
        srv.pending_global_nv = {}
        srv.pending_client_nv = {}
        srv.room_last_nv_flush = {}
        srv.nv_monitor_window = {}
        srv._room_last_object_broadcast = {}
        srv.room_empty_since = {}
        srv.room_id_mapping_dirty = {}
        srv.room_last_id_mapping_broadcast = {}
        srv.client_transform_body_cache = {}
        srv._router_queue_ctrl = MagicMock()
        srv._router_queue_ctrl.put_nowait = MagicMock()
        srv._router_queue_ctrl.get_nowait = MagicMock(side_effect=Exception("empty"))

        # Stats
        srv.message_count = 0
        srv.broadcast_count = 0
        srv.skipped_broadcasts = 0
        srv.ctrl_unicast_sent = 0
        srv.ctrl_unicast_wouldblock = 0
        srv.ctrl_unicast_dropped = 0
        srv.ctrl_unicast_unreachable = 0
        srv.wrong_lane_dropped = 0

        # Initialize a room
        srv._initialize_room("test_room")
        # Add a client
        srv.rooms["test_room"]["device_a"] = {
            "control_identity": b"ident_a",
            "transform_identity": b"transform_a",
            "last_update": time.monotonic(),
            "transform_data": {},
            "client_no": 1,
            "is_stealth": False,
        }
        srv.room_device_id_to_client_no["test_room"]["device_a"] = 1
        srv.room_client_no_to_device_id["test_room"][1] = "device_a"

        srv.rooms["test_room"]["device_b"] = {
            "control_identity": b"ident_b",
            "transform_identity": b"transform_b",
            "last_update": time.monotonic(),
            "transform_data": {},
            "client_no": 2,
            "is_stealth": False,
        }
        srv.room_device_id_to_client_no["test_room"]["device_b"] = 2
        srv.room_client_no_to_device_id["test_room"][2] = "device_b"

        return srv

    def test_request_ownership_empty_object(self, server) -> None:  # type: ignore[no-untyped-def]
        """RequestOwnership on an unowned object should take ownership."""
        data = {"operationType": 2, "objectId": _OBJ_ID_1}
        server._handle_object_ownership_request(b"ident_a", "test_room", 1, data)

        assert server.room_objects["test_room"][_OBJ_ID_1]["owner_client_no"] == 1

    def test_request_ownership_takes_over_existing_owner(self, server) -> None:  # type: ignore[no-untyped-def]
        """RequestOwnership should take ownership regardless of prior owner."""
        # Client 1 takes ownership
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": _OBJ_ID_1}
        )
        # Client 2 takes over
        server._handle_object_ownership_request(
            b"ident_b", "test_room", 2, {"operationType": 2, "objectId": _OBJ_ID_1}
        )

        assert server.room_objects["test_room"][_OBJ_ID_1]["owner_client_no"] == 2

    def test_release_ownership(self, server) -> None:  # type: ignore[no-untyped-def]
        """Releasing an owned object should set owner to 0."""
        # Take ownership
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": _OBJ_ID_1}
        )
        # Release
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 1, "objectId": _OBJ_ID_1}
        )

        assert server.room_objects["test_room"][_OBJ_ID_1]["owner_client_no"] == 0

    def test_release_by_non_owner_rejected(self, server) -> None:  # type: ignore[no-untyped-def]
        """Releasing by a non-owner should be rejected."""
        # Client 1 takes ownership
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": _OBJ_ID_1}
        )
        # Try to release by client 2
        server._handle_object_ownership_request(
            b"ident_b", "test_room", 2, {"operationType": 1, "objectId": _OBJ_ID_1}
        )

        # Still owned by client 1
        assert server.room_objects["test_room"][_OBJ_ID_1]["owner_client_no"] == 1

    def test_object_pose_from_owner(self, server) -> None:  # type: ignore[no-untyped-def]
        """Object pose from owner should update state."""
        # Take ownership
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": _OBJ_ID_1}
        )
        # Send pose
        data = {
            "objectId": _OBJ_ID_1,
            "poseSeq": 5,
            "bodyBytes": b"\x00" * 13,
        }
        server._handle_object_pose("test_room", 1, data)

        assert server.room_objects["test_room"][_OBJ_ID_1]["pose_seq"] == 5
        assert server.room_object_dirty["test_room"] is True

    def test_object_pose_from_non_owner_ignored(self, server) -> None:  # type: ignore[no-untyped-def]
        """Object pose from non-owner should be ignored."""
        # Client 1 takes ownership
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": _OBJ_ID_1}
        )
        # Reset dirty flag
        server.room_object_dirty["test_room"] = False

        # Try to send pose from client 2
        data = {
            "objectId": _OBJ_ID_1,
            "poseSeq": 5,
            "bodyBytes": b"\x00" * 13,
        }
        server._handle_object_pose("test_room", 2, data)

        # Should not have updated
        assert server.room_objects["test_room"][_OBJ_ID_1]["pose_seq"] == 0
        assert server.room_object_dirty["test_room"] is False

    def test_dirty_flag_not_set_on_request_without_pose(self, server) -> None:  # type: ignore[no-untyped-def]
        """Ownership-only RequestOwnership must NOT mark the room dirty when no pose exists.

        Rationale: ownership changes are delivered via ROUTER ownership_changed.
        Marking the room dirty with empty body_bytes would cause the next PUB
        snapshot to broadcast identity-at-origin and snap non-owners.
        """
        server.room_object_dirty["test_room"] = False
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": _OBJ_ID_1}
        )
        assert server.room_object_dirty["test_room"] is False

    def test_dirty_flag_set_on_request_with_existing_pose(self, server) -> None:  # type: ignore[no-untyped-def]
        """RequestOwnership on an object that already has a real pose should mark dirty
        so the new owner is reflected in the next PUB snapshot."""
        # Seed with an existing pose.
        server.room_objects["test_room"][_OBJ_ID_1] = {
            "owner_client_no": 0,
            "pose_time": 1.0,
            "pose_seq": 7,
            "body_bytes": b"\x00" * 13,
        }
        server.room_object_dirty["test_room"] = False
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": _OBJ_ID_1}
        )
        assert server.room_object_dirty["test_room"] is True

    def test_zero_object_id_rejected_for_ownership(self, server) -> None:  # type: ignore[no-untyped-def]
        """objectId == 0 is the "unassigned" sentinel and must never create state."""
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 2, "objectId": 0}
        )
        assert server.room_objects["test_room"] == {}

    def test_zero_object_id_rejected_for_pose(self, server) -> None:  # type: ignore[no-untyped-def]
        """Pose updates with objectId == 0 must be ignored (no auto-register)."""
        server._handle_object_pose(
            "test_room",
            1,
            {"objectId": 0, "poseSeq": 1, "bodyBytes": b"\x00" * 13},
        )
        assert server.room_objects["test_room"] == {}
        assert server.room_object_dirty["test_room"] is False
