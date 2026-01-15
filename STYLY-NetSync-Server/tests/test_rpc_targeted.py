"""Unit tests for targeted RPC functionality."""

from __future__ import annotations

import pytest

from styly_netsync.binary_serializer import (
    MSG_RPC,
    MSG_RPC_TARGETED,
    deserialize,
    serialize_rpc_message,
    serialize_rpc_targeted_message,
)


class TestSerializeRpcTargetedMessage:
    """Tests for serialize_rpc_targeted_message()"""

    def test_single_target(self) -> None:
        """Test serialization with a single target client."""
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": "TestFunction",
            "argumentsJson": '["arg1","arg2"]',
        }
        result = serialize_rpc_targeted_message(data)

        # Verify message type
        assert result[0] == MSG_RPC_TARGETED

        # Deserialize and verify
        msg_type, deserialized, _ = deserialize(result)
        assert msg_type == MSG_RPC_TARGETED
        assert deserialized["senderClientNo"] == 1
        assert deserialized["targetClientNos"] == [2]
        assert deserialized["functionName"] == "TestFunction"
        assert deserialized["argumentsJson"] == '["arg1","arg2"]'

    def test_multiple_targets(self) -> None:
        """Test serialization with multiple target clients."""
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2, 3, 5, 10],
            "functionName": "MultiTarget",
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["targetClientNos"] == [2, 3, 5, 10]

    def test_empty_target_list(self) -> None:
        """Test serialization with empty target list."""
        data = {
            "senderClientNo": 1,
            "targetClientNos": [],
            "functionName": "NoTargets",
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["targetClientNos"] == []

    def test_missing_target_list(self) -> None:
        """Test serialization when targetClientNos is missing."""
        data = {
            "senderClientNo": 1,
            "functionName": "NoTargetKey",
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["targetClientNos"] == []

    def test_function_name_max_length(self) -> None:
        """Test with function name at maximum allowed length (255 bytes)."""
        max_name = "A" * 255
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": max_name,
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["functionName"] == max_name

    def test_function_name_exceeds_limit(self) -> None:
        """Test that function name over 255 bytes raises error."""
        long_name = "A" * 256
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": long_name,
            "argumentsJson": "[]",
        }
        with pytest.raises(ValueError, match="too long"):
            serialize_rpc_targeted_message(data)

    def test_unicode_function_name(self) -> None:
        """Test with unicode characters in function name."""
        # Japanese characters (3 bytes each in UTF-8)
        unicode_name = "テスト関数"  # 5 chars = 15 bytes
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": unicode_name,
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["functionName"] == unicode_name

    def test_unicode_function_name_exceeds_limit(self) -> None:
        """Test that unicode function name over 255 bytes raises error."""
        # Each Japanese character is 3 bytes in UTF-8
        # 86 characters * 3 bytes = 258 bytes > 255
        long_unicode_name = "あ" * 86
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": long_unicode_name,
            "argumentsJson": "[]",
        }
        with pytest.raises(ValueError, match="too long"):
            serialize_rpc_targeted_message(data)

    def test_sender_client_no_zero(self) -> None:
        """Test with senderClientNo=0 (unassigned)."""
        data = {
            "senderClientNo": 0,
            "targetClientNos": [1],
            "functionName": "Test",
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["senderClientNo"] == 0

    def test_large_arguments_json(self) -> None:
        """Test with large argumentsJson payload."""
        large_args = '["' + "x" * 10000 + '"]'
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": "LargeArgs",
            "argumentsJson": large_args,
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["argumentsJson"] == large_args

    def test_max_target_count(self) -> None:
        """Test with many targets (up to 100)."""
        targets = list(range(1, 101))  # 100 targets
        data = {
            "senderClientNo": 1,
            "targetClientNos": targets,
            "functionName": "ManyTargets",
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["targetClientNos"] == targets
        assert len(deserialized["targetClientNos"]) == 100


class TestSerializeRpcMessage:
    """Tests for serialize_rpc_message() (broadcast RPC)"""

    def test_basic_rpc(self) -> None:
        """Test basic RPC serialization."""
        data = {
            "senderClientNo": 1,
            "functionName": "BroadcastFunc",
            "argumentsJson": '["arg1"]',
        }
        result = serialize_rpc_message(data)

        assert result[0] == MSG_RPC

        msg_type, deserialized, _ = deserialize(result)
        assert msg_type == MSG_RPC
        assert deserialized["senderClientNo"] == 1
        assert deserialized["functionName"] == "BroadcastFunc"
        assert deserialized["argumentsJson"] == '["arg1"]'

    def test_function_name_exceeds_limit(self) -> None:
        """Test that function name over 255 bytes raises error."""
        long_name = "B" * 256
        data = {
            "senderClientNo": 1,
            "functionName": long_name,
            "argumentsJson": "[]",
        }
        with pytest.raises(ValueError, match="too long"):
            serialize_rpc_message(data)


class TestDeserializeEdgeCases:
    """Tests for deserialization edge cases."""

    def test_rpc_targeted_roundtrip(self) -> None:
        """Test complete roundtrip for targeted RPC."""
        original = {
            "senderClientNo": 42,
            "targetClientNos": [1, 2, 3],
            "functionName": "RoundTrip",
            "argumentsJson": '{"key":"value"}',
        }
        serialized = serialize_rpc_targeted_message(original)
        msg_type, deserialized, _ = deserialize(serialized)

        assert msg_type == MSG_RPC_TARGETED
        assert deserialized["senderClientNo"] == original["senderClientNo"]
        assert deserialized["targetClientNos"] == original["targetClientNos"]
        assert deserialized["functionName"] == original["functionName"]
        assert deserialized["argumentsJson"] == original["argumentsJson"]

    def test_empty_function_name(self) -> None:
        """Test with empty function name."""
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": "",
            "argumentsJson": "[]",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["functionName"] == ""

    def test_empty_arguments_json(self) -> None:
        """Test with empty argumentsJson."""
        data = {
            "senderClientNo": 1,
            "targetClientNos": [2],
            "functionName": "NoArgs",
            "argumentsJson": "",
        }
        result = serialize_rpc_targeted_message(data)
        msg_type, deserialized, _ = deserialize(result)

        assert deserialized["argumentsJson"] == ""
