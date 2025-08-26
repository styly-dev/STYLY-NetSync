"""
Data types for STYLY NetSync client.

All types use snake_case naming conventions for Python compatibility.
"""

from dataclasses import dataclass, field


@dataclass
class transform_data:
    """Transform data with position and rotation."""

    pos_x: float = 0.0
    pos_y: float = 0.0
    pos_z: float = 0.0
    rot_x: float = 0.0
    rot_y: float = 0.0
    rot_z: float = 0.0
    is_local_space: bool = False


@dataclass
class client_transform_data:
    """Complete transform data for a client."""

    client_no: int | None = None
    device_id: str | None = None
    physical: transform_data | None = None
    head: transform_data | None = None
    right_hand: transform_data | None = None
    left_hand: transform_data | None = None
    virtuals: list[transform_data] | None = None


@dataclass
class room_transform_data:
    """Complete snapshot of a room's state."""

    room_id: str
    clients: dict[int, client_transform_data] = field(default_factory=dict)
    timestamp: float = 0.0  # monotonic seconds when snapshot was updated
