"""Tests for object synchronization serialization and client API."""

from __future__ import annotations

import math

import pytest

from styly_netsync import binary_serializer
from styly_netsync.binary_serializer import (
    MSG_CLIENT_OBJECTS,
    MSG_OBJECT_OWNER,
    MSG_ROOM_OBJECTS,
    serialize_client_objects,
    serialize_object_owner,
    serialize_room_objects,
)


def _quat_angle_error_deg(
    qa: tuple[float, float, float, float], qb: tuple[float, float, float, float]
) -> float:
    """Get angular distance between quaternions in degrees."""
    dot = qa[0] * qb[0] + qa[1] * qb[1] + qa[2] * qb[2] + qa[3] * qb[3]
    dot = abs(dot)
    dot = min(1.0, max(-1.0, dot))
    return math.degrees(2.0 * math.acos(dot))


class TestClientObjectsRoundTrip:
    """Tests for MSG_CLIENT_OBJECTS serialization/deserialization round-trip."""

    def test_single_object_round_trip(self) -> None:
        """Single object round-trips with correct header and body."""
        objects = [(1, 1.5, 2.0, -3.0, 0.0, 0.0, 0.0, 1.0)]
        raw = serialize_client_objects(42, objects)
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_CLIENT_OBJECTS
        assert data is not None
        header = data["header"]
        assert header["poseSeq"] == 42
        obj_list = data["objects"]
        assert len(obj_list) == 1
        assert obj_list[0][0] == 1  # objectId
        assert len(obj_list[0][1]) == 13  # body_bytes length

    def test_multiple_objects_round_trip(self) -> None:
        """Multiple objects round-trip correctly."""
        objects = [
            (1, 1.0, 2.0, 3.0, 0.0, 0.0, 0.0, 1.0),
            (2, -1.0, -2.0, -3.0, 0.0, 1.0, 0.0, 0.0),
            (100, 10.5, 20.5, 30.5, 0.0, 0.0, 0.7071, 0.7071),
        ]
        raw = serialize_client_objects(999, objects)
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_CLIENT_OBJECTS
        assert data is not None
        assert data["header"]["poseSeq"] == 999
        assert len(data["objects"]) == 3
        assert data["objects"][0][0] == 1
        assert data["objects"][1][0] == 2
        assert data["objects"][2][0] == 100

    def test_empty_object_list(self) -> None:
        """Empty object list round-trips correctly."""
        raw = serialize_client_objects(0, [])
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_CLIENT_OBJECTS
        assert data is not None
        assert data["header"]["poseSeq"] == 0
        assert len(data["objects"]) == 0

    def test_max_object_count_255(self) -> None:
        """255 objects (max byte count) round-trips correctly."""
        objects = [
            (i, float(i), float(i), float(i), 0.0, 0.0, 0.0, 1.0) for i in range(255)
        ]
        raw = serialize_client_objects(1000, objects)
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_CLIENT_OBJECTS
        assert data is not None
        assert len(data["objects"]) == 255
        # Verify first and last object IDs
        assert data["objects"][0][0] == 0
        assert data["objects"][254][0] == 254

    def test_pose_seq_wraps_at_16bit(self) -> None:
        """Pose sequence wraps at 16-bit boundary."""
        raw = serialize_client_objects(0xFFFF, [(1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0)])
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert data is not None
        assert data["header"]["poseSeq"] == 0xFFFF

    def test_message_type_byte(self) -> None:
        """First byte of serialized data is MSG_CLIENT_OBJECTS."""
        raw = serialize_client_objects(1, [(1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0)])
        assert raw[0] == MSG_CLIENT_OBJECTS
        assert raw[0] == 13


class TestRoomObjectsRoundTrip:
    """Tests for MSG_ROOM_OBJECTS serialization/deserialization round-trip."""

    def _make_body_bytes(
        self,
        px: float,
        py: float,
        pz: float,
        rx: float,
        ry: float,
        rz: float,
        rw: float,
    ) -> bytes:
        """Create 13-byte body from object transforms via client objects serialization."""
        # Serialize as client objects and extract the body bytes
        raw = serialize_client_objects(0, [(0, px, py, pz, rx, ry, rz, rw)])
        _, data, _ = binary_serializer.deserialize(raw)
        assert data is not None
        return data["objects"][0][1]

    def test_single_object_round_trip(self) -> None:
        """Single room object round-trips with position and rotation decoded."""
        body = self._make_body_bytes(1.5, 2.0, -3.0, 0.0, 0.0, 0.0, 1.0)
        raw = serialize_room_objects(100.0, [(10, 5, body)])
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_ROOM_OBJECTS
        assert data is not None
        assert abs(data["broadcastTime"] - 100.0) < 1e-6
        assert len(data["objects"]) == 1

        obj = data["objects"][0]
        assert obj["objectId"] == 10
        assert obj["ownerClientNo"] == 5
        assert abs(obj["position"]["x"] - 1.5) <= 0.01
        assert abs(obj["position"]["y"] - 2.0) <= 0.01
        assert abs(obj["position"]["z"] + 3.0) <= 0.01

    def test_multiple_objects_round_trip(self) -> None:
        """Multiple room objects round-trip correctly."""
        body1 = self._make_body_bytes(1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0)
        body2 = self._make_body_bytes(-5.0, 10.0, 3.0, 0.0, 0.0, 0.0, 1.0)
        raw = serialize_room_objects(200.0, [(1, 10, body1), (2, 20, body2)])
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_ROOM_OBJECTS
        assert data is not None
        assert len(data["objects"]) == 2
        assert data["objects"][0]["objectId"] == 1
        assert data["objects"][0]["ownerClientNo"] == 10
        assert data["objects"][1]["objectId"] == 2
        assert data["objects"][1]["ownerClientNo"] == 20

    def test_empty_object_list(self) -> None:
        """Empty room objects round-trips correctly."""
        raw = serialize_room_objects(0.0, [])
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_ROOM_OBJECTS
        assert data is not None
        assert len(data["objects"]) == 0

    def test_rotation_fidelity(self) -> None:
        """Rotation through room objects stays within 1 degree."""
        # 45-degree rotation around Y
        rw = math.cos(math.pi / 8)
        ry = math.sin(math.pi / 8)
        body = self._make_body_bytes(0.0, 0.0, 0.0, 0.0, ry, 0.0, rw)
        raw = serialize_room_objects(0.0, [(1, 1, body)])
        _, data, _ = binary_serializer.deserialize(raw)

        assert data is not None
        rot = data["objects"][0]["rotation"]
        err = _quat_angle_error_deg(
            (0.0, ry, 0.0, rw), (rot["x"], rot["y"], rot["z"], rot["w"])
        )
        assert err <= 1.0

    def test_message_type_byte(self) -> None:
        """First byte of serialized data is MSG_ROOM_OBJECTS."""
        raw = serialize_room_objects(0.0, [])
        assert raw[0] == MSG_ROOM_OBJECTS
        assert raw[0] == 14


class TestObjectOwnerRoundTrip:
    """Tests for MSG_OBJECT_OWNER serialization/deserialization round-trip."""

    def test_basic_round_trip(self) -> None:
        """Basic ownership message round-trips correctly."""
        raw = serialize_object_owner(10, 5, 1)
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert msg_type == MSG_OBJECT_OWNER
        assert data is not None
        assert data["objectId"] == 10
        assert data["newOwnerClientNo"] == 5
        assert data["seq"] == 1

    def test_release_ownership(self) -> None:
        """Releasing ownership (owner=0) round-trips correctly."""
        raw = serialize_object_owner(42, 0, 100)
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert data is not None
        assert data["objectId"] == 42
        assert data["newOwnerClientNo"] == 0
        assert data["seq"] == 100

    def test_max_values(self) -> None:
        """Maximum uint16 values round-trip correctly."""
        raw = serialize_object_owner(0xFFFF, 0xFFFF, 0xFFFF)
        msg_type, data, _ = binary_serializer.deserialize(raw)

        assert data is not None
        assert data["objectId"] == 0xFFFF
        assert data["newOwnerClientNo"] == 0xFFFF
        assert data["seq"] == 0xFFFF

    def test_zero_values(self) -> None:
        """All-zero values round-trip correctly."""
        raw = serialize_object_owner(0, 0, 0)
        _, data, _ = binary_serializer.deserialize(raw)

        assert data is not None
        assert data["objectId"] == 0
        assert data["newOwnerClientNo"] == 0
        assert data["seq"] == 0

    def test_message_type_byte(self) -> None:
        """First byte of serialized data is MSG_OBJECT_OWNER."""
        raw = serialize_object_owner(1, 1, 1)
        assert raw[0] == MSG_OBJECT_OWNER
        assert raw[0] == 19

    def test_serialized_length(self) -> None:
        """MSG_OBJECT_OWNER should be exactly 7 bytes."""
        raw = serialize_object_owner(1, 2, 3)
        assert len(raw) == 7


class TestObjectPositionBoundaryValues:
    """Tests for boundary position values in object transforms."""

    def test_large_positive_position(self) -> None:
        """Large positive position values survive round-trip via room objects."""
        # int24 max * 0.01 = 83886.07
        objects = [(1, 5000.0, 5000.0, 5000.0, 0.0, 0.0, 0.0, 1.0)]
        raw = serialize_client_objects(1, objects)
        _, client_data, _ = binary_serializer.deserialize(raw)
        assert client_data is not None

        body = client_data["objects"][0][1]
        room_raw = serialize_room_objects(0.0, [(1, 1, body)])
        _, room_data, _ = binary_serializer.deserialize(room_raw)
        assert room_data is not None

        pos = room_data["objects"][0]["position"]
        assert abs(pos["x"] - 5000.0) <= 0.01
        assert abs(pos["y"] - 5000.0) <= 0.01
        assert abs(pos["z"] - 5000.0) <= 0.01

    def test_large_negative_position(self) -> None:
        """Large negative position values survive round-trip."""
        objects = [(1, -5000.0, -5000.0, -5000.0, 0.0, 0.0, 0.0, 1.0)]
        raw = serialize_client_objects(1, objects)
        _, client_data, _ = binary_serializer.deserialize(raw)
        assert client_data is not None

        body = client_data["objects"][0][1]
        room_raw = serialize_room_objects(0.0, [(1, 1, body)])
        _, room_data, _ = binary_serializer.deserialize(room_raw)
        assert room_data is not None

        pos = room_data["objects"][0]["position"]
        assert abs(pos["x"] + 5000.0) <= 0.01
        assert abs(pos["y"] + 5000.0) <= 0.01
        assert abs(pos["z"] + 5000.0) <= 0.01

    def test_zero_position(self) -> None:
        """Zero position round-trips exactly."""
        objects = [(1, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0)]
        raw = serialize_client_objects(1, objects)
        _, client_data, _ = binary_serializer.deserialize(raw)
        assert client_data is not None

        body = client_data["objects"][0][1]
        room_raw = serialize_room_objects(0.0, [(1, 1, body)])
        _, room_data, _ = binary_serializer.deserialize(room_raw)
        assert room_data is not None

        pos = room_data["objects"][0]["position"]
        assert pos["x"] == 0.0
        assert pos["y"] == 0.0
        assert pos["z"] == 0.0

    def test_position_quantization_precision(self) -> None:
        """Position values are quantized to ABS_POS_SCALE (0.01m) precision."""
        # Value that is exactly representable
        objects = [(1, 1.23, -4.56, 7.89, 0.0, 0.0, 0.0, 1.0)]
        raw = serialize_client_objects(1, objects)
        _, client_data, _ = binary_serializer.deserialize(raw)
        assert client_data is not None

        body = client_data["objects"][0][1]
        room_raw = serialize_room_objects(0.0, [(1, 1, body)])
        _, room_data, _ = binary_serializer.deserialize(room_raw)
        assert room_data is not None

        pos = room_data["objects"][0]["position"]
        assert abs(pos["x"] - 1.23) <= 0.01
        assert abs(pos["y"] + 4.56) <= 0.01
        assert abs(pos["z"] - 7.89) <= 0.01

    @pytest.mark.parametrize(
        "quat",
        [
            (0.0, 0.0, 0.0, 1.0),  # identity
            (1.0, 0.0, 0.0, 0.0),  # 180 deg around X
            (0.0, 1.0, 0.0, 0.0),  # 180 deg around Y
            (0.0, 0.0, 1.0, 0.0),  # 180 deg around Z
            (0.5, 0.5, 0.5, 0.5),  # 120 deg around (1,1,1)
        ],
    )
    def test_various_rotations(self, quat: tuple[float, float, float, float]) -> None:
        """Various canonical rotations survive round-trip within 1 degree."""
        rx, ry, rz, rw = quat
        objects = [(1, 0.0, 0.0, 0.0, rx, ry, rz, rw)]
        raw = serialize_client_objects(1, objects)
        _, client_data, _ = binary_serializer.deserialize(raw)
        assert client_data is not None

        body = client_data["objects"][0][1]
        room_raw = serialize_room_objects(0.0, [(1, 1, body)])
        _, room_data, _ = binary_serializer.deserialize(room_raw)
        assert room_data is not None

        rot = room_data["objects"][0]["rotation"]
        err = _quat_angle_error_deg(quat, (rot["x"], rot["y"], rot["z"], rot["w"]))
        assert err <= 1.0
