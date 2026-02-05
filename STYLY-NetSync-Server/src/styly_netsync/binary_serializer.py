import logging
import math
import struct
from typing import Any

logger = logging.getLogger(__name__)

# Message type identifiers
PROTOCOL_VERSION = 3
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

# Transform data type identifiers (deprecated - kept for reference)

# Maximum allowed virtual transforms to prevent memory issues
# This can be configured via set_max_virtual_transforms()
_max_virtual_transforms = 50
MAX_VIRTUAL_TRANSFORMS = _max_virtual_transforms  # Legacy alias for backward compat

# Protocol v3 transform encoding constants
ABS_POS_SCALE = 0.01
REL_POS_SCALE = 0.005
PHYSICAL_YAW_SCALE = 0.1

ENCODING_PHYSICAL_YAW_ONLY = 1 << 0
ENCODING_RIGHT_REL_HEAD = 1 << 1
ENCODING_LEFT_REL_HEAD = 1 << 2
ENCODING_VIRTUAL_REL_HEAD = 1 << 3
ENCODING_FLAGS_DEFAULT = (
    ENCODING_PHYSICAL_YAW_ONLY
    | ENCODING_RIGHT_REL_HEAD
    | ENCODING_LEFT_REL_HEAD
    | ENCODING_VIRTUAL_REL_HEAD
)

POSE_FLAG_STEALTH = 1 << 0
POSE_FLAG_PHYSICAL_VALID = 1 << 1
POSE_FLAG_HEAD_VALID = 1 << 2
POSE_FLAG_RIGHT_VALID = 1 << 3
POSE_FLAG_LEFT_VALID = 1 << 4
POSE_FLAG_VIRTUALS_VALID = 1 << 5


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


def _transform_get_position(transform: dict[str, Any]) -> tuple[float, float, float]:
    """Read position from wire transform dict with defaults."""
    return (
        float(transform.get("posX", 0.0)),
        float(transform.get("posY", 0.0)),
        float(transform.get("posZ", 0.0)),
    )


def _transform_get_quaternion(transform: dict[str, Any]) -> tuple[float, float, float, float]:
    """Read quaternion from wire transform dict with defaults."""
    return (
        float(transform.get("rotX", 0.0)),
        float(transform.get("rotY", 0.0)),
        float(transform.get("rotZ", 0.0)),
        float(transform.get("rotW", 1.0)),
    )


def _normalize_quaternion(
    qx: float, qy: float, qz: float, qw: float
) -> tuple[float, float, float, float]:
    """Normalize a quaternion and guard against zero-length input."""
    mag_sq = qx * qx + qy * qy + qz * qz + qw * qw
    if mag_sq <= 1e-12:
        return 0.0, 0.0, 0.0, 1.0

    inv_mag = 1.0 / math.sqrt(mag_sq)
    return qx * inv_mag, qy * inv_mag, qz * inv_mag, qw * inv_mag


def _quaternion_inverse(
    qx: float, qy: float, qz: float, qw: float
) -> tuple[float, float, float, float]:
    """Inverse of a unit quaternion (after normalization)."""
    nx, ny, nz, nw = _normalize_quaternion(qx, qy, qz, qw)
    return -nx, -ny, -nz, nw


def _quaternion_multiply(
    ax: float,
    ay: float,
    az: float,
    aw: float,
    bx: float,
    by: float,
    bz: float,
    bw: float,
) -> tuple[float, float, float, float]:
    """Quaternion multiplication."""
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def _quaternion_to_yaw_degrees(
    qx: float, qy: float, qz: float, qw: float
) -> float:
    """Extract yaw in degrees from quaternion."""
    nx, ny, nz, nw = _normalize_quaternion(qx, qy, qz, qw)
    siny_cosp = 2.0 * (nw * ny + nz * nx)
    cosy_cosp = 1.0 - 2.0 * (ny * ny + nz * nz)
    yaw = math.degrees(math.atan2(siny_cosp, cosy_cosp))
    return _normalize_yaw_degrees(yaw)


def _normalize_yaw_degrees(yaw: float) -> float:
    """Normalize yaw to [-180, 180)."""
    normalized = ((yaw + 180.0) % 360.0) - 180.0
    return normalized


def _quantize_signed(value: float, scale: float) -> int:
    """Quantize a float to signed int16 with clamping."""
    if scale <= 0:
        return 0
    scaled = int(round(value / scale))
    if scaled < -32768:
        return -32768
    if scaled > 32767:
        return 32767
    return scaled


def _dequantize_signed(value: int, scale: float) -> float:
    """Restore quantized value to float."""
    return float(value) * scale


def _compress_quaternion_smallest_three(
    qx: float, qy: float, qz: float, qw: float
) -> int:
    """Compress quaternion to 32-bit using smallest-three encoding."""
    nx, ny, nz, nw = _normalize_quaternion(qx, qy, qz, qw)
    values = [nx, ny, nz, nw]
    abs_values = [abs(v) for v in values]
    largest_index = max(range(4), key=lambda i: abs_values[i])

    if values[largest_index] < 0.0:
        values = [-v for v in values]

    qmin = -0.70710677
    qmax = 0.70710677
    max_10bit = 1023

    packed = largest_index << 30
    write_index = 0
    for i in range(4):
        if i == largest_index:
            continue

        clamped = min(max(values[i], qmin), qmax)
        normalized = (clamped - qmin) / (qmax - qmin)
        scaled = int(round(normalized * max_10bit))
        if scaled < 0:
            scaled = 0
        if scaled > max_10bit:
            scaled = max_10bit
        shift = 20 - (write_index * 10)
        packed |= scaled << shift
        write_index += 1

    return packed


def _decompress_quaternion_smallest_three(packed: int) -> tuple[float, float, float, float]:
    """Decompress 32-bit smallest-three quaternion."""
    largest_index = (packed >> 30) & 0x3
    a = (packed >> 20) & 0x3FF
    b = (packed >> 10) & 0x3FF
    c = packed & 0x3FF

    qmin = -0.70710677
    qmax = 0.70710677
    inv = 1.0 / 1023.0

    def decode(v: int) -> float:
        return qmin + ((qmax - qmin) * (v * inv))

    values = [0.0, 0.0, 0.0, 0.0]
    read_values = [a, b, c]
    read_index = 0
    for i in range(4):
        if i == largest_index:
            continue
        values[i] = decode(read_values[read_index])
        read_index += 1

    sum_sq = 0.0
    for i in range(4):
        if i == largest_index:
            continue
        sum_sq += values[i] * values[i]
    values[largest_index] = math.sqrt(max(0.0, 1.0 - sum_sq))

    return _normalize_quaternion(values[0], values[1], values[2], values[3])


def _create_transform_dict(
    px: float,
    py: float,
    pz: float,
    qx: float,
    qy: float,
    qz: float,
    qw: float,
    is_local_space: bool,
) -> dict[str, Any]:
    """Create a standard wire transform dictionary."""
    return {
        "posX": px,
        "posY": py,
        "posZ": pz,
        "rotX": qx,
        "rotY": qy,
        "rotZ": qz,
        "rotW": qw,
        "isLocalSpace": is_local_space,
    }


def _serialize_client_body(buffer: bytearray, client: dict[str, Any]) -> None:
    """Serialize a client body in protocol v3 compact format."""
    pose_seq = int(client.get("poseSeq", 0)) & 0xFFFF
    physical = client.get("physical", {}) or {}
    head = client.get("head", {}) or {}
    right = client.get("rightHand", {}) or {}
    left = client.get("leftHand", {}) or {}
    virtuals = client.get("virtuals", []) or []

    raw_flags = client.get("flags")
    if raw_flags is None:
        flags = 0
        if physical:
            flags |= POSE_FLAG_PHYSICAL_VALID
        if head:
            flags |= POSE_FLAG_HEAD_VALID
            if right:
                flags |= POSE_FLAG_RIGHT_VALID
            if left:
                flags |= POSE_FLAG_LEFT_VALID
            if virtuals:
                flags |= POSE_FLAG_VIRTUALS_VALID
    else:
        flags = int(raw_flags) & 0xFF

    # Stealth frames must not include transform-valid bits.
    if flags & POSE_FLAG_STEALTH:
        flags = POSE_FLAG_STEALTH

    # Relative transforms require head as anchor.
    if (flags & POSE_FLAG_HEAD_VALID) == 0:
        flags &= ~(POSE_FLAG_RIGHT_VALID | POSE_FLAG_LEFT_VALID | POSE_FLAG_VIRTUALS_VALID)

    buffer.extend(struct.pack("<H", pose_seq))
    buffer.append(flags)
    buffer.append(ENCODING_FLAGS_DEFAULT)

    physical_valid = bool(flags & POSE_FLAG_PHYSICAL_VALID)
    head_valid = bool(flags & POSE_FLAG_HEAD_VALID)
    right_valid = head_valid and bool(flags & POSE_FLAG_RIGHT_VALID)
    left_valid = head_valid and bool(flags & POSE_FLAG_LEFT_VALID)
    virtual_valid = head_valid and bool(flags & POSE_FLAG_VIRTUALS_VALID)

    physical_pos = _transform_get_position(physical)
    physical_rot = _transform_get_quaternion(physical)
    head_pos = _transform_get_position(head)
    head_rot = _transform_get_quaternion(head)
    head_rot_n = _normalize_quaternion(*head_rot)

    if physical_valid:
        buffer.extend(
            struct.pack(
                "<hhh",
                _quantize_signed(physical_pos[0], ABS_POS_SCALE),
                _quantize_signed(physical_pos[1], ABS_POS_SCALE),
                _quantize_signed(physical_pos[2], ABS_POS_SCALE),
            )
        )
        yaw_deg = _quaternion_to_yaw_degrees(*physical_rot)
        buffer.extend(struct.pack("<h", _quantize_signed(yaw_deg, PHYSICAL_YAW_SCALE)))

    if head_valid:
        buffer.extend(
            struct.pack(
                "<hhh",
                _quantize_signed(head_pos[0], ABS_POS_SCALE),
                _quantize_signed(head_pos[1], ABS_POS_SCALE),
                _quantize_signed(head_pos[2], ABS_POS_SCALE),
            )
        )
        head_packed = _compress_quaternion_smallest_three(*head_rot_n)
        buffer.extend(struct.pack("<I", head_packed))

    inv_head_rot = _quaternion_inverse(*head_rot_n)

    if right_valid:
        right_pos = _transform_get_position(right)
        right_rot = _normalize_quaternion(*_transform_get_quaternion(right))
        rel_pos = (
            right_pos[0] - head_pos[0],
            right_pos[1] - head_pos[1],
            right_pos[2] - head_pos[2],
        )
        rel_rot = _quaternion_multiply(*inv_head_rot, *right_rot)
        buffer.extend(
            struct.pack(
                "<hhh",
                _quantize_signed(rel_pos[0], REL_POS_SCALE),
                _quantize_signed(rel_pos[1], REL_POS_SCALE),
                _quantize_signed(rel_pos[2], REL_POS_SCALE),
            )
        )
        buffer.extend(
            struct.pack("<I", _compress_quaternion_smallest_three(*rel_rot))
        )

    if left_valid:
        left_pos = _transform_get_position(left)
        left_rot = _normalize_quaternion(*_transform_get_quaternion(left))
        rel_pos = (
            left_pos[0] - head_pos[0],
            left_pos[1] - head_pos[1],
            left_pos[2] - head_pos[2],
        )
        rel_rot = _quaternion_multiply(*inv_head_rot, *left_rot)
        buffer.extend(
            struct.pack(
                "<hhh",
                _quantize_signed(rel_pos[0], REL_POS_SCALE),
                _quantize_signed(rel_pos[1], REL_POS_SCALE),
                _quantize_signed(rel_pos[2], REL_POS_SCALE),
            )
        )
        buffer.extend(
            struct.pack("<I", _compress_quaternion_smallest_three(*rel_rot))
        )

    virtual_count = 0
    if virtual_valid:
        virtual_count = min(len(virtuals), MAX_VIRTUAL_TRANSFORMS)
    buffer.append(virtual_count)

    for i in range(virtual_count):
        vt = virtuals[i] or {}
        vt_pos = _transform_get_position(vt)
        vt_rot = _normalize_quaternion(*_transform_get_quaternion(vt))
        rel_pos = (
            vt_pos[0] - head_pos[0],
            vt_pos[1] - head_pos[1],
            vt_pos[2] - head_pos[2],
        )
        rel_rot = _quaternion_multiply(*inv_head_rot, *vt_rot)
        buffer.extend(
            struct.pack(
                "<hhh",
                _quantize_signed(rel_pos[0], REL_POS_SCALE),
                _quantize_signed(rel_pos[1], REL_POS_SCALE),
                _quantize_signed(rel_pos[2], REL_POS_SCALE),
            )
        )
        buffer.extend(
            struct.pack("<I", _compress_quaternion_smallest_three(*rel_rot))
        )


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
    if message_type < MSG_CLIENT_TRANSFORM or message_type > MSG_ROOM_POSE_V2:
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


def _deserialize_client_body(data: bytes, offset: int) -> tuple[dict[str, Any], int]:
    """Deserialize protocol v3 compact pose body."""
    result: dict[str, Any] = {}
    result["poseSeq"] = struct.unpack("<H", data[offset : offset + 2])[0]
    offset += 2
    flags = data[offset]
    result["flags"] = flags
    offset += 1
    encoding_flags = data[offset]
    result["encodingFlags"] = encoding_flags
    offset += 1

    physical_valid = bool(flags & POSE_FLAG_PHYSICAL_VALID)
    head_valid = bool(flags & POSE_FLAG_HEAD_VALID)
    right_valid = head_valid and bool(flags & POSE_FLAG_RIGHT_VALID)
    left_valid = head_valid and bool(flags & POSE_FLAG_LEFT_VALID)
    virtual_valid = head_valid and bool(flags & POSE_FLAG_VIRTUALS_VALID)

    physical = _create_transform_dict(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, True)
    head = _create_transform_dict(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, False)
    right = _create_transform_dict(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, False)
    left = _create_transform_dict(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, False)

    head_pos = (0.0, 0.0, 0.0)
    head_rot = (0.0, 0.0, 0.0, 1.0)

    if physical_valid:
        px_q, py_q, pz_q = struct.unpack("<hhh", data[offset : offset + 6])
        offset += 6
        yaw_q = struct.unpack("<h", data[offset : offset + 2])[0]
        offset += 2
        yaw_deg = _dequantize_signed(yaw_q, PHYSICAL_YAW_SCALE)
        yaw_rad = math.radians(yaw_deg)
        half = yaw_rad * 0.5
        qy = math.sin(half)
        qw = math.cos(half)
        physical = _create_transform_dict(
            _dequantize_signed(px_q, ABS_POS_SCALE),
            _dequantize_signed(py_q, ABS_POS_SCALE),
            _dequantize_signed(pz_q, ABS_POS_SCALE),
            0.0,
            qy,
            0.0,
            qw,
            True,
        )

    if head_valid:
        hx_q, hy_q, hz_q = struct.unpack("<hhh", data[offset : offset + 6])
        offset += 6
        packed_head = struct.unpack("<I", data[offset : offset + 4])[0]
        offset += 4
        head_pos = (
            _dequantize_signed(hx_q, ABS_POS_SCALE),
            _dequantize_signed(hy_q, ABS_POS_SCALE),
            _dequantize_signed(hz_q, ABS_POS_SCALE),
        )
        head_rot = _decompress_quaternion_smallest_three(packed_head)
        head = _create_transform_dict(
            head_pos[0],
            head_pos[1],
            head_pos[2],
            head_rot[0],
            head_rot[1],
            head_rot[2],
            head_rot[3],
            False,
        )

    if right_valid:
        rx_q, ry_q, rz_q = struct.unpack("<hhh", data[offset : offset + 6])
        offset += 6
        packed_rel = struct.unpack("<I", data[offset : offset + 4])[0]
        offset += 4
        rel_pos = (
            _dequantize_signed(rx_q, REL_POS_SCALE),
            _dequantize_signed(ry_q, REL_POS_SCALE),
            _dequantize_signed(rz_q, REL_POS_SCALE),
        )
        rel_rot = _decompress_quaternion_smallest_three(packed_rel)
        abs_pos = (
            head_pos[0] + rel_pos[0],
            head_pos[1] + rel_pos[1],
            head_pos[2] + rel_pos[2],
        )
        abs_rot = _quaternion_multiply(*head_rot, *rel_rot)
        abs_rot = _normalize_quaternion(*abs_rot)
        right = _create_transform_dict(
            abs_pos[0], abs_pos[1], abs_pos[2], abs_rot[0], abs_rot[1], abs_rot[2], abs_rot[3], False
        )

    if left_valid:
        lx_q, ly_q, lz_q = struct.unpack("<hhh", data[offset : offset + 6])
        offset += 6
        packed_rel = struct.unpack("<I", data[offset : offset + 4])[0]
        offset += 4
        rel_pos = (
            _dequantize_signed(lx_q, REL_POS_SCALE),
            _dequantize_signed(ly_q, REL_POS_SCALE),
            _dequantize_signed(lz_q, REL_POS_SCALE),
        )
        rel_rot = _decompress_quaternion_smallest_three(packed_rel)
        abs_pos = (
            head_pos[0] + rel_pos[0],
            head_pos[1] + rel_pos[1],
            head_pos[2] + rel_pos[2],
        )
        abs_rot = _quaternion_multiply(*head_rot, *rel_rot)
        abs_rot = _normalize_quaternion(*abs_rot)
        left = _create_transform_dict(
            abs_pos[0], abs_pos[1], abs_pos[2], abs_rot[0], abs_rot[1], abs_rot[2], abs_rot[3], False
        )

    virtual_count = data[offset]
    offset += 1
    if virtual_count > MAX_VIRTUAL_TRANSFORMS:
        virtual_count = MAX_VIRTUAL_TRANSFORMS

    virtuals: list[dict[str, Any]] = []
    for _ in range(virtual_count):
        vx_q, vy_q, vz_q = struct.unpack("<hhh", data[offset : offset + 6])
        offset += 6
        packed_rel = struct.unpack("<I", data[offset : offset + 4])[0]
        offset += 4
        if virtual_valid:
            rel_pos = (
                _dequantize_signed(vx_q, REL_POS_SCALE),
                _dequantize_signed(vy_q, REL_POS_SCALE),
                _dequantize_signed(vz_q, REL_POS_SCALE),
            )
            rel_rot = _decompress_quaternion_smallest_three(packed_rel)
            abs_pos = (
                head_pos[0] + rel_pos[0],
                head_pos[1] + rel_pos[1],
                head_pos[2] + rel_pos[2],
            )
            abs_rot = _quaternion_multiply(*head_rot, *rel_rot)
            abs_rot = _normalize_quaternion(*abs_rot)
            virtuals.append(
                _create_transform_dict(
                    abs_pos[0],
                    abs_pos[1],
                    abs_pos[2],
                    abs_rot[0],
                    abs_rot[1],
                    abs_rot[2],
                    abs_rot[3],
                    False,
                )
            )

    result["physical"] = physical
    result["head"] = head
    result["rightHand"] = right
    result["leftHand"] = left
    result["virtuals"] = virtuals
    return result, offset


def _deserialize_client_transform(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize client pose (v3) from binary data."""
    result: dict[str, Any] = {}

    protocol_version = data[offset]
    result["protocolVersion"] = protocol_version
    offset += 1
    if protocol_version != PROTOCOL_VERSION:
        raise ValueError(f"Unsupported protocol version: {protocol_version}")

    # Device ID
    result["deviceId"], offset = _unpack_string(data, offset)
    body, offset = _deserialize_client_body(data, offset)
    result.update(body)
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
    """Deserialize room pose (v3) with client numbers only."""
    result: dict[str, Any] = {}

    protocol_version = data[offset]
    result["protocolVersion"] = protocol_version
    offset += 1
    if protocol_version != PROTOCOL_VERSION:
        raise ValueError(f"Unsupported protocol version: {protocol_version}")

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

        body, offset = _deserialize_client_body(data, offset)
        client.update(body)

        result["clients"].append(client)

    return result


def _deserialize_device_id_mapping(data: bytes, offset: int) -> dict[str, Any]:
    """Deserialize device ID mapping message"""
    result: dict[str, Any] = {"mappings": []}

    # Skip server version (3 bytes: major, minor, patch)
    offset += 3

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
