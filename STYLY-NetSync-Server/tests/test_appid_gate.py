#!/usr/bin/env python3
"""
Unit tests for AppID gate functionality in STYLY NetSync Server.

Tests the AppID decision algorithm and HELLO message handling.
"""


import pytest

from styly_netsync import binary_serializer
from styly_netsync.server import NetSyncServer


class TestAppIdGate:
    """Test the AppID gate decision algorithm and related functionality."""

    def test_appid_permission_empty_allowlist(self):
        """Test that empty allow-list permits all AppIDs (filter disabled)."""
        server = NetSyncServer(allowed_app_ids=[])

        # Filter should be disabled
        assert not server.appid_filter_enabled

        # Any AppID should be permitted
        assert server._check_appid_permission("com.styly.prod")
        assert server._check_appid_permission("com.other.app")
        assert server._check_appid_permission("test")

    def test_appid_permission_missing_appid(self):
        """Test that missing or empty AppID is always denied."""
        # Test with filter disabled
        server_disabled = NetSyncServer(allowed_app_ids=[])
        assert not server_disabled._check_appid_permission("")
        assert not server_disabled._check_appid_permission(None)

        # Test with filter enabled
        server_enabled = NetSyncServer(allowed_app_ids=["com.styly.prod"])
        assert not server_enabled._check_appid_permission("")
        assert not server_enabled._check_appid_permission(None)

    def test_appid_permission_allowlist_matching(self):
        """Test AppID matching with allow-list."""
        server = NetSyncServer(allowed_app_ids=["com.styly.prod", "com.styly.stage"])

        # Filter should be enabled
        assert server.appid_filter_enabled

        # Exact matches should be permitted
        assert server._check_appid_permission("com.styly.prod")
        assert server._check_appid_permission("com.styly.stage")

        # Non-matches should be denied
        assert not server._check_appid_permission("com.other.app")
        assert not server._check_appid_permission("com.styly.dev")
        assert not server._check_appid_permission("COM.STYLY.PROD")  # Case sensitive

    def test_appid_permission_byte_wise_equality(self):
        """Test that AppID comparison is byte-wise equality."""
        server = NetSyncServer(allowed_app_ids=["com.styly.prod"])

        # Exact match
        assert server._check_appid_permission("com.styly.prod")

        # Case variations should fail (byte-wise)
        assert not server._check_appid_permission("Com.Styly.Prod")
        assert not server._check_appid_permission("COM.STYLY.PROD")

        # Extra characters should fail
        assert not server._check_appid_permission("com.styly.prod2")
        assert not server._check_appid_permission(" com.styly.prod")
        assert not server._check_appid_permission("com.styly.prod ")

    def test_hello_handshake_validation(self):
        """Test HELLO handshake message validation."""
        server = NetSyncServer(allowed_app_ids=["com.styly.prod"])

        # Valid AppID should pass
        valid_data = {"appId": "com.styly.prod", "deviceId": "device123"}
        assert server._handle_hello_handshake(b"client1", valid_data)

        # Invalid AppID should fail
        invalid_data = {"appId": "com.other.app", "deviceId": "device123"}
        assert not server._handle_hello_handshake(b"client1", invalid_data)

        # Missing AppID should fail
        missing_data = {"deviceId": "device123"}
        assert not server._handle_hello_handshake(b"client1", missing_data)

    def test_hello_handshake_appid_length_validation(self):
        """Test HELLO handshake AppID length validation."""
        server = NetSyncServer(allowed_app_ids=[])  # Filter disabled

        # Valid length AppID
        valid_data = {"appId": "com.styly.prod", "deviceId": "device123"}
        assert server._handle_hello_handshake(b"client1", valid_data)

        # AppID too long (>128 bytes when encoded)
        long_appid = "a" * 129  # 129 characters = 129 bytes in UTF-8
        long_data = {"appId": long_appid, "deviceId": "device123"}
        assert not server._handle_hello_handshake(b"client1", long_data)

        # Edge case: exactly 128 bytes
        edge_appid = "a" * 128  # 128 characters = 128 bytes in UTF-8
        edge_data = {"appId": edge_appid, "deviceId": "device123"}
        assert server._handle_hello_handshake(b"client1", edge_data)

    def test_hello_serialization_deserialization(self):
        """Test HELLO message serialization and deserialization."""
        # Test basic serialization
        hello_data = binary_serializer.serialize_hello("com.styly.prod", "device123")
        assert len(hello_data) > 0
        assert hello_data[0] == binary_serializer.MSG_HELLO

        # Test deserialization
        msg_type, data, raw_payload = binary_serializer.deserialize(hello_data)
        assert msg_type == binary_serializer.MSG_HELLO
        assert data["appId"] == "com.styly.prod"
        assert data["deviceId"] == "device123"
        assert raw_payload == b""

    def test_hello_serialization_without_deviceid(self):
        """Test HELLO message serialization without DeviceID."""
        hello_data = binary_serializer.serialize_hello("com.styly.prod", "")

        # Test deserialization
        msg_type, data, raw_payload = binary_serializer.deserialize(hello_data)
        assert msg_type == binary_serializer.MSG_HELLO
        assert data["appId"] == "com.styly.prod"
        assert data["deviceId"] == ""

    def test_hello_serialization_length_limits(self):
        """Test HELLO message serialization with length limits."""
        # AppID at limit (128 bytes)
        appid_128 = "a" * 128
        deviceid_64 = "b" * 64

        hello_data = binary_serializer.serialize_hello(appid_128, deviceid_64)
        msg_type, data, raw_payload = binary_serializer.deserialize(hello_data)
        assert data["appId"] == appid_128
        assert data["deviceId"] == deviceid_64

        # AppID over limit should raise ValueError
        appid_129 = "a" * 129
        with pytest.raises(ValueError, match="AppID too long"):
            binary_serializer.serialize_hello(appid_129, "device123")

        # DeviceID over limit should raise ValueError
        deviceid_65 = "b" * 65
        with pytest.raises(ValueError, match="DeviceID too long"):
            binary_serializer.serialize_hello("com.styly.prod", deviceid_65)

    def test_server_appid_stats_initialization(self):
        """Test that AppID statistics are properly initialized."""
        server = NetSyncServer(allowed_app_ids=["com.styly.prod"])

        # Check initial stats
        assert server.discovery_allowed == 0
        assert server.discovery_denied == 0
        assert server.handshake_allowed == 0
        assert server.handshake_denied == 0
        assert server.app_id_missing == 0

        # Check filter configuration
        assert server.appid_filter_enabled
        assert "com.styly.prod" in server.allowed_app_ids

    def test_server_appid_filter_disabled_config(self):
        """Test server configuration with AppID filter disabled."""
        server = NetSyncServer(allowed_app_ids=[])

        assert not server.appid_filter_enabled
        assert len(server.allowed_app_ids) == 0

    def test_server_appid_filter_enabled_config(self):
        """Test server configuration with AppID filter enabled."""
        app_ids = ["com.styly.prod", "com.styly.stage", "com.test.app"]
        server = NetSyncServer(allowed_app_ids=app_ids)

        assert server.appid_filter_enabled
        assert server.allowed_app_ids == set(app_ids)


if __name__ == "__main__":
    # Run specific tests
    test_instance = TestAppIdGate()
    test_instance.test_appid_permission_empty_allowlist()
    test_instance.test_appid_permission_missing_appid()
    test_instance.test_appid_permission_allowlist_matching()
    test_instance.test_appid_permission_byte_wise_equality()
    test_instance.test_hello_handshake_validation()
    test_instance.test_hello_handshake_appid_length_validation()
    test_instance.test_hello_serialization_deserialization()
    test_instance.test_hello_serialization_without_deviceid()
    test_instance.test_hello_serialization_length_limits()
    test_instance.test_server_appid_stats_initialization()
    test_instance.test_server_appid_filter_disabled_config()
    test_instance.test_server_appid_filter_enabled_config()

    print("All AppID gate tests passed!")
