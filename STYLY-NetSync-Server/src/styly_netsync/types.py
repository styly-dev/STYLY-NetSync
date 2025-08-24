from dataclasses import dataclass, field
from typing import Optional, List, Dict

@dataclass
class transform:
    """Represents a 3D transform."""
    pos_x: float = 0.0
    pos_y: float = 0.0
    pos_z: float = 0.0
    rot_x: float = 0.0
    rot_y: float = 0.0
    rot_z: float = 0.0
    is_local_space: bool = False

@dataclass
class client_transform:
    """Represents the transform of a client, including head, hands, and virtual objects."""
    client_no: Optional[int] = None
    device_id: Optional[str] = None
    physical: Optional[transform] = None
    head: Optional[transform] = None
    right_hand: Optional[transform] = None
    left_hand: Optional[transform] = None
    virtuals: Optional[List[transform]] = field(default_factory=list)

@dataclass
class room_snapshot:
    """Represents a snapshot of the room's state."""
    room_id: str
    clients: Dict[int, client_transform] = field(default_factory=dict)
    timestamp: float = 0.0
