"""
Adapter layer for converting between snake_case Python structures and wire format.

The binary_serializer expects camelCase keys matching the Unity/C# format.
This module handles the conversion between Python snake_case and wire camelCase.
"""

import math
from typing import Any, Dict, List, Optional
from .types import transform, client_transform, room_snapshot, device_mapping


# Field name mappings: snake_case -> camelCase
TRANSFORM_FIELD_MAP = {
    'pos_x': 'posX',
    'pos_y': 'posY', 
    'pos_z': 'posZ',
    'rot_x': 'rotX',
    'rot_y': 'rotY',
    'rot_z': 'rotZ',
    'is_local_space': 'isLocalSpace'
}

CLIENT_FIELD_MAP = {
    'client_no': 'clientNo',
    'device_id': 'deviceId',
    'right_hand': 'rightHand',
    'left_hand': 'leftHand'
}

# Reverse mappings: camelCase -> snake_case
TRANSFORM_FIELD_MAP_REVERSE = {v: k for k, v in TRANSFORM_FIELD_MAP.items()}
CLIENT_FIELD_MAP_REVERSE = {v: k for k, v in CLIENT_FIELD_MAP.items()}


def transform_to_wire(t: Optional[transform]) -> Optional[Dict[str, Any]]:
    """Convert snake_case transform to wire format dict."""
    if t is None:
        return None
    
    return {
        TRANSFORM_FIELD_MAP['pos_x']: t.pos_x,
        TRANSFORM_FIELD_MAP['pos_y']: t.pos_y,
        TRANSFORM_FIELD_MAP['pos_z']: t.pos_z,
        TRANSFORM_FIELD_MAP['rot_x']: t.rot_x,
        TRANSFORM_FIELD_MAP['rot_y']: t.rot_y,
        TRANSFORM_FIELD_MAP['rot_z']: t.rot_z,
        TRANSFORM_FIELD_MAP['is_local_space']: t.is_local_space
    }


def transform_from_wire(data: Optional[Dict[str, Any]]) -> Optional[transform]:
    """Convert wire format dict to snake_case transform."""
    if data is None:
        return None
    
    return transform(
        pos_x=data.get(TRANSFORM_FIELD_MAP['pos_x'], 0.0),
        pos_y=data.get(TRANSFORM_FIELD_MAP['pos_y'], 0.0),
        pos_z=data.get(TRANSFORM_FIELD_MAP['pos_z'], 0.0),
        rot_x=data.get(TRANSFORM_FIELD_MAP['rot_x'], 0.0),
        rot_y=data.get(TRANSFORM_FIELD_MAP['rot_y'], 0.0),
        rot_z=data.get(TRANSFORM_FIELD_MAP['rot_z'], 0.0),
        is_local_space=data.get(TRANSFORM_FIELD_MAP['is_local_space'], False)
    )


def client_transform_to_wire(ct: client_transform) -> Dict[str, Any]:
    """Convert snake_case client_transform to wire format dict."""
    result = {}
    
    # Only include device_id if set (not used in room broadcasts)
    if ct.device_id is not None:
        result[CLIENT_FIELD_MAP['device_id']] = ct.device_id
    
    # Only include client_no if set (not used when sending)
    if ct.client_no is not None:
        result[CLIENT_FIELD_MAP['client_no']] = ct.client_no
    
    # Transform fields
    if ct.physical is not None:
        result['physical'] = transform_to_wire(ct.physical)
    
    if ct.head is not None:
        result['head'] = transform_to_wire(ct.head)
    
    if ct.right_hand is not None:
        result[CLIENT_FIELD_MAP['right_hand']] = transform_to_wire(ct.right_hand)
    
    if ct.left_hand is not None:
        result[CLIENT_FIELD_MAP['left_hand']] = transform_to_wire(ct.left_hand)
    
    # Virtuals array
    if ct.virtuals is not None:
        result['virtuals'] = [transform_to_wire(vt) for vt in ct.virtuals]
    
    return result


def client_transform_from_wire(data: Dict[str, Any]) -> client_transform:
    """Convert wire format dict to snake_case client_transform."""
    return client_transform(
        client_no=data.get(CLIENT_FIELD_MAP['client_no']),
        device_id=data.get(CLIENT_FIELD_MAP['device_id']),
        physical=transform_from_wire(data.get('physical')),
        head=transform_from_wire(data.get('head')),
        right_hand=transform_from_wire(data.get(CLIENT_FIELD_MAP['right_hand'])),
        left_hand=transform_from_wire(data.get(CLIENT_FIELD_MAP['left_hand'])),
        virtuals=[transform_from_wire(vt) for vt in data.get('virtuals', [])]
    )


def room_snapshot_from_wire(data: Dict[str, Any]) -> room_snapshot:
    """Convert wire format room data to snake_case room_snapshot."""
    clients = {}
    for client_data in data.get('clients', []):
        client_no = client_data.get(CLIENT_FIELD_MAP['client_no'])
        if client_no is not None:
            clients[client_no] = client_transform_from_wire(client_data)
    
    return room_snapshot(
        room_id=data.get('roomId', ''),
        clients=clients,
        timestamp=data.get('timestamp', 0.0)
    )


def device_mappings_from_wire(data: Dict[str, Any]) -> List[device_mapping]:
    """Convert wire format device mappings to snake_case device_mapping list."""
    mappings = []
    for mapping_data in data.get('mappings', []):
        mappings.append(device_mapping(
            client_no=mapping_data.get(CLIENT_FIELD_MAP['client_no'], 0),
            device_id=mapping_data.get(CLIENT_FIELD_MAP['device_id'], ''),
            is_stealth=mapping_data.get('isStealthMode', False)
        ))
    return mappings


def create_stealth_transform() -> transform:
    """Create a transform with all NaN values for stealth handshake."""
    nan = float('nan')
    return transform(
        pos_x=nan, pos_y=nan, pos_z=nan,
        rot_x=nan, rot_y=nan, rot_z=nan,
        is_local_space=True
    )


def create_stealth_handshake() -> client_transform:
    """Create a complete stealth handshake with all NaN transforms."""
    stealth_transform = create_stealth_transform()
    
    return client_transform(
        physical=stealth_transform,
        head=create_stealth_transform(),
        right_hand=create_stealth_transform(), 
        left_hand=create_stealth_transform(),
        virtuals=[]  # Empty virtuals for stealth
    )