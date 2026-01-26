import logging
import struct
from typing import Any

logger = logging.getLogger(__name__)

# Message type identifiers
PROTOCOL_VERSION = 2
MSG_CLIENT_TRANSFORM = 1
MSG_ROOM_TRANSFORM = 2  # Legacy room transform with short IDs only
MSG_RPC = 3  # Remote procedure call
MSG_RPC_SERVER = 4  # Reserved for future use
MSG_RPC_CLIENT = 5  # Reserved for future use
MSG_DEVICE_ID_MAPPING = 6  # Device ID mapping notification
MSG_GLOBAL_VAR_SET = 7  # Set global variable
MSG_GLOBAL_VAR_SYNC = 8  # Sync global variables
MSG_CLIENT_VAR_SET = 9  # Set client variable
MSG_CLIENT_VAR_SYNC = 10  # Sync client variables
MSG_CLIENT_POSE_V2 = 11
MSG_ROOM_POSE_V2 = 12
MSG_HEARTBEAT = 13
MSG_RPC_DELIVERY = 14
MSG_RPC_ACK = 15

# Transform data type identifiers (deprecated - kept for reference)

# Maximum allowed virtual transforms to prevent memory issues
# This can be configured via set_max_virtual_transforms()
_max_virtual_transforms = 50
MAX_VIRTUAL_TRANSFORMS = _max_virtual_transforms  # Legacy alias for backward compat


def get_max_virtual_transforms() -> int:
    """Get the current maximum virtual transforms limit."""
    return _max_virtual_transforms


def set_max_virtual_transforms(value: int) -> None:
    """Set the maximum virtual transforms limit.

    Args:
        value: New limit (must be positive).
    """
    global _max_virtual_transforms, MAX_VIRTUAL_TRANSFORMS
    if value <= 0:
        raise ValueError("max_virtual_transforms must be positive")
    _max_virtual_transforms = value
    MAX_VIRTUAL_TRANSFORMS = value


def _is_stealth_client(data: dict[str, Any]) -> bool:
    """Check if client data indicates stealth mode (flag bit)."""
    return bool(data.get("flags", 0) & 0x01)


# Helper functions for common operations
def _pack_string(buffer: bytearray, string: str, use_ushort: bool = False) -> None:
    """Pack a string with length prefix into buffer"""
    string_bytes = string.encode("utf-8")
    if use_ushort:
        buffer.extend(struct.pack("<H", len(string_bytes)))
    else:
        buffer.append(len(string_bytes))
    buffer.extend(string_bytes)


def _unpack_string(
    data: bytes, offset: int, use_ushort: bool = False
) -> tuple[str, int]:
    """Unpack a length-prefixed string from data"""
    if use_ushort:
        length = struct.unpack("<H", data[offset : offset + 2])[0]
        offset += 2
    else:
        length = data[offset]
        offset += 1
    string = data[offset : offset + length].decode("utf-8")
    return string, offset + length


def _pack_transform(
    buffer: bytearray, transform: dict[str, Any], keys: list[str]
) -> None:
    """Pack a transform with specified keys"""
    for key in keys:
        default = 1.0 if key == "rotW" else 0.0
        buffer.extend(struct.pack("<f", transform.get(key, default)))


def _unpack_transform(
    data: bytes, offset: int, keys: list[str], is_local_space: bool = False
) -> tuple[dict[str, Any], int]:
    """Unpack a transform with specified keys"""
    transform = {"isLocalSpace": is_local_space}
    for key in keys:
        value = struct.unpack("<f", data[offset : offset + 4])[0]
        transform[key] = value
        offset += 4
    return transform, offset


def _pack_full_transform(buffer: bytearray, transform: dict[str, Any]) -> None:
    """Pack a full 7-float pose (position + quaternion rotation)."""
    _pack_transform(
        buffer, transform, ["posX", "posY", "posZ", "rotX", "rotY", "rotZ", "rotW"]
    )


def _unpack_full_transform(
    data: bytes, offset: int, is_local_space: bool = False
) -> tuple[dict[str, Any], int]:
    """Unpack a full 7-float pose (position + quaternion rotation)."""
    return _unpack_transform(
        data,
        offset,
        ["posX", "posY", "posZ", "rotX", "rotY", "rotZ", "rotW"],
        is_local_space,
    )


def _serialize_client_body(buffer: bytearray, client: dict[str, Any]) -> None:
    """Serialize a client's body (poseSeq, flags, poses, virtuals)."""
    buffer.extend(struct.pack("<H", client.get("poseSeq", 0)))
    buffer.append(client.get("flags", 0))

    _pack_full_transform(buffer, client.get("physical", {}))

    for transform_key in ["head", "rightHand", "leftHand"]:
        _pack_full_transform(buffer, client.get(transform_key, {}))

    virtuals = client.get("virtuals", [])
    virtual_count = min(len(virtuals), MAX_VIRTUAL_TRANSFORMS)
    buffer.append(virtual_count)

    for i in range(virtual_count):
        _pack_full_transform(buffer, virtuals[i])


def serialize_client_transform(data: dict[str, Any]) -> bytes:
    """Serialize client transform data to binary format"""
    buffer = bytearray()

    # Message type
    buffer.append(MSG_CLIENT_POSE_V2)

    # Protocol version
    buffer.append(PROTOCOL_VERSION)

    # Device ID
    _pack_string(buffer, data.get("deviceId", ""))

    # Client body
    _serialize_client_body(buffer, data)

    return bytes(buffer)


def serialize_room_transform(data: dict[str, Any]) -> bytes:
    """Serialize room transform data with short IDs (2 bytes per client ID)

    Args:
        data: The room transform data with clientNo field in each client
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_ROOM_POSE_V2)

    # Protocol version
    buffer.append(PROTOCOL_VERSION)

    # Room ID
    _pack_string(buffer, data.get("roomId", ""))

    # Broadcast time
    buffer.extend(struct.pack("<d", data.get("broadcastTime", 0.0)))

    # Number of clients
    clients = data.get("clients", [])
    buffer.extend(struct.pack("<H", len(clients)))  # ushort

    # Each client's data with short ID
    for client in clients:
        _serialize_client_data_short(buffer, client)

    return bytes(buffer)


def _serialize_client_data_short(buffer: bytearray, client: dict[str, Any]) -> None:
    """Serialize a single client's data with client number only (2 bytes)"""
    # Client number (2 bytes)
    client_no = client.get("clientNo", 0)
    buffer.extend(struct.pack("<H", client_no))

    # Pose time (8 bytes double)
    buffer.extend(struct.pack("<d", client.get("poseTime", 0.0)))

    _serialize_client_body(buffer, client)


def _serialize_rpc_base(buffer: bytearray, data: dict[str, Any], msg_type: int) -> None:
    """Serialize common RPC fields with client numbers"""
    buffer.append(msg_type)

    # Sender client number (2 bytes)
    sender_client_no = data.get("senderClientNo", 0)
    buffer.extend(struct.pack("<H", sender_client_no))

    _pack_string(buffer, data.get("functionName", ""))
    _pack_string(buffer, data.get("argumentsJson", ""), use_ushort=True)


def serialize_rpc_message(data: dict[str, Any]) -> bytes:
    """Serialize RPC message"""
    buffer = bytearray()
    _serialize_rpc_base(buffer, data, MSG_RPC)
    return bytes(buffer)


def serialize_heartbeat(data: dict[str, Any]) -> bytes:
    """Serialize heartbeat message.

    Args:
        data: Dictionary with deviceId, clientNo, timestamp
    """
    buffer = bytearray()
    buffer.append(MSG_HEARTBEAT)
    _pack_string(buffer, data.get("deviceId", ""))
    buffer.extend(struct.pack("<H", data.get("clientNo", 0)))
    buffer.extend(struct.pack("<d", data.get("timestamp", 0.0)))
    return bytes(buffer)


def serialize_rpc_delivery(data: dict[str, Any]) -> bytes:
    """Serialize RPC delivery message."""
    buffer = bytearray()
    buffer.append(MSG_RPC_DELIVERY)
    _pack_string(buffer, data.get("rpcId", ""), use_ushort=True)
    buffer.extend(struct.pack("<H", data.get("senderClientNo", 0)))
    _pack_string(buffer, data.get("functionName", ""))
    _pack_string(buffer, data.get("argumentsJson", ""), use_ushort=True)
    return bytes(buffer)


def serialize_rpc_ack(data: dict[str, Any]) -> bytes:
    """Serialize RPC ack message."""
    buffer = bytearray()
    buffer.append(MSG_RPC_ACK)
    _pack_string(buffer, data.get("rpcId", ""), use_ushort=True)
    buffer.extend(struct.pack("<H", data.get("receiverClientNo", 0)))
    buffer.extend(struct.pack("<d", data.get("timestamp", 0.0)))
    return bytes(buffer)


def parse_version(version_str: str) -> tuple[int, int, int]:
    """Parse semantic version string into (major, minor, patch) tuple.

    Args:
        version_str: Version string like "0.7.5" or "1.2.3-beta"

    Returns:
        Tuple of (major, minor, patch) integers. Returns (0, 0, 0) for invalid input.
    """
    try:
        # Remove any suffix like "-beta", "-rc1", etc.
        base_version = version_str.split("-")[0].split("+")[0]
        parts = base_version.split(".")
        major = int(parts[0]) if len(parts) > 0 else 0
        minor = int(parts[1]) if len(parts) > 1 else 0
        patch = int(parts[2]) if len(parts) > 2 else 0
        # Clamp to 0-255 range for single byte storage
        return (min(major, 255), min(minor, 255), min(patch, 255))
    except (ValueError, IndexError):
        return (0, 0, 0)


def serialize_device_id_mapping(
    mappings: list[tuple[int, str, bool]], version: tuple[int, int, int] = (0, 0, 0)
) -> bytes:
    """Serialize device ID mapping message

    Args:
        mappings: List of (client_no, device_id, is_stealth) tuples
        version: Server version as (major, minor, patch) tuple
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_DEVICE_ID_MAPPING)

    # Server version (3 bytes: major, minor, patch)
    buffer.append(version[0] & 0xFF)
    buffer.append(version[1] & 0xFF)
    buffer.append(version[2] & 0xFF)

    # Number of mappings
    buffer.extend(struct.pack("<H", len(mappings)))

    # Each mapping
    for client_no, device_id, is_stealth in mappings:
        buffer.extend(struct.pack("<H", client_no))
        buffer.append(0x01 if is_stealth else 0x00)  # Stealth flag (1 byte)
        _pack_string(buffer, device_id)

    return bytes(buffer)


def serialize_global_var_set(data: dict[str, Any]) -> bytes:
    """Serialize global variable set message

    Args:
        data: Dictionary with senderClientNo, variableName, variableValue, timestamp
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_GLOBAL_VAR_SET)

    # Sender client number (2 bytes)
    buffer.extend(struct.pack("<H", data.get("senderClientNo", 0)))

    # Variable name (max 64 bytes)
    name = data.get("variableName", "")[:64]
    _pack_string(buffer, name)

    # Variable value (max 1024 bytes)
    value = data.get("variableValue", "")[:1024]
    _pack_string(buffer, value, use_ushort=True)

    # Timestamp (8 bytes double)
    buffer.extend(struct.pack("<d", data.get("timestamp", 0.0)))

    return bytes(buffer)


def serialize_global_var_sync(data: dict[str, Any]) -> bytes:
    """Serialize global variable sync message

    Args:
        data: Dictionary with variables list
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_GLOBAL_VAR_SYNC)

    # Number of variables
    variables = data.get("variables", [])
    buffer.extend(struct.pack("<H", len(variables)))

    # Each variable
    for var in variables:
        _pack_string(buffer, var.get("name", "")[:64])
        _pack_string(buffer, var.get("value", "")[:1024], use_ushort=True)
        buffer.extend(struct.pack("<d", var.get("timestamp", 0.0)))
        buffer.extend(struct.pack("<H", var.get("lastWriterClientNo", 0)))

    return bytes(buffer)


def serialize_client_var_set(data: dict[str, Any]) -> bytes:
    """Serialize client variable set message

    Args:
        data: Dictionary with senderClientNo, targetClientNo, variableName, variableValue, timestamp
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_CLIENT_VAR_SET)

    # Sender client number (2 bytes)
    buffer.extend(struct.pack("<H", data.get("senderClientNo", 0)))

    # Target client number (2 bytes)
    buffer.extend(struct.pack("<H", data.get("targetClientNo", 0)))

    # Variable name (max 64 bytes)
    name = data.get("variableName", "")[:64]
    _pack_string(buffer, name)

    # Variable value (max 1024 bytes)
    value = data.get("variableValue", "")[:1024]
    _pack_string(buffer, value, use_ushort=True)

    # Timestamp (8 bytes double)
    buffer.extend(struct.pack("<d", data.get("timestamp", 0.0)))

    return bytes(buffer)


def serialize_client_var_sync(data: dict[str, Any]) -> bytes:
    """Serialize client variable sync message

    Args:
        data: Dictionary with clientVariables dict
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_CLIENT_VAR_SYNC)

    # Client variables by client number
    client_vars = data.get("clientVariables", {})
    buffer.extend(struct.pack("<H", len(client_vars)))

    # Each client's variables
    for client_no_str, variables in client_vars.items():
        client_no = int(client_no_str)
        buffer.extend(struct.pack("<H", client_no))
        buffer.extend(struct.pack("<H", len(variables)))

        # Each variable for this client
        for var in variables:
            _pack_string(buffer, var.get("name", "")[:64])
            _pack_string(buffer, var.get("value", "")[:1024], use_ushort=True)
            buffer.extend(struct.pack("<d", var.get("timestamp", 0.0)))
            buffer.extend(struct.pack("<H", var.get("lastWriterClientNo", 0)))

    return bytes(buffer)


def deserialize(data: bytes) -> tuple[int, dict[str, Any] | None, bytes]:
    """Deserialize binary data to message type, data, and raw payload

    Returns:
        Tuple of (message_type, data_dict, raw_payload)
        raw_payload is the client data portion for MSG_CLIENT_POSE_V2, empty bytes otherwise
    """
    if not data:
        return 0, None, b""

    offset = 0
    message_type = data[offset]
    offset += 1

    # Validate message type is within valid range
    if message_type < MSG_CLIENT_TRANSFORM or message_type > MSG_RPC_ACK:
        # Return invalid message type with None data instead of raising exception
        return message_type, None, b""

    try:
        if message_type == MSG_CLIENT_POSE_V2:
            device_id_len = data[offset + 1]
            body_offset = offset + 2 + device_id_len
            raw_client_data = data[body_offset:]
            return (
                message_type,
                _deserialize_client_transform(data, offset),
                raw_client_data,
            )
        elif message_type == MSG_ROOM_POSE_V2:
            return message_type, _deserialize_room_transform(data, offset), b""
        elif message_type == MSG_RPC:
            return message_type, _deserialize_rpc_message(data, offset), b""
        # MSG_RPC_SERVER and MSG_RPC_CLIENT are reserved for future use
        elif message_type == MSG_DEVICE_ID_MAPPING:
            return message_type, _deserialize_device_id_mapping(data, offset), b""
        elif message_type == MSG_GLOBAL_VAR_SET:
            return message_type, _deserialize_global_var_set(data, offset), b""
        elif message_type == MSG_GLOBAL_VAR_SYNC:
            return message_type, _deserialize_global_var_sync(data, offset), b""
        elif message_type == MSG_CLIENT_VAR_SET:
            return message_type, _deserialize_client_var_set(data, offset), b""
        elif message_type == MSG_CLIENT_VAR_SYNC:
            return message_type, _deserialize_client_var_sync(data, offset), b""
        elif message_type == MSG_HEARTBEAT:
            return message_type, _deserialize_heartbeat(data, offset), b""
        elif message_type == MSG_RPC_DELIVERY:
            return message_type, _deserialize_rpc_delivery(data, offset), b""
        elif message_type == MSG_RPC_ACK:
            return message_type, _deserialize_rpc_ack(data, offset), b""
        else:
            # Should not reach here due to validation above
            return message_type, None, b""
    except Exception as e:
        # Log deserialization error at DEBUG level for troubleshooting
        logger.debug(
            "Deserialization failed for message type %d: %s",
            message_type,
            str(e),
        )
        return message_type, None, b""


def _deserialize_client_transform(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize client pose (v2) from binary data."""
    result: dict[str, Any] = {}

    result["protocolVersion"] = data[offset]
    offset += 1

    # Device ID
    result["deviceId"], offset = _unpack_string(data, offset)

    result["poseSeq"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2
    result["flags"] = data[offset]
    offset += 1

    # Physical pose
    result["physical"], offset = _unpack_full_transform(
        data, offset, is_local_space=True
    )

    # Head, Right hand, Left hand transforms
    result["head"], offset = _unpack_full_transform(data, offset)
    result["rightHand"], offset = _unpack_full_transform(data, offset)
    result["leftHand"], offset = _unpack_full_transform(data, offset)

    # Virtual transforms
    virtual_count = data[offset]
    offset += 1

    # Validate virtual count to prevent memory issues
    if virtual_count > MAX_VIRTUAL_TRANSFORMS:
        virtual_count = MAX_VIRTUAL_TRANSFORMS

    if virtual_count > 0:
        result["virtuals"] = []
        for _ in range(virtual_count):
            vt, offset = _unpack_full_transform(data, offset)
            result["virtuals"].append(vt)

    return result


def _deserialize_rpc_message(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize RPC message with sender client number"""
    result: dict[str, Any] = {}

    # Sender client number (2 bytes)
    result["senderClientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    result["functionName"], offset = _unpack_string(data, offset)
    result["argumentsJson"], offset = _unpack_string(data, offset, use_ushort=True)
    return result


def _deserialize_heartbeat(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize heartbeat message."""
    result: dict[str, Any] = {}
    result["deviceId"], offset = _unpack_string(data, offset)
    result["clientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2
    result["timestamp"] = struct.unpack("<d", data[offset : offset + 8])[0]
    return result


def _deserialize_rpc_delivery(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize RPC delivery message."""
    result: dict[str, Any] = {}
    result["rpcId"], offset = _unpack_string(data, offset, use_ushort=True)
    result["senderClientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2
    result["functionName"], offset = _unpack_string(data, offset)
    result["argumentsJson"], offset = _unpack_string(data, offset, use_ushort=True)
    return result


def _deserialize_rpc_ack(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize RPC ack message."""
    result: dict[str, Any] = {}
    result["rpcId"], offset = _unpack_string(data, offset, use_ushort=True)
    result["receiverClientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2
    result["timestamp"] = struct.unpack("<d", data[offset : offset + 8])[0]
    return result


def _deserialize_room_transform(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize room pose (v2) with client numbers only."""
    result: dict[str, Any] = {}

    result["protocolVersion"] = data[offset]
    offset += 1

    # Room ID
    result["roomId"], offset = _unpack_string(data, offset)

    result["broadcastTime"] = struct.unpack("<d", data[offset : offset + 8])[0]
    offset += 8

    # Number of clients
    client_count = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    result["clients"] = []
    for _ in range(client_count):
        client = {}

        # Client number (2 bytes)
        client_no = struct.unpack("<H", data[offset : offset + 2])[0]
        offset += 2
        client["clientNo"] = client_no

        # Pose time
        client["poseTime"] = struct.unpack("<d", data[offset : offset + 8])[0]
        offset += 8

        # Pose sequence + flags
        client["poseSeq"] = struct.unpack("<H", data[offset : offset + 2])[0]
        offset += 2
        client["flags"] = data[offset]
        offset += 1

        # Physical pose
        client["physical"], offset = _unpack_full_transform(
            data, offset, is_local_space=True
        )

        # Head, Right hand, Left hand transforms
        client["head"], offset = _unpack_full_transform(data, offset)
        client["rightHand"], offset = _unpack_full_transform(data, offset)
        client["leftHand"], offset = _unpack_full_transform(data, offset)

        # Virtual transforms
        virtual_count = data[offset]
        offset += 1

        # Validate virtual count to prevent memory issues
        if virtual_count > MAX_VIRTUAL_TRANSFORMS:
            virtual_count = MAX_VIRTUAL_TRANSFORMS

        if virtual_count > 0:
            client["virtuals"] = []
            for _ in range(virtual_count):
                vt, offset = _unpack_full_transform(data, offset)
                client["virtuals"].append(vt)

        result["clients"].append(client)

    return result


def _deserialize_device_id_mapping(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize device ID mapping message"""
    result: dict[str, Any] = {"mappings": []}

    # Number of mappings
    count = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    # Each mapping
    for _ in range(count):
        client_no = struct.unpack("<H", data[offset : offset + 2])[0]
        offset += 2
        is_stealth = data[offset] == 0x01  # Read stealth flag (1 byte)
        offset += 1
        device_id, offset = _unpack_string(data, offset)
        result["mappings"].append(
            {"clientNo": client_no, "deviceId": device_id, "isStealthMode": is_stealth}
        )

    return result


def _deserialize_global_var_set(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize global variable set message"""
    result: dict[str, Any] = {}

    # Sender client number (2 bytes)
    result["senderClientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    # Variable name
    result["variableName"], offset = _unpack_string(data, offset)

    # Variable value
    result["variableValue"], offset = _unpack_string(data, offset, use_ushort=True)

    # Timestamp (8 bytes double)
    result["timestamp"] = struct.unpack("<d", data[offset : offset + 8])[0]
    offset += 8

    return result


def _deserialize_global_var_sync(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize global variable sync message"""
    result: dict[str, Any] = {"variables": []}

    # Number of variables
    count = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    # Each variable
    for _ in range(count):
        var = {}
        var["name"], offset = _unpack_string(data, offset)
        var["value"], offset = _unpack_string(data, offset, use_ushort=True)
        var["timestamp"] = struct.unpack("<d", data[offset : offset + 8])[0]
        offset += 8
        var["lastWriterClientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
        offset += 2
        result["variables"].append(var)

    return result


def _deserialize_client_var_set(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize client variable set message"""
    result: dict[str, Any] = {}

    # Sender client number (2 bytes)
    result["senderClientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    # Target client number (2 bytes)
    result["targetClientNo"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    # Variable name
    result["variableName"], offset = _unpack_string(data, offset)

    # Variable value
    result["variableValue"], offset = _unpack_string(data, offset, use_ushort=True)

    # Timestamp (8 bytes double)
    result["timestamp"] = struct.unpack("<d", data[offset : offset + 8])[0]
    offset += 8

    return result


def _deserialize_client_var_sync(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize client variable sync message"""
    result: dict[str, Any] = {"clientVariables": {}}

    # Number of clients
    client_count = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2

    # Each client's variables
    for _ in range(client_count):
        client_no = struct.unpack("<H", data[offset : offset + 2])[0]
        offset += 2

        var_count = struct.unpack("<H", data[offset : offset + 2])[0]
        offset += 2

        variables = []
        for _ in range(var_count):
            var = {}
            var["name"], offset = _unpack_string(data, offset)
            var["value"], offset = _unpack_string(data, offset, use_ushort=True)
            var["timestamp"] = struct.unpack("<d", data[offset : offset + 8])[0]
            offset += 8
            var["lastWriterClientNo"] = struct.unpack("<H", data[offset : offset + 2])[
                0
            ]
            offset += 2
            variables.append(var)

        result["clientVariables"][str(client_no)] = variables

    return result
