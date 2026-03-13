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


class TestObjectPoseSerialization:
    """Test MSG_OBJECT_POSE (13) serialize/deserialize round-trip."""

    def test_basic_round_trip(self) -> None:
        data = {
            "objectId": "test_obj_1",
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
        assert result["objectId"] == "test_obj_1"
        assert result["poseSeq"] == 42
        assert abs(result["posX"] - 1.5) < 0.02
        assert abs(result["posY"] - 2.0) < 0.02
        assert abs(result["posZ"] - (-3.5)) < 0.02

    def test_body_bytes_extracted(self) -> None:
        data = {
            "objectId": "obj",
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

    def test_long_object_id_clamped(self) -> None:
        data = {
            "objectId": "a" * 100,
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
        assert len(result["objectId"]) == 64


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
                "objectId": "obj_A",
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
                "objectId": "obj_B",
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
        assert result["objects"][0]["objectId"] == "obj_A"
        assert result["objects"][0]["ownerClientNo"] == 1
        assert result["objects"][0]["poseSeq"] == 10
        assert result["objects"][1]["objectId"] == "obj_B"
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
                "objectId": "obj_cached",
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
        assert result["objects"][0]["objectId"] == "obj_cached"


class TestOwnershipRequestSerialization:
    """Test MSG_OBJECT_OWNERSHIP_REQUEST (15) deserialize."""

    def test_claim(self) -> None:
        # Build request manually
        buf = bytearray()
        buf.append(MSG_OBJECT_OWNERSHIP_REQUEST)
        buf.append(0)  # operationType = claim
        obj_id = b"test_obj"
        buf.append(len(obj_id))
        buf.extend(obj_id)

        msg_type, result, _ = deserialize(bytes(buf))
        assert msg_type == MSG_OBJECT_OWNERSHIP_REQUEST
        assert result is not None
        assert result["operationType"] == 0
        assert result["objectId"] == "test_obj"

    def test_release(self) -> None:
        buf = bytearray()
        buf.append(MSG_OBJECT_OWNERSHIP_REQUEST)
        buf.append(1)  # release
        obj_id = b"obj"
        buf.append(len(obj_id))
        buf.extend(obj_id)

        _, result, _ = deserialize(bytes(buf))
        assert result is not None
        assert result["operationType"] == 1

    def test_force_claim(self) -> None:
        buf = bytearray()
        buf.append(MSG_OBJECT_OWNERSHIP_REQUEST)
        buf.append(2)  # force_claim
        obj_id = b"obj"
        buf.append(len(obj_id))
        buf.extend(obj_id)

        _, result, _ = deserialize(bytes(buf))
        assert result is not None
        assert result["operationType"] == 2


class TestOwnershipChangedSerialization:
    """Test MSG_OBJECT_OWNERSHIP_CHANGED (16) round-trip."""

    def test_round_trip(self) -> None:
        raw = serialize_object_ownership_changed("my_obj", 5, 3)
        assert raw[0] == MSG_OBJECT_OWNERSHIP_CHANGED

        msg_type, result, _ = deserialize(raw)
        assert msg_type == MSG_OBJECT_OWNERSHIP_CHANGED
        assert result is not None
        assert result["objectId"] == "my_obj"
        assert result["newOwnerClientNo"] == 5
        assert result["previousOwnerClientNo"] == 3


class TestOwnershipRejectedSerialization:
    """Test MSG_OBJECT_OWNERSHIP_REJECTED (17) round-trip."""

    def test_round_trip(self) -> None:
        raw = serialize_object_ownership_rejected("my_obj", 2, 0)
        assert raw[0] == MSG_OBJECT_OWNERSHIP_REJECTED

        msg_type, result, _ = deserialize(raw)
        assert msg_type == MSG_OBJECT_OWNERSHIP_REJECTED
        assert result is not None
        assert result["objectId"] == "my_obj"
        assert result["currentOwnerClientNo"] == 2
        assert result["reasonCode"] == 0

    def test_not_owner_reason(self) -> None:
        raw = serialize_object_ownership_rejected("obj", 1, 1)
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

        # Initialize a room
        srv._initialize_room("test_room")
        # Add a client
        srv.rooms["test_room"]["device_a"] = {
            "identity": b"ident_a",
            "last_update": time.monotonic(),
            "transform_data": {},
            "client_no": 1,
            "is_stealth": False,
        }
        srv.room_device_id_to_client_no["test_room"]["device_a"] = 1
        srv.room_client_no_to_device_id["test_room"][1] = "device_a"

        srv.rooms["test_room"]["device_b"] = {
            "identity": b"ident_b",
            "last_update": time.monotonic(),
            "transform_data": {},
            "client_no": 2,
            "is_stealth": False,
        }
        srv.room_device_id_to_client_no["test_room"]["device_b"] = 2
        srv.room_client_no_to_device_id["test_room"][2] = "device_b"

        return srv

    def test_claim_empty_object(self, server) -> None:  # type: ignore[no-untyped-def]
        """Claiming an unowned object should succeed."""
        data = {"operationType": 0, "objectId": "obj1"}
        server._handle_object_ownership_request(b"ident_a", "test_room", 1, data)

        assert server.room_objects["test_room"]["obj1"]["owner_client_no"] == 1

    def test_claim_owned_object_rejected(self, server) -> None:  # type: ignore[no-untyped-def]
        """Claiming an object already owned by another should be rejected."""
        # First claim by client 1
        data = {"operationType": 0, "objectId": "obj1"}
        server._handle_object_ownership_request(b"ident_a", "test_room", 1, data)

        # Then try to claim by client 2
        server._handle_object_ownership_request(b"ident_b", "test_room", 2, data)

        # Should still be owned by client 1
        assert server.room_objects["test_room"]["obj1"]["owner_client_no"] == 1

    def test_release(self, server) -> None:  # type: ignore[no-untyped-def]
        """Releasing an owned object should set owner to 0."""
        # Claim
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 0, "objectId": "obj1"}
        )
        # Release
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 1, "objectId": "obj1"}
        )

        assert server.room_objects["test_room"]["obj1"]["owner_client_no"] == 0

    def test_release_by_non_owner_rejected(self, server) -> None:  # type: ignore[no-untyped-def]
        """Releasing by a non-owner should be rejected."""
        # Claim by client 1
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 0, "objectId": "obj1"}
        )
        # Try to release by client 2
        server._handle_object_ownership_request(
            b"ident_b", "test_room", 2, {"operationType": 1, "objectId": "obj1"}
        )

        # Still owned by client 1
        assert server.room_objects["test_room"]["obj1"]["owner_client_no"] == 1

    def test_force_claim(self, server) -> None:  # type: ignore[no-untyped-def]
        """Force claim should take ownership regardless."""
        # Claim by client 1
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 0, "objectId": "obj1"}
        )
        # Force claim by client 2
        server._handle_object_ownership_request(
            b"ident_b", "test_room", 2, {"operationType": 2, "objectId": "obj1"}
        )

        assert server.room_objects["test_room"]["obj1"]["owner_client_no"] == 2

    def test_object_pose_from_owner(self, server) -> None:  # type: ignore[no-untyped-def]
        """Object pose from owner should update state."""
        # Claim
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 0, "objectId": "obj1"}
        )
        # Send pose
        data = {
            "objectId": "obj1",
            "poseSeq": 5,
            "bodyBytes": b"\x00" * 13,
        }
        server._handle_object_pose("test_room", 1, data)

        assert server.room_objects["test_room"]["obj1"]["pose_seq"] == 5
        assert server.room_object_dirty["test_room"] is True

    def test_object_pose_from_non_owner_ignored(self, server) -> None:  # type: ignore[no-untyped-def]
        """Object pose from non-owner should be ignored."""
        # Claim by client 1
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 0, "objectId": "obj1"}
        )
        # Reset dirty flag
        server.room_object_dirty["test_room"] = False

        # Try to send pose from client 2
        data = {
            "objectId": "obj1",
            "poseSeq": 5,
            "bodyBytes": b"\x00" * 13,
        }
        server._handle_object_pose("test_room", 2, data)

        # Should not have updated
        assert server.room_objects["test_room"]["obj1"]["pose_seq"] == 0
        assert server.room_object_dirty["test_room"] is False

    def test_dirty_flag_set_on_claim(self, server) -> None:  # type: ignore[no-untyped-def]
        """Dirty flag should be set when ownership changes."""
        server.room_object_dirty["test_room"] = False
        server._handle_object_ownership_request(
            b"ident_a", "test_room", 1, {"operationType": 0, "objectId": "obj1"}
        )
        assert server.room_object_dirty["test_room"] is True
