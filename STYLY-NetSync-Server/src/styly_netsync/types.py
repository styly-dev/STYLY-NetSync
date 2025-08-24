from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(slots=True)
class transform:
    """Represents a 6-DOF transform."""

    pos_x: float = 0.0
    pos_y: float = 0.0
    pos_z: float = 0.0
    rot_x: float = 0.0
    rot_y: float = 0.0
    rot_z: float = 0.0
    is_local_space: bool = False


@dataclass(slots=True)
class client_transform:
    """Transform state for a client."""

    client_no: int | None = None
    device_id: str | None = None
    physical: transform | None = None
    head: transform | None = None
    right_hand: transform | None = None
    left_hand: transform | None = None
    virtuals: list[transform] | None = None


@dataclass(slots=True)
class room_snapshot:
    """Snapshot of a room's state."""

    room_id: str
    clients: dict[int, client_transform] = field(default_factory=dict)
    timestamp: float = 0.0
