"""
Data types for STYLY NetSync client module.

Provides dataclasses that match the Unity NetSync message format
but use Python snake_case conventions.
"""

from dataclasses import dataclass
from typing import Dict, List, Optional
import time


@dataclass
class Transform:
    """Represents a 6-DOF transform with position and rotation.
    
    Uses snake_case naming convention for Python API.
    All coordinates are in Unity's left-handed coordinate system.
    """
    pos_x: float = 0.0
    pos_y: float = 0.0  
    pos_z: float = 0.0
    rot_x: float = 0.0  # Euler rotation in degrees
    rot_y: float = 0.0
    rot_z: float = 0.0
    is_local_space: bool = False
    
    def __post_init__(self):
        """Ensure all values are float type."""
        self.pos_x = float(self.pos_x)
        self.pos_y = float(self.pos_y)
        self.pos_z = float(self.pos_z)
        self.rot_x = float(self.rot_x)
        self.rot_y = float(self.rot_y)
        self.rot_z = float(self.rot_z)


@dataclass
class ClientTransform:
    """Represents a client's complete transform data.
    
    Includes physical body tracking, head/hand tracking, and virtual objects.
    """
    client_no: int
    device_id: str
    physical: Transform
    head: Transform
    right_hand: Transform
    left_hand: Transform
    virtuals: List[Transform]
    
    def __post_init__(self):
        """Ensure transforms are Transform instances and virtuals is a list."""
        if not isinstance(self.physical, Transform):
            self.physical = Transform(**self.physical) if isinstance(self.physical, dict) else Transform()
        if not isinstance(self.head, Transform):
            self.head = Transform(**self.head) if isinstance(self.head, dict) else Transform()
        if not isinstance(self.right_hand, Transform):
            self.right_hand = Transform(**self.right_hand) if isinstance(self.right_hand, dict) else Transform()
        if not isinstance(self.left_hand, Transform):
            self.left_hand = Transform(**self.left_hand) if isinstance(self.left_hand, dict) else Transform()
        
        # Ensure virtuals is a list of Transform objects
        if not isinstance(self.virtuals, list):
            self.virtuals = []
        else:
            self.virtuals = [
                Transform(**v) if isinstance(v, dict) else v 
                for v in self.virtuals
                if isinstance(v, (dict, Transform))
            ]


@dataclass 
class RoomSnapshot:
    """Represents a complete snapshot of all clients in a room.
    
    This is the main data structure for pull-based transform updates.
    """
    room_id: str
    clients: Dict[int, ClientTransform]  # client_no -> ClientTransform
    timestamp: float
    
    def __post_init__(self):
        """Ensure clients dict contains ClientTransform instances."""
        if not isinstance(self.clients, dict):
            self.clients = {}
        
        # Convert dict values to ClientTransform instances if needed
        for client_no, client_data in self.clients.items():
            if not isinstance(client_data, ClientTransform):
                if isinstance(client_data, dict):
                    self.clients[client_no] = ClientTransform(
                        client_no=client_no,
                        **client_data
                    )
        
        # Set timestamp if not provided
        if not hasattr(self, 'timestamp') or self.timestamp == 0:
            self.timestamp = time.time()


@dataclass
class DeviceMapping:
    """Represents the mapping between client numbers and device IDs."""
    client_no: int
    device_id: str
    is_stealth_mode: bool


@dataclass
class RPCMessage:
    """Represents a remote procedure call message."""
    sender_client_no: int
    function_name: str
    arguments_json: str


@dataclass
class NetworkVariable:
    """Represents a network variable with metadata."""
    name: str
    value: str
    timestamp: float
    last_writer_client_no: int


@dataclass
class GlobalVariable(NetworkVariable):
    """Represents a global network variable accessible to all clients."""
    pass


@dataclass
class ClientVariable(NetworkVariable):
    """Represents a client-specific network variable."""
    owner_client_no: int