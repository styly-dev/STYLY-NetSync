"""Tests for binary_serializer module."""

from __future__ import annotations

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
