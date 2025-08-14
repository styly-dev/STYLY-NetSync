import math
import struct
from dataclasses import dataclass, field
from typing import Any, Dict, List, Tuple, Union

# Message type identifiers
MSG_CLIENT_TRANSFORM = 1
MSG_ROOM_TRANSFORM = 2  # Room transform with short IDs only
MSG_RPC_BROADCAST = 3  # Broadcast function call
MSG_RPC_SERVER    = 4  # Client-to-server RPC call
MSG_RPC_CLIENT    = 5  # Client-to-client RPC call
MSG_DEVICE_ID_MAPPING = 6  # Device ID mapping notification
MSG_GLOBAL_VAR_SET = 7  # Set global variable
MSG_GLOBAL_VAR_SYNC = 8  # Sync global variables
MSG_CLIENT_VAR_SET = 9  # Set client variable
MSG_CLIENT_VAR_SYNC = 10  # Sync client variables

# Maximum allowed virtual transforms to prevent memory issues
MAX_VIRTUAL_TRANSFORMS = 50

@dataclass
class Vector3:
    x: float = 0.0
    y: float = 0.0
    z: float = 0.0

@dataclass
class TransformData:
    position: Vector3 = field(default_factory=Vector3)
    rotation: Vector3 = field(default_factory=Vector3)

# Stealth mode detection utilities
def _is_nan_transform(transform: TransformData) -> bool:
    return (
        math.isnan(transform.position.x) and
        math.isnan(transform.position.y) and
        math.isnan(transform.position.z) and
        math.isnan(transform.rotation.x) and
        math.isnan(transform.rotation.y) and
        math.isnan(transform.rotation.z)
    )

def _is_stealth_client(data: Dict[str, Any]) -> bool:
    """Check if client data indicates stealth mode (NaN handshake)"""
    physical = data.get('physical')
    head = data.get('head')
    right = data.get('rightHand')
    left = data.get('leftHand')
    virtuals = data.get('virtuals', [])
    if not all(isinstance(t, TransformData) for t in [physical, head, right, left]):
        return False
    if not (_is_nan_transform(physical) and _is_nan_transform(head) and _is_nan_transform(right) and _is_nan_transform(left)):
        return False
    return len(virtuals) == 0

# Helper functions for common operations
def _pack_string(buffer: bytearray, string: str, use_ushort: bool = False) -> None:
    """Pack a string with length prefix into buffer"""
    string_bytes = string.encode('utf-8')
    if use_ushort:
        buffer.extend(struct.pack('<H', len(string_bytes)))
    else:
        buffer.append(len(string_bytes))
    buffer.extend(string_bytes)

def _unpack_string(data: bytes, offset: int, use_ushort: bool = False) -> Tuple[str, int]:
    """Unpack a length-prefixed string from data"""
    if use_ushort:
        length = struct.unpack('<H', data[offset:offset+2])[0]
        offset += 2
    else:
        length = data[offset]
        offset += 1
    string = data[offset:offset+length].decode('utf-8')
    return string, offset + length

def serialize_transform_data(buffer: bytearray, data: TransformData) -> None:
    buffer.extend(struct.pack('<f', data.position.x))
    buffer.extend(struct.pack('<f', data.position.y))
    buffer.extend(struct.pack('<f', data.position.z))
    buffer.extend(struct.pack('<f', data.rotation.x))
    buffer.extend(struct.pack('<f', data.rotation.y))
    buffer.extend(struct.pack('<f', data.rotation.z))

def deserialize_transform_data(data: bytes, offset: int) -> Tuple[TransformData, int]:
    px, py, pz, rx, ry, rz = struct.unpack_from('<ffffff', data, offset)
    offset += 24
    return TransformData(position=Vector3(px, py, pz), rotation=Vector3(rx, ry, rz)), offset

def _serialize_client_data(buffer: bytearray, client: Dict[str, Any]) -> None:
    """Serialize a single client's data (shared by room transform and client transform)"""
    _pack_string(buffer, client.get('deviceId', ''))
    serialize_transform_data(buffer, client.get('physical', TransformData()))
    serialize_transform_data(buffer, client.get('head', TransformData()))
    serialize_transform_data(buffer, client.get('rightHand', TransformData()))
    serialize_transform_data(buffer, client.get('leftHand', TransformData()))
    virtuals = client.get('virtuals', [])
    virtual_count = min(len(virtuals), MAX_VIRTUAL_TRANSFORMS)
    buffer.append(virtual_count)
    for i in range(virtual_count):
        serialize_transform_data(buffer, virtuals[i])

def serialize_client_transform(data: Dict[str, Any]) -> bytes:
    """Serialize client transform data to binary format"""
    buffer = bytearray()

    # Message type
    buffer.append(MSG_CLIENT_TRANSFORM)

    # Client data
    _serialize_client_data(buffer, data)

    return bytes(buffer)

def serialize_room_transform(data: Dict[str, Any]) -> bytes:
    """Serialize room transform data with short IDs (2 bytes per client ID)
    
    Args:
        data: The room transform data with clientNo field in each client
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_ROOM_TRANSFORM)

    # Room ID
    _pack_string(buffer, data.get('roomId', ''))

    # Number of clients
    clients = data.get('clients', [])
    buffer.extend(struct.pack('<H', len(clients)))  # ushort

    # Each client's data with short ID
    for client in clients:
        _serialize_client_data_short(buffer, client)

    return bytes(buffer)

def _serialize_client_data_short(buffer: bytearray, client: Dict[str, Any]) -> None:
    """Serialize a single client's data with client number only (2 bytes)"""
    # Client number (2 bytes)
    client_no = client.get('clientNo', 0)
    buffer.extend(struct.pack('<H', client_no))
    serialize_transform_data(buffer, client.get('physical', TransformData()))
    serialize_transform_data(buffer, client.get('head', TransformData()))
    serialize_transform_data(buffer, client.get('rightHand', TransformData()))
    serialize_transform_data(buffer, client.get('leftHand', TransformData()))

    virtuals = client.get('virtuals', [])
    virtual_count = min(len(virtuals), MAX_VIRTUAL_TRANSFORMS)
    buffer.append(virtual_count)
    for i in range(virtual_count):
        serialize_transform_data(buffer, virtuals[i])


def _serialize_rpc_base(buffer: bytearray, data: Dict[str, Any], msg_type: int) -> None:
    """Serialize common RPC fields with client numbers"""
    buffer.append(msg_type)

    # Sender client number (2 bytes)
    sender_client_no = data.get('senderClientNo', 0)
    buffer.extend(struct.pack('<H', sender_client_no))

    if msg_type == MSG_RPC_CLIENT:
        # Target client number (2 bytes)
        target_client_no = data.get('targetClientNo', 0)
        buffer.extend(struct.pack('<H', target_client_no))

    _pack_string(buffer, data.get('functionName', ''))
    _pack_string(buffer, data.get('argumentsJson', ''), use_ushort=True)

def serialize_rpc_request(data: Dict[str, Any]) -> bytes:
    """Serialize client-to-server RPC request"""
    buffer = bytearray()
    _serialize_rpc_base(buffer, data, MSG_RPC_SERVER)
    return bytes(buffer)

def serialize_rpc_message(data: Dict[str, Any]) -> bytes:
    """Serialize RPC broadcast message"""
    buffer = bytearray()
    _serialize_rpc_base(buffer, data, MSG_RPC_BROADCAST)
    return bytes(buffer)

def serialize_rpc_client_message(data: Dict[str, Any]) -> bytes:
    """Serialize client-to-client RPC message"""
    buffer = bytearray()
    _serialize_rpc_base(buffer, data, MSG_RPC_CLIENT)
    return bytes(buffer)

def serialize_device_id_mapping(mappings: List[Tuple[int, str, bool]]) -> bytes:
    """Serialize device ID mapping message
    
    Args:
        mappings: List of (client_no, device_id, is_stealth) tuples
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_DEVICE_ID_MAPPING)

    # Number of mappings
    buffer.extend(struct.pack('<H', len(mappings)))

    # Each mapping
    for client_no, device_id, is_stealth in mappings:
        buffer.extend(struct.pack('<H', client_no))
        buffer.append(0x01 if is_stealth else 0x00)  # Stealth flag (1 byte)
        _pack_string(buffer, device_id)

    return bytes(buffer)

def serialize_global_var_set(data: Dict[str, Any]) -> bytes:
    """Serialize global variable set message
    
    Args:
        data: Dictionary with senderClientNo, variableName, variableValue, timestamp
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_GLOBAL_VAR_SET)

    # Sender client number (2 bytes)
    buffer.extend(struct.pack('<H', data.get('senderClientNo', 0)))

    # Variable name (max 64 bytes)
    name = data.get('variableName', '')[:64]
    _pack_string(buffer, name)

    # Variable value (max 1024 bytes)
    value = data.get('variableValue', '')[:1024]
    _pack_string(buffer, value, use_ushort=True)

    # Timestamp (8 bytes double)
    buffer.extend(struct.pack('<d', data.get('timestamp', 0.0)))

    return bytes(buffer)

def serialize_global_var_sync(data: Dict[str, Any]) -> bytes:
    """Serialize global variable sync message
    
    Args:
        data: Dictionary with variables list
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_GLOBAL_VAR_SYNC)

    # Number of variables
    variables = data.get('variables', [])
    buffer.extend(struct.pack('<H', len(variables)))

    # Each variable
    for var in variables:
        _pack_string(buffer, var.get('name', '')[:64])
        _pack_string(buffer, var.get('value', '')[:1024], use_ushort=True)
        buffer.extend(struct.pack('<d', var.get('timestamp', 0.0)))
        buffer.extend(struct.pack('<H', var.get('lastWriterClientNo', 0)))

    return bytes(buffer)

def serialize_client_var_set(data: Dict[str, Any]) -> bytes:
    """Serialize client variable set message
    
    Args:
        data: Dictionary with senderClientNo, targetClientNo, variableName, variableValue, timestamp
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_CLIENT_VAR_SET)

    # Sender client number (2 bytes)
    buffer.extend(struct.pack('<H', data.get('senderClientNo', 0)))

    # Target client number (2 bytes)
    buffer.extend(struct.pack('<H', data.get('targetClientNo', 0)))

    # Variable name (max 64 bytes)
    name = data.get('variableName', '')[:64]
    _pack_string(buffer, name)

    # Variable value (max 1024 bytes)
    value = data.get('variableValue', '')[:1024]
    _pack_string(buffer, value, use_ushort=True)

    # Timestamp (8 bytes double)
    buffer.extend(struct.pack('<d', data.get('timestamp', 0.0)))

    return bytes(buffer)

def serialize_client_var_sync(data: Dict[str, Any]) -> bytes:
    """Serialize client variable sync message
    
    Args:
        data: Dictionary with clientVariables dict
    """
    buffer = bytearray()

    # Message type
    buffer.append(MSG_CLIENT_VAR_SYNC)

    # Client variables by client number
    client_vars = data.get('clientVariables', {})
    buffer.extend(struct.pack('<H', len(client_vars)))

    # Each client's variables
    for client_no_str, variables in client_vars.items():
        client_no = int(client_no_str)
        buffer.extend(struct.pack('<H', client_no))
        buffer.extend(struct.pack('<H', len(variables)))

        # Each variable for this client
        for var in variables:
            _pack_string(buffer, var.get('name', '')[:64])
            _pack_string(buffer, var.get('value', '')[:1024], use_ushort=True)
            buffer.extend(struct.pack('<d', var.get('timestamp', 0.0)))
            buffer.extend(struct.pack('<H', var.get('lastWriterClientNo', 0)))

    return bytes(buffer)

def deserialize(data: bytes) -> Tuple[int, Union[Dict[str, Any], None], bytes]:
    """Deserialize binary data to message type, data, and raw payload
    
    Returns:
        Tuple of (message_type, data_dict, raw_payload)
        raw_payload is the client data portion for MSG_CLIENT_TRANSFORM, empty bytes otherwise
    """
    if not data:
        return 0, None, b''

    offset = 0
    message_type = data[offset]
    offset += 1

    # Validate message type is within valid range
    if message_type < MSG_CLIENT_TRANSFORM or message_type > MSG_CLIENT_VAR_SYNC:
        # Return invalid message type with None data instead of raising exception
        return message_type, None, b''

    try:
        if message_type == MSG_CLIENT_TRANSFORM:
            # Extract the raw client data for caching
            raw_client_data = data[offset:]
            return message_type, _deserialize_client_transform(data, offset), raw_client_data
        elif message_type == MSG_ROOM_TRANSFORM:
            return message_type, _deserialize_room_transform(data, offset), b''
        elif message_type == MSG_RPC_BROADCAST:
            return message_type, _deserialize_rpc_message(data, offset), b''
        elif message_type == MSG_RPC_SERVER:
            return message_type, _deserialize_rpc_message(data, offset), b''
        elif message_type == MSG_RPC_CLIENT:
            return message_type, _deserialize_rpc_client_message(data, offset), b''
        elif message_type == MSG_DEVICE_ID_MAPPING:
            return message_type, _deserialize_device_id_mapping(data, offset), b''
        elif message_type == MSG_GLOBAL_VAR_SET:
            return message_type, _deserialize_global_var_set(data, offset), b''
        elif message_type == MSG_GLOBAL_VAR_SYNC:
            return message_type, _deserialize_global_var_sync(data, offset), b''
        elif message_type == MSG_CLIENT_VAR_SET:
            return message_type, _deserialize_client_var_set(data, offset), b''
        elif message_type == MSG_CLIENT_VAR_SYNC:
            return message_type, _deserialize_client_var_sync(data, offset), b''
        else:
            # Should not reach here due to validation above
            return message_type, None, b''
    except Exception:
        # Error deserializing message - return None data
        return message_type, None, b''

def _deserialize_client_transform(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize client transform from binary data"""
    result: Dict[str, Any] = {}

    result['deviceId'], offset = _unpack_string(data, offset)
    result['physical'], offset = deserialize_transform_data(data, offset)
    result['head'], offset = deserialize_transform_data(data, offset)
    result['rightHand'], offset = deserialize_transform_data(data, offset)
    result['leftHand'], offset = deserialize_transform_data(data, offset)

    virtual_count = data[offset]
    offset += 1
    if virtual_count > MAX_VIRTUAL_TRANSFORMS:
        virtual_count = MAX_VIRTUAL_TRANSFORMS

    result['virtuals'] = []
    for _ in range(virtual_count):
        vt, offset = deserialize_transform_data(data, offset)
        result['virtuals'].append(vt)

    return result

def _deserialize_rpc_message(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize RPC broadcast or server message with sender client number"""
    result: Dict[str, Any] = {}

    # Sender client number (2 bytes)
    result['senderClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    result['functionName'], offset = _unpack_string(data, offset)
    result['argumentsJson'], offset = _unpack_string(data, offset, use_ushort=True)
    return result

def _deserialize_rpc_client_message(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize client-to-client RPC message with sender and target client numbers"""
    result: Dict[str, Any] = {}

    # Sender client number (2 bytes)
    result['senderClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    # Target client number (2 bytes)
    result['targetClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    result['functionName'], offset = _unpack_string(data, offset)
    result['argumentsJson'], offset = _unpack_string(data, offset, use_ushort=True)
    return result

def _deserialize_room_transform(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize room transform with client numbers only"""
    result: Dict[str, Any] = {}

    result['roomId'], offset = _unpack_string(data, offset)

    client_count = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    result['clients'] = []
    for _ in range(client_count):
        client: Dict[str, Any] = {}
        client_no = struct.unpack('<H', data[offset:offset+2])[0]
        offset += 2
        client['clientNo'] = client_no

        client['physical'], offset = deserialize_transform_data(data, offset)
        client['head'], offset = deserialize_transform_data(data, offset)
        client['rightHand'], offset = deserialize_transform_data(data, offset)
        client['leftHand'], offset = deserialize_transform_data(data, offset)

        virtual_count = data[offset]
        offset += 1
        if virtual_count > MAX_VIRTUAL_TRANSFORMS:
            virtual_count = MAX_VIRTUAL_TRANSFORMS

        client['virtuals'] = []
        for _ in range(virtual_count):
            vt, offset = deserialize_transform_data(data, offset)
            client['virtuals'].append(vt)

        result['clients'].append(client)

    return result

def _deserialize_device_id_mapping(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize device ID mapping message"""
    result = {'mappings': []}

    # Number of mappings
    count = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    # Each mapping
    for _ in range(count):
        client_no = struct.unpack('<H', data[offset:offset+2])[0]
        offset += 2
        is_stealth = data[offset] == 0x01  # Read stealth flag (1 byte)
        offset += 1
        device_id, offset = _unpack_string(data, offset)
        result['mappings'].append({'clientNo': client_no, 'deviceId': device_id, 'isStealthMode': is_stealth})

    return result

def _deserialize_global_var_set(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize global variable set message"""
    result = {}

    # Sender client number (2 bytes)
    result['senderClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    # Variable name
    result['variableName'], offset = _unpack_string(data, offset)

    # Variable value
    result['variableValue'], offset = _unpack_string(data, offset, use_ushort=True)

    # Timestamp (8 bytes double)
    result['timestamp'] = struct.unpack('<d', data[offset:offset+8])[0]
    offset += 8

    return result

def _deserialize_global_var_sync(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize global variable sync message"""
    result = {'variables': []}

    # Number of variables
    count = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    # Each variable
    for _ in range(count):
        var = {}
        var['name'], offset = _unpack_string(data, offset)
        var['value'], offset = _unpack_string(data, offset, use_ushort=True)
        var['timestamp'] = struct.unpack('<d', data[offset:offset+8])[0]
        offset += 8
        var['lastWriterClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
        offset += 2
        result['variables'].append(var)

    return result

def _deserialize_client_var_set(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize client variable set message"""
    result = {}

    # Sender client number (2 bytes)
    result['senderClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    # Target client number (2 bytes)
    result['targetClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    # Variable name
    result['variableName'], offset = _unpack_string(data, offset)

    # Variable value
    result['variableValue'], offset = _unpack_string(data, offset, use_ushort=True)

    # Timestamp (8 bytes double)
    result['timestamp'] = struct.unpack('<d', data[offset:offset+8])[0]
    offset += 8

    return result

def _deserialize_client_var_sync(data: bytes, offset: int) -> Dict[str, Any]:
    """Deserialize client variable sync message"""
    result = {'clientVariables': {}}

    # Number of clients
    client_count = struct.unpack('<H', data[offset:offset+2])[0]
    offset += 2

    # Each client's variables
    for _ in range(client_count):
        client_no = struct.unpack('<H', data[offset:offset+2])[0]
        offset += 2

        var_count = struct.unpack('<H', data[offset:offset+2])[0]
        offset += 2

        variables = []
        for _ in range(var_count):
            var = {}
            var['name'], offset = _unpack_string(data, offset)
            var['value'], offset = _unpack_string(data, offset, use_ushort=True)
            var['timestamp'] = struct.unpack('<d', data[offset:offset+8])[0]
            offset += 8
            var['lastWriterClientNo'] = struct.unpack('<H', data[offset:offset+2])[0]
            offset += 2
            variables.append(var)

        result['clientVariables'][str(client_no)] = variables

    return result
