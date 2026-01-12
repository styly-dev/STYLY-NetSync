import math
import struct
from typing import Any

# Message type identifiers
MSG_CLIENT_TRANSFORM = 1
MSG_ROOM_TRANSFORM = 2  # Room transform with short IDs only
MSG_RPC = 3  # Remote procedure call
MSG_RPC_SERVER = 4  # Reserved for future use
MSG_RPC_CLIENT = 5  # Reserved for future use
MSG_DEVICE_ID_MAPPING = 6  # Device ID mapping notification
MSG_GLOBAL_VAR_SET = 7  # Set global variable
MSG_GLOBAL_VAR_SYNC = 8  # Sync global variables
MSG_CLIENT_VAR_SET = 9  # Set client variable
MSG_CLIENT_VAR_SYNC = 10  # Sync client variables

# Transform data type identifiers (deprecated - kept for reference)

# Maximum allowed virtual transforms to prevent memory issues
MAX_VIRTUAL_TRANSFORMS = 50


# Stealth mode detection utilities
def _is_nan_transform(transform: dict[str, Any]) -> bool:
    """Check if a transform contains all NaN values (stealth mode indicator)"""
    # Check physical transform (all 6 values must be NaN)
    physical = transform.get("physical", {})
    if not physical:
        return False

    # All physical values must be NaN (now 6 floats)
    for key in ["posX", "posY", "posZ", "rotX", "rotY", "rotZ"]:
        if not math.isnan(physical.get(key, 0)):
            return False

    # Check head transform (all 6 values must be NaN)
    head = transform.get("head", {})
    if not head:
        return False
    for key in ["posX", "posY", "posZ", "rotX", "rotY", "rotZ"]:
        if not math.isnan(head.get(key, 0)):
            return False

    # Check right hand transform (all 6 values must be NaN)
    right_hand = transform.get("rightHand", {})
    if not right_hand:
        return False
    for key in ["posX", "posY", "posZ", "rotX", "rotY", "rotZ"]:
        if not math.isnan(right_hand.get(key, 0)):
            return False

    # Check left hand transform (all 6 values must be NaN)
    left_hand = transform.get("leftHand", {})
    if not left_hand:
        return False
    for key in ["posX", "posY", "posZ", "rotX", "rotY", "rotZ"]:
        if not math.isnan(left_hand.get(key, 0)):
            return False

    # Check virtuals count is 0
    virtuals = transform.get("virtuals", [])
    if len(virtuals) != 0:
        return False

    return True


def _is_stealth_client(data: dict[str, Any]) -> bool:
    """Check if client data indicates stealth mode (NaN handshake)"""
    return _is_nan_transform(data)


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
        buffer.extend(struct.pack("<f", transform.get(key, 0)))


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
    """Pack a full 6-float transform"""
    _pack_transform(buffer, transform, ["posX", "posY", "posZ", "rotX", "rotY", "rotZ"])


def _unpack_full_transform(
    data: bytes, offset: int, is_local_space: bool = False
) -> tuple[dict[str, Any], int]:
    """Unpack a full 6-float transform"""
    return _unpack_transform(
        data, offset, ["posX", "posY", "posZ", "rotX", "rotY", "rotZ"], is_local_space
    )


def _serialize_client_data(buffer: bytearray, client: dict[str, Any]) -> None:
    """Serialize a single client's data (shared by room transform and client transform)"""
    # Device ID
    _pack_string(buffer, client.get("deviceId", ""))

    # Physical transform (now full 6 floats)
    _pack_full_transform(buffer, client.get("physical", {}))

    # Head, Right hand, Left hand transforms
    for transform_key in ["head", "rightHand", "leftHand"]:
        _pack_full_transform(buffer, client.get(transform_key, {}))

    # Virtual transforms
    virtuals = client.get("virtuals", [])
    virtual_count = min(len(virtuals), MAX_VIRTUAL_TRANSFORMS)
    # Limit virtual transforms to maximum allowed
    buffer.append(virtual_count)

    for i in range(virtual_count):
        _pack_full_transform(buffer, virtuals[i])


def serialize_client_transform(data: dict[str, Any]) -> bytes:
    """Serialize client transform data to binary format"""
    buffer = bytearray()

    # Message type
    buffer.append(MSG_CLIENT_TRANSFORM)

    # Client data
    _serialize_client_data(buffer, data)

    return bytes(buffer)


def serialize_room_transform(data: dict[str, Any]) -> bytes:
    """Serialize room transform data with short IDs (2 bytes per client ID)

    Args:
        data: The room transform data with clientNo field in each client
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_ROOM_TRANSFORM)

    # Room ID
    _pack_string(buffer, data.get("roomId", ""))

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

    # Physical transform (now full 6 floats)
    _pack_full_transform(buffer, client.get("physical", {}))

    # Head, Right hand, Left hand transforms
    for transform_key in ["head", "rightHand", "leftHand"]:
        _pack_full_transform(buffer, client.get(transform_key, {}))

    # Virtual transforms
    virtuals = client.get("virtuals", [])
    virtual_count = min(len(virtuals), MAX_VIRTUAL_TRANSFORMS)
    # Limit virtual transforms to maximum allowed
    buffer.append(virtual_count)

    for i in range(virtual_count):
        _pack_full_transform(buffer, virtuals[i])


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


def serialize_device_id_mapping(mappings: list[tuple[int, str, bool]]) -> bytes:
    """Serialize device ID mapping message

    Args:
        mappings: List of (client_no, device_id, is_stealth) tuples
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_DEVICE_ID_MAPPING)

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
        raw_payload is the client data portion for MSG_CLIENT_TRANSFORM, empty bytes otherwise
    """
    if not data:
        return 0, None, b""

    offset = 0
    message_type = data[offset]
    offset += 1

    # Validate message type is within valid range
    if message_type < MSG_CLIENT_TRANSFORM or message_type > MSG_CLIENT_VAR_SYNC:
        # Return invalid message type with None data instead of raising exception
        return message_type, None, b""

    try:
        if message_type == MSG_CLIENT_TRANSFORM:
            # Extract the raw client data for caching
            raw_client_data = data[offset:]
            return (
                message_type,
                _deserialize_client_transform(data, offset),
                raw_client_data,
            )
        elif message_type == MSG_ROOM_TRANSFORM:
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
        else:
            # Should not reach here due to validation above
            return message_type, None, b""
    except Exception:
        # Error deserializing message - return None data
        return message_type, None, b""


def _deserialize_client_transform(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize client transform from binary data"""
    result: dict[str, Any] = {}

    # Device ID
    result["deviceId"], offset = _unpack_string(data, offset)

    # Physical transform (now full 6 floats)
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


def _deserialize_room_transform(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize room transform with client numbers only"""
    result: dict[str, Any] = {}

    # Room ID
    result["roomId"], offset = _unpack_string(data, offset)

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

        # Physical transform (now full 6 floats)
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
