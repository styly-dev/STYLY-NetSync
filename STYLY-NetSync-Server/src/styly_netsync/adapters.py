"""
Adapters for converting between Python snake_case API and wire format.

Handles conversion between:
- Python snake_case (pos_x, rot_y, right_hand) and wire camelCase (posX, rotY, rightHand)
- Python dataclasses and dictionary format for binary serialization
- Client-friendly API and internal message format
"""

from typing import Any, Dict, List
import time
import math

from .types import Transform, ClientTransform, RoomSnapshot, DeviceMapping, RPCMessage, NetworkVariable


# Field name mapping: snake_case -> camelCase
TRANSFORM_FIELD_MAP = {
    'pos_x': 'posX',
    'pos_y': 'posY', 
    'pos_z': 'posZ',
    'rot_x': 'rotX',
    'rot_y': 'rotY',
    'rot_z': 'rotZ',
    'is_local_space': 'isLocalSpace'
}

# Reverse mapping: camelCase -> snake_case
TRANSFORM_FIELD_MAP_REVERSE = {v: k for k, v in TRANSFORM_FIELD_MAP.items()}

CLIENT_FIELD_MAP = {
    'client_no': 'clientNo',
    'device_id': 'deviceId',
    'right_hand': 'rightHand',
    'left_hand': 'leftHand'
}

CLIENT_FIELD_MAP_REVERSE = {v: k for k, v in CLIENT_FIELD_MAP.items()}


def transform_to_wire(transform: Transform) -> Dict[str, Any]:
    """Convert Python Transform to wire format dictionary."""
    return {
        TRANSFORM_FIELD_MAP['pos_x']: transform.pos_x,
        TRANSFORM_FIELD_MAP['pos_y']: transform.pos_y,
        TRANSFORM_FIELD_MAP['pos_z']: transform.pos_z,
        TRANSFORM_FIELD_MAP['rot_x']: transform.rot_x,
        TRANSFORM_FIELD_MAP['rot_y']: transform.rot_y,
        TRANSFORM_FIELD_MAP['rot_z']: transform.rot_z,
        TRANSFORM_FIELD_MAP['is_local_space']: transform.is_local_space
    }


def transform_from_wire(wire_data: Dict[str, Any]) -> Transform:
    """Convert wire format dictionary to Python Transform."""
    return Transform(
        pos_x=wire_data.get('posX', 0.0),
        pos_y=wire_data.get('posY', 0.0),
        pos_z=wire_data.get('posZ', 0.0),
        rot_x=wire_data.get('rotX', 0.0),
        rot_y=wire_data.get('rotY', 0.0),
        rot_z=wire_data.get('rotZ', 0.0),
        is_local_space=wire_data.get('isLocalSpace', False)
    )


def client_transform_to_wire(client: ClientTransform) -> Dict[str, Any]:
    """Convert Python ClientTransform to wire format dictionary."""
    wire_data = {
        'clientNo': client.client_no,
        'deviceId': client.device_id,
        'physical': transform_to_wire(client.physical),
        'head': transform_to_wire(client.head),
        'rightHand': transform_to_wire(client.right_hand),
        'leftHand': transform_to_wire(client.left_hand),
        'virtuals': [transform_to_wire(vt) for vt in client.virtuals]
    }
    return wire_data


def client_transform_from_wire(wire_data: Dict[str, Any], client_no: int = None) -> ClientTransform:
    """Convert wire format dictionary to Python ClientTransform."""
    # Extract client number from wire data or parameter
    if client_no is None:
        client_no = wire_data.get('clientNo', 0)
    
    return ClientTransform(
        client_no=client_no,
        device_id=wire_data.get('deviceId', ''),
        physical=transform_from_wire(wire_data.get('physical', {})),
        head=transform_from_wire(wire_data.get('head', {})),
        right_hand=transform_from_wire(wire_data.get('rightHand', {})),
        left_hand=transform_from_wire(wire_data.get('leftHand', {})),
        virtuals=[transform_from_wire(vt) for vt in wire_data.get('virtuals', [])]
    )


def room_snapshot_from_wire(wire_data: Dict[str, Any]) -> RoomSnapshot:
    """Convert wire format room transform to Python RoomSnapshot."""
    clients = {}
    
    # Handle both list format (from room transform) and dict format
    if 'clients' in wire_data:
        if isinstance(wire_data['clients'], list):
            # Room transform format: list of clients with clientNo
            for client_data in wire_data['clients']:
                client_no = client_data.get('clientNo', 0)
                clients[client_no] = client_transform_from_wire(client_data, client_no)
        elif isinstance(wire_data['clients'], dict):
            # Direct dict format: client_no -> client_data
            for client_no_str, client_data in wire_data['clients'].items():
                client_no = int(client_no_str)
                clients[client_no] = client_transform_from_wire(client_data, client_no)
    
    return RoomSnapshot(
        room_id=wire_data.get('roomId', ''),
        clients=clients,
        timestamp=time.time()
    )


def create_stealth_transform() -> Transform:
    """Create a transform with all NaN values for stealth mode."""
    return Transform(
        pos_x=math.nan,
        pos_y=math.nan,
        pos_z=math.nan,
        rot_x=math.nan,
        rot_y=math.nan,
        rot_z=math.nan,
        is_local_space=False
    )


def create_stealth_client_transform(device_id: str) -> ClientTransform:
    """Create a ClientTransform with all NaN values for stealth mode."""
    stealth_transform = create_stealth_transform()
    
    return ClientTransform(
        client_no=0,  # Will be set by server
        device_id=device_id,
        physical=stealth_transform,
        head=stealth_transform, 
        right_hand=stealth_transform,
        left_hand=stealth_transform,
        virtuals=[]  # No virtual transforms in stealth mode
    )


def is_stealth_transform(transform: Transform) -> bool:
    """Check if a transform indicates stealth mode (all NaN values)."""
    return (math.isnan(transform.pos_x) and
            math.isnan(transform.pos_y) and
            math.isnan(transform.pos_z) and
            math.isnan(transform.rot_x) and
            math.isnan(transform.rot_y) and
            math.isnan(transform.rot_z))


def is_stealth_client(client: ClientTransform) -> bool:
    """Check if a client is in stealth mode."""
    return (is_stealth_transform(client.physical) and
            is_stealth_transform(client.head) and
            is_stealth_transform(client.right_hand) and
            is_stealth_transform(client.left_hand) and
            len(client.virtuals) == 0)


def rpc_message_from_wire(wire_data: Dict[str, Any]) -> RPCMessage:
    """Convert wire format RPC message to Python RPCMessage."""
    return RPCMessage(
        sender_client_no=wire_data.get('senderClientNo', 0),
        function_name=wire_data.get('functionName', ''),
        arguments_json=wire_data.get('argumentsJson', '[]')
    )


def device_mappings_from_wire(wire_data: Dict[str, Any]) -> List[DeviceMapping]:
    """Convert wire format device ID mappings to Python DeviceMapping list."""
    mappings = []
    for mapping_data in wire_data.get('mappings', []):
        mappings.append(DeviceMapping(
            client_no=mapping_data.get('clientNo', 0),
            device_id=mapping_data.get('deviceId', ''),
            is_stealth_mode=mapping_data.get('isStealthMode', False)
        ))
    return mappings


def network_variables_from_wire(wire_data: Dict[str, Any]) -> List[NetworkVariable]:
    """Convert wire format global variables to Python NetworkVariable list."""
    variables = []
    for var_data in wire_data.get('variables', []):
        variables.append(NetworkVariable(
            name=var_data.get('name', ''),
            value=var_data.get('value', ''),
            timestamp=var_data.get('timestamp', 0.0),
            last_writer_client_no=var_data.get('lastWriterClientNo', 0)
        ))
    return variables


def client_variables_from_wire(wire_data: Dict[str, Any]) -> Dict[int, List[NetworkVariable]]:
    """Convert wire format client variables to Python dict."""
    client_vars = {}
    for client_no_str, var_list in wire_data.get('clientVariables', {}).items():
        client_no = int(client_no_str)
        variables = []
        for var_data in var_list:
            variables.append(NetworkVariable(
                name=var_data.get('name', ''),
                value=var_data.get('value', ''),
                timestamp=var_data.get('timestamp', 0.0),
                last_writer_client_no=var_data.get('lastWriterClientNo', 0)
            ))
        client_vars[client_no] = variables
    return client_vars