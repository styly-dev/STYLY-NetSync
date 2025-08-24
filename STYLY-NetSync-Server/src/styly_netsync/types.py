"""
Data structures for STYLY NetSync client module.

All data structures use snake_case naming convention as per Python standards.
These structures are converted to/from wire format by the adapter layer.
"""

from dataclasses import dataclass, field
from typing import Dict, List, Optional
import time


@dataclass
class transform:
    """A 6-DOF transform with position and rotation.
    
    Attributes:
        pos_x: X position coordinate
        pos_y: Y position coordinate  
        pos_z: Z position coordinate
        rot_x: X rotation (Euler angles in radians)
        rot_y: Y rotation (Euler angles in radians)
        rot_z: Z rotation (Euler angles in radians)
        is_local_space: Whether transform is in local or world space
    """
    pos_x: float = 0.0
    pos_y: float = 0.0
    pos_z: float = 0.0
    rot_x: float = 0.0
    rot_y: float = 0.0
    rot_z: float = 0.0
    is_local_space: bool = False


@dataclass
class client_transform:
    """Transform data for a single client.
    
    Attributes:
        client_no: Client number (2-byte ID assigned by server, None when sending)
        device_id: Device UUID (36-char string, None when receiving room broadcasts)
        physical: Physical avatar transform (local space, ground movement only)
        head: Head transform (world space, full 6DOF)
        right_hand: Right hand transform (world space, full 6DOF)
        left_hand: Left hand transform (world space, full 6DOF)  
        virtuals: List of virtual object transforms (world space, full 6DOF)
    """
    client_no: Optional[int] = None
    device_id: Optional[str] = None
    physical: Optional[transform] = None
    head: Optional[transform] = None
    right_hand: Optional[transform] = None
    left_hand: Optional[transform] = None
    virtuals: Optional[List[transform]] = None


@dataclass
class room_snapshot:
    """Complete snapshot of a room's state.
    
    This represents the latest known state of all non-stealth clients in a room.
    The snapshot is updated whenever a MSG_ROOM_TRANSFORM message is received.
    
    Attributes:
        room_id: Room identifier
        clients: Dictionary mapping client_no to client_transform
        timestamp: Monotonic timestamp when this snapshot was last updated
    """
    room_id: str = ""
    clients: Dict[int, client_transform] = field(default_factory=dict)
    timestamp: float = field(default_factory=time.monotonic)


@dataclass
class rpc_event:
    """RPC event data.
    
    Attributes:
        sender_client_no: Client number that sent the RPC
        function_name: Name of the function to call
        args: List of string arguments
    """
    sender_client_no: int
    function_name: str
    args: List[str]


@dataclass
class network_variable_event:
    """Network variable change event.
    
    Attributes:
        name: Variable name
        old_value: Previous value (None if new variable)
        new_value: New value
        meta: Additional metadata (timestamp, last_writer_client_no, etc.)
    """
    name: str
    old_value: Optional[str]
    new_value: str
    meta: Dict[str, any] = field(default_factory=dict)


@dataclass
class client_variable_event:
    """Client-specific network variable change event.
    
    Attributes:
        client_no: Client number that owns the variable
        name: Variable name
        old_value: Previous value (None if new variable)
        new_value: New value
        meta: Additional metadata (timestamp, last_writer_client_no, etc.)
    """
    client_no: int
    name: str
    old_value: Optional[str]
    new_value: str
    meta: Dict[str, any] = field(default_factory=dict)


@dataclass
class device_mapping:
    """Device ID to client number mapping.
    
    Attributes:
        client_no: Client number (2-byte ID)
        device_id: Device UUID (36-char string)
        is_stealth: Whether this client is in stealth mode
    """
    client_no: int
    device_id: str
    is_stealth: bool