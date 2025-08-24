"""
Data types for STYLY NetSync client.

All types use snake_case naming conventions for Python compatibility.
"""

from dataclasses import dataclass, field


@dataclass
class transform:
    """Transform data with position and rotation."""

    pos_x: float = 0.0
    pos_y: float = 0.0
    pos_z: float = 0.0
    rot_x: float = 0.0
    rot_y: float = 0.0
    rot_z: float = 0.0
    is_local_space: bool = False


@dataclass
class client_transform:
    """Complete transform data for a client."""

    client_no: int | None = None
    device_id: str | None = None
    physical: transform | None = None
    head: transform | None = None
    right_hand: transform | None = None
    left_hand: transform | None = None
    virtuals: list[transform] | None = None


@dataclass
class room_snapshot:
    """Complete snapshot of a room's state."""

    room_id: str
    clients: dict[int, client_transform] = field(default_factory=dict)
    timestamp: float = 0.0  # monotonic seconds when snapshot was updated
