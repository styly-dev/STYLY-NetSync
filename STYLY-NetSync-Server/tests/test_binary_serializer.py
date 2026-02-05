"""Tests for binary_serializer module."""

from __future__ import annotations

import math
import random

import pytest

from styly_netsync import binary_serializer


class TestDeviceIdMappingSerialization:
    """Tests for device ID mapping serialization/deserialization round-trip."""

    def test_roundtrip_single_mapping(self) -> None:
        """Test serialization and deserialization of a single mapping."""
        mappings = [(1, "device-uuid-123", False)]
        version = (1, 2, 3)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING
        assert result is not None
        assert len(result["mappings"]) == 1
        assert result["mappings"][0]["clientNo"] == 1
        assert result["mappings"][0]["deviceId"] == "device-uuid-123"
        assert result["mappings"][0]["isStealthMode"] is False

    def test_roundtrip_multiple_mappings(self) -> None:
        """Test serialization and deserialization of multiple mappings."""
        mappings = [
            (1, "device-a", False),
            (2, "device-b", True),
            (3, "device-c", False),
            (100, "device-with-longer-uuid-string", True),
        ]
        version = (0, 7, 5)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING
        assert result is not None
        assert len(result["mappings"]) == 4

        for i, (client_no, device_id, is_stealth) in enumerate(mappings):
            assert result["mappings"][i]["clientNo"] == client_no
            assert result["mappings"][i]["deviceId"] == device_id
            assert result["mappings"][i]["isStealthMode"] is is_stealth

    def test_roundtrip_empty_mappings(self) -> None:
        """Test serialization and deserialization of empty mappings."""
        mappings: list[tuple[int, str, bool]] = []
        version = (1, 0, 0)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING
        assert result is not None
        assert len(result["mappings"]) == 0

    def test_roundtrip_stealth_flags(self) -> None:
        """Test that stealth flags are correctly preserved."""
        mappings = [
            (1, "normal-client", False),
            (2, "stealth-client", True),
            (3, "another-normal", False),
        ]
        version = (0, 0, 0)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert result is not None
        assert result["mappings"][0]["isStealthMode"] is False
        assert result["mappings"][1]["isStealthMode"] is True
        assert result["mappings"][2]["isStealthMode"] is False

    @pytest.mark.parametrize(
        "version",
        [
            (0, 0, 0),
            (1, 0, 0),
            (0, 7, 5),
            (255, 255, 255),
            (10, 20, 30),
        ],
    )
    def test_roundtrip_various_versions(self, version: tuple[int, int, int]) -> None:
        """Test that deserialization works with various version values."""
        mappings = [(42, "test-device", False)]

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING
        assert result is not None
        assert len(result["mappings"]) == 1
        assert result["mappings"][0]["clientNo"] == 42
        assert result["mappings"][0]["deviceId"] == "test-device"

    def test_roundtrip_max_client_no(self) -> None:
        """Test serialization with maximum client number (2-byte unsigned)."""
        mappings = [(65535, "max-client-device", True)]
        version = (1, 0, 0)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert result is not None
        assert result["mappings"][0]["clientNo"] == 65535

    def test_roundtrip_unicode_device_id(self) -> None:
        """Test serialization with unicode characters in device ID."""
        mappings = [(1, "device-日本語-test", False)]
        version = (1, 0, 0)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert result is not None
        assert result["mappings"][0]["deviceId"] == "device-日本語-test"

    def test_roundtrip_uuid_format_device_id(self) -> None:
        """Test serialization with standard UUID format device ID."""
        device_uuid = "550e8400-e29b-41d4-a716-446655440000"
        mappings = [(1, device_uuid, False)]
        version = (0, 7, 5)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)
        msg_type, result, _ = binary_serializer.deserialize(serialized)

        assert result is not None
        assert result["mappings"][0]["deviceId"] == device_uuid

    def test_message_type_is_correct(self) -> None:
        """Test that serialized message has correct message type byte."""
        mappings = [(1, "device", False)]
        version = (0, 0, 0)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)

        # First byte should be MSG_DEVICE_ID_MAPPING (6)
        assert serialized[0] == binary_serializer.MSG_DEVICE_ID_MAPPING
        assert serialized[0] == 6

    def test_version_bytes_in_serialized_data(self) -> None:
        """Test that version bytes are correctly placed in serialized data."""
        mappings = [(1, "device", False)]
        version = (10, 20, 30)

        serialized = binary_serializer.serialize_device_id_mapping(mappings, version)

        # Bytes 1-3 should be version (major, minor, patch)
        assert serialized[1] == 10  # major
        assert serialized[2] == 20  # minor
        assert serialized[3] == 30  # patch


def _random_unit_quaternion(rng: random.Random) -> tuple[float, float, float, float]:
    """Generate a random unit quaternion."""
    u1 = rng.random()
    u2 = rng.random()
    u3 = rng.random()
    sqrt1_minus_u1 = math.sqrt(1.0 - u1)
    sqrt_u1 = math.sqrt(u1)
    theta1 = 2.0 * math.pi * u2
    theta2 = 2.0 * math.pi * u3
    x = sqrt1_minus_u1 * math.sin(theta1)
    y = sqrt1_minus_u1 * math.cos(theta1)
    z = sqrt_u1 * math.sin(theta2)
    w = sqrt_u1 * math.cos(theta2)
    return x, y, z, w


def _quat_angle_error_deg(
    qa: tuple[float, float, float, float], qb: tuple[float, float, float, float]
) -> float:
    """Get angular distance between quaternions in degrees."""
    dot = qa[0] * qb[0] + qa[1] * qb[1] + qa[2] * qb[2] + qa[3] * qb[3]
    dot = abs(dot)
    dot = min(1.0, max(-1.0, dot))
    return math.degrees(2.0 * math.acos(dot))


def _yaw_deg_from_quaternion(q: tuple[float, float, float, float]) -> float:
    """Extract yaw angle in degrees from quaternion."""
    x, y, z, w = q
    siny_cosp = 2.0 * (w * y + z * x)
    cosy_cosp = 1.0 - 2.0 * (y * y + z * z)
    return math.degrees(math.atan2(siny_cosp, cosy_cosp))


def _build_transform(
    pos: tuple[float, float, float], rot: tuple[float, float, float, float]
) -> dict[str, float]:
    """Build a wire transform dictionary."""
    return {
        "posX": pos[0],
        "posY": pos[1],
        "posZ": pos[2],
        "rotX": rot[0],
        "rotY": rot[1],
        "rotZ": rot[2],
        "rotW": rot[3],
    }


def _build_random_client_pose(
    rng: random.Random, virtual_count: int
) -> dict[str, object]:
    """Build a random client pose in a safe non-clamping range."""
    head_pos = (
        rng.uniform(-20.0, 20.0),
        rng.uniform(1.0, 2.2),
        rng.uniform(-20.0, 20.0),
    )
    head_rot = _random_unit_quaternion(rng)

    # Physical is yaw-only by protocol design.
    physical_pos = (
        rng.uniform(-20.0, 20.0),
        rng.uniform(0.0, 2.0),
        rng.uniform(-20.0, 20.0),
    )
    physical_yaw = rng.uniform(-180.0, 180.0)
    half = math.radians(physical_yaw) * 0.5
    physical_rot = (0.0, math.sin(half), 0.0, math.cos(half))

    right_rel = (rng.uniform(0.2, 0.7), rng.uniform(-0.4, 0.4), rng.uniform(-0.4, 0.4))
    left_rel = (rng.uniform(-0.7, -0.2), rng.uniform(-0.4, 0.4), rng.uniform(-0.4, 0.4))
    right_pos = (head_pos[0] + right_rel[0], head_pos[1] + right_rel[1], head_pos[2] + right_rel[2])
    left_pos = (head_pos[0] + left_rel[0], head_pos[1] + left_rel[1], head_pos[2] + left_rel[2])
    right_rot = _random_unit_quaternion(rng)
    left_rot = _random_unit_quaternion(rng)

    virtuals: list[dict[str, float]] = []
    for _ in range(virtual_count):
        rel = (rng.uniform(-1.5, 1.5), rng.uniform(-1.5, 1.5), rng.uniform(-1.5, 1.5))
        pos = (head_pos[0] + rel[0], head_pos[1] + rel[1], head_pos[2] + rel[2])
        virtuals.append(_build_transform(pos, _random_unit_quaternion(rng)))

    return {
        "deviceId": f"client-{rng.randint(1, 1_000_000)}",
        "poseSeq": rng.randint(0, 65535),
        "flags": 0x3E,  # Physical + Head + Right + Left + Virtuals
        "physical": _build_transform(physical_pos, physical_rot),
        "head": _build_transform(head_pos, head_rot),
        "rightHand": _build_transform(right_pos, right_rot),
        "leftHand": _build_transform(left_pos, left_rot),
        "virtuals": virtuals,
    }


class TestTransformSerializationV3:
    """Tests for protocol v3 transform compact serialization."""

    def test_protocol_version_is_v3(self) -> None:
        """Protocol version constant should be updated to v3."""
        assert binary_serializer.PROTOCOL_VERSION == 3

    def test_client_roundtrip_without_flags_infers_valid_bits(self) -> None:
        """Serializer should infer valid bits when flags are omitted."""
        payload = {
            "deviceId": "infer-flags",
            "poseSeq": 77,
            "physical": _build_transform((1.2, 0.0, -2.3), (0.0, 0.0, 0.0, 1.0)),
            "head": _build_transform((1.2, 1.6, -2.3), _random_unit_quaternion(random.Random(11))),
            "rightHand": _build_transform((1.5, 1.2, -2.3), _random_unit_quaternion(random.Random(12))),
            "leftHand": _build_transform((0.9, 1.2, -2.3), _random_unit_quaternion(random.Random(13))),
            "virtuals": [
                _build_transform((1.2, 1.0, -1.8), _random_unit_quaternion(random.Random(14)))
            ],
        }

        msg_type, decoded, _ = binary_serializer.deserialize(
            binary_serializer.serialize_client_transform(payload)
        )
        assert msg_type == binary_serializer.MSG_CLIENT_POSE_V2
        assert decoded is not None

        expected_flags = (
            binary_serializer.POSE_FLAG_PHYSICAL_VALID
            | binary_serializer.POSE_FLAG_HEAD_VALID
            | binary_serializer.POSE_FLAG_RIGHT_VALID
            | binary_serializer.POSE_FLAG_LEFT_VALID
            | binary_serializer.POSE_FLAG_VIRTUALS_VALID
        )
        assert decoded["flags"] == expected_flags
        assert abs(decoded["head"]["posX"] - 1.2) <= 0.01
        assert abs(decoded["head"]["posY"] - 1.6) <= 0.01
        assert abs(decoded["head"]["posZ"] + 2.3) <= 0.01
        assert len(decoded["virtuals"]) == 1

    def test_stealth_flag_sanitizes_transform_valid_bits(self) -> None:
        """Stealth messages should not carry transform-valid bits or virtual payloads."""
        payload = {
            "deviceId": "stealth-sanitize",
            "poseSeq": 88,
            "flags": (
                binary_serializer.POSE_FLAG_STEALTH
                | binary_serializer.POSE_FLAG_PHYSICAL_VALID
                | binary_serializer.POSE_FLAG_HEAD_VALID
                | binary_serializer.POSE_FLAG_RIGHT_VALID
                | binary_serializer.POSE_FLAG_LEFT_VALID
                | binary_serializer.POSE_FLAG_VIRTUALS_VALID
            ),
            "physical": _build_transform((9.0, 9.0, 9.0), _random_unit_quaternion(random.Random(21))),
            "head": _build_transform((8.0, 8.0, 8.0), _random_unit_quaternion(random.Random(22))),
            "rightHand": _build_transform((7.0, 7.0, 7.0), _random_unit_quaternion(random.Random(23))),
            "leftHand": _build_transform((6.0, 6.0, 6.0), _random_unit_quaternion(random.Random(24))),
            "virtuals": [
                _build_transform((5.0, 5.0, 5.0), _random_unit_quaternion(random.Random(25)))
            ],
        }

        _, decoded, _ = binary_serializer.deserialize(
            binary_serializer.serialize_client_transform(payload)
        )
        assert decoded is not None
        assert decoded["flags"] == binary_serializer.POSE_FLAG_STEALTH
        assert decoded["virtuals"] == []
        assert decoded["head"]["posX"] == 0.0
        assert decoded["head"]["posY"] == 0.0
        assert decoded["head"]["posZ"] == 0.0

    def test_quaternion_codec_random_10000(self) -> None:
        """Quaternion smallest-three codec should remain within 1 degree."""
        rng = random.Random(42)
        for _ in range(10_000):
            src = _random_unit_quaternion(rng)
            packed = binary_serializer._compress_quaternion_smallest_three(*src)
            dst = binary_serializer._decompress_quaternion_smallest_three(packed)

            assert math.isfinite(dst[0])
            assert math.isfinite(dst[1])
            assert math.isfinite(dst[2])
            assert math.isfinite(dst[3])
            assert not (
                abs(dst[0]) < 1e-8
                and abs(dst[1]) < 1e-8
                and abs(dst[2]) < 1e-8
                and abs(dst[3]) < 1e-8
            )

            err = _quat_angle_error_deg(src, dst)
            assert err <= 1.0

    def test_client_roundtrip_random_10000(self) -> None:
        """Client roundtrip should satisfy position/rotation error bounds."""
        rng = random.Random(12345)
        for _ in range(10_000):
            original = _build_random_client_pose(rng, virtual_count=rng.randint(0, 3))
            encoded = binary_serializer.serialize_client_transform(original)
            msg_type, decoded, raw = binary_serializer.deserialize(encoded)

            assert msg_type == binary_serializer.MSG_CLIENT_POSE_V2
            assert decoded is not None
            assert decoded["protocolVersion"] == 3
            assert len(raw) > 0

            o_head = original["head"]
            d_head = decoded["head"]
            assert abs(o_head["posX"] - d_head["posX"]) <= 0.01
            assert abs(o_head["posY"] - d_head["posY"]) <= 0.01
            assert abs(o_head["posZ"] - d_head["posZ"]) <= 0.01
            head_err = _quat_angle_error_deg(
                (o_head["rotX"], o_head["rotY"], o_head["rotZ"], o_head["rotW"]),
                (d_head["rotX"], d_head["rotY"], d_head["rotZ"], d_head["rotW"]),
            )
            assert head_err <= 1.0

            o_right = original["rightHand"]
            d_right = decoded["rightHand"]
            o_rel_right = (
                o_right["posX"] - o_head["posX"],
                o_right["posY"] - o_head["posY"],
                o_right["posZ"] - o_head["posZ"],
            )
            d_rel_right = (
                d_right["posX"] - d_head["posX"],
                d_right["posY"] - d_head["posY"],
                d_right["posZ"] - d_head["posZ"],
            )
            assert abs(o_rel_right[0] - d_rel_right[0]) <= 0.005
            assert abs(o_rel_right[1] - d_rel_right[1]) <= 0.005
            assert abs(o_rel_right[2] - d_rel_right[2]) <= 0.005
            right_err = _quat_angle_error_deg(
                (o_right["rotX"], o_right["rotY"], o_right["rotZ"], o_right["rotW"]),
                (d_right["rotX"], d_right["rotY"], d_right["rotZ"], d_right["rotW"]),
            )
            assert right_err <= 1.0

            o_left = original["leftHand"]
            d_left = decoded["leftHand"]
            o_rel_left = (
                o_left["posX"] - o_head["posX"],
                o_left["posY"] - o_head["posY"],
                o_left["posZ"] - o_head["posZ"],
            )
            d_rel_left = (
                d_left["posX"] - d_head["posX"],
                d_left["posY"] - d_head["posY"],
                d_left["posZ"] - d_head["posZ"],
            )
            assert abs(o_rel_left[0] - d_rel_left[0]) <= 0.005
            assert abs(o_rel_left[1] - d_rel_left[1]) <= 0.005
            assert abs(o_rel_left[2] - d_rel_left[2]) <= 0.005
            left_err = _quat_angle_error_deg(
                (o_left["rotX"], o_left["rotY"], o_left["rotZ"], o_left["rotW"]),
                (d_left["rotX"], d_left["rotY"], d_left["rotZ"], d_left["rotW"]),
            )
            assert left_err <= 1.0

            o_phys = original["physical"]
            d_phys = decoded["physical"]
            assert abs(o_phys["posX"] - d_phys["posX"]) <= 0.01
            assert abs(o_phys["posY"] - d_phys["posY"]) <= 0.01
            assert abs(o_phys["posZ"] - d_phys["posZ"]) <= 0.01
            o_yaw = _yaw_deg_from_quaternion(
                (o_phys["rotX"], o_phys["rotY"], o_phys["rotZ"], o_phys["rotW"])
            )
            d_yaw = _yaw_deg_from_quaternion(
                (d_phys["rotX"], d_phys["rotY"], d_phys["rotZ"], d_phys["rotW"])
            )
            yaw_err = abs(((o_yaw - d_yaw + 180.0) % 360.0) - 180.0)
            assert yaw_err <= 1.0

            o_virtuals = original["virtuals"]
            d_virtuals = decoded["virtuals"]
            assert len(o_virtuals) == len(d_virtuals)
            for ov, dv in zip(o_virtuals, d_virtuals, strict=True):
                o_rel = (
                    ov["posX"] - o_head["posX"],
                    ov["posY"] - o_head["posY"],
                    ov["posZ"] - o_head["posZ"],
                )
                d_rel = (
                    dv["posX"] - d_head["posX"],
                    dv["posY"] - d_head["posY"],
                    dv["posZ"] - d_head["posZ"],
                )
                assert abs(o_rel[0] - d_rel[0]) <= 0.005
                assert abs(o_rel[1] - d_rel[1]) <= 0.005
                assert abs(o_rel[2] - d_rel[2]) <= 0.005
                virtual_err = _quat_angle_error_deg(
                    (ov["rotX"], ov["rotY"], ov["rotZ"], ov["rotW"]),
                    (dv["rotX"], dv["rotY"], dv["rotZ"], dv["rotW"]),
                )
                assert virtual_err <= 1.0

    def test_clamp_boundaries(self) -> None:
        """Out-of-range values should clamp to int16 range."""
        payload = {
            "deviceId": "clamp-test",
            "poseSeq": 10,
            "flags": 0x3E,
            "physical": _build_transform((9999.0, -9999.0, 9999.0), (0.0, 0.0, 0.0, 1.0)),
            "head": _build_transform((9999.0, -9999.0, 9999.0), _random_unit_quaternion(random.Random(1))),
            "rightHand": _build_transform((9999.0, 9999.0, 9999.0), _random_unit_quaternion(random.Random(2))),
            "leftHand": _build_transform((-9999.0, -9999.0, -9999.0), _random_unit_quaternion(random.Random(3))),
            "virtuals": [
                _build_transform((9999.0, 9999.0, 9999.0), _random_unit_quaternion(random.Random(4)))
            ],
        }
        _, decoded, _ = binary_serializer.deserialize(
            binary_serializer.serialize_client_transform(payload)
        )
        assert decoded is not None

        max_abs = 32767 * binary_serializer.ABS_POS_SCALE
        min_abs = -32768 * binary_serializer.ABS_POS_SCALE
        max_rel = 32767 * binary_serializer.REL_POS_SCALE
        min_rel = -32768 * binary_serializer.REL_POS_SCALE

        assert min_abs <= decoded["head"]["posX"] <= max_abs
        assert min_abs <= decoded["head"]["posY"] <= max_abs
        assert min_abs <= decoded["head"]["posZ"] <= max_abs

        rel_right = (
            decoded["rightHand"]["posX"] - decoded["head"]["posX"],
            decoded["rightHand"]["posY"] - decoded["head"]["posY"],
            decoded["rightHand"]["posZ"] - decoded["head"]["posZ"],
        )
        assert min_rel <= rel_right[0] <= max_rel
        assert min_rel <= rel_right[1] <= max_rel
        assert min_rel <= rel_right[2] <= max_rel

    def test_room_relay_integrity(self) -> None:
        """Room serialization should preserve pose sequence, flags, and decoded pose fidelity."""
        rng = random.Random(2026)
        c1 = _build_random_client_pose(rng, virtual_count=2)
        c1["clientNo"] = 101
        c1["poseTime"] = 123.456
        c2 = _build_random_client_pose(rng, virtual_count=1)
        c2["clientNo"] = 202
        c2["poseTime"] = 223.456

        room_payload = {
            "roomId": "room-v3",
            "broadcastTime": 999.123,
            "clients": [c1, c2],
        }
        encoded = binary_serializer.serialize_room_transform(room_payload)
        msg_type, decoded, _ = binary_serializer.deserialize(encoded)

        assert msg_type == binary_serializer.MSG_ROOM_POSE_V2
        assert decoded is not None
        assert decoded["protocolVersion"] == 3
        assert decoded["roomId"] == "room-v3"
        assert len(decoded["clients"]) == 2

        for src, dst in zip(room_payload["clients"], decoded["clients"], strict=True):
            assert src["clientNo"] == dst["clientNo"]
            assert src["poseSeq"] == dst["poseSeq"]
            assert src["flags"] == dst["flags"]
            src_head = src["head"]
            dst_head = dst["head"]
            assert abs(src_head["posX"] - dst_head["posX"]) <= 0.01
            assert abs(src_head["posY"] - dst_head["posY"]) <= 0.01
            assert abs(src_head["posZ"] - dst_head["posZ"]) <= 0.01
