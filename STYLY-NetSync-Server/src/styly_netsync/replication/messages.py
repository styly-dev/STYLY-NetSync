"""Wire-level message dataclasses for replication protocol v1.

These types describe the exact byte layout in
``docs/replication-protocol-v1.md`` and are the Python mirror of
``ReplicationMessages.cs`` / ``EntityState.cs`` on the Unity side.

Higher-level domain state (:class:`.models.EntityRecord`,
:class:`.room_registry.RoomState`) lives in sibling modules and is what
the server mutates at runtime; the types here are pure wire DTOs.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum, IntFlag


class StateFlags(IntFlag):
    """Bit flags on every :class:`StateUpdate`."""

    NONE = 0
    KEYFRAME = 1 << 0
    TELEPORT = 1 << 1
    HEARTBEAT = 1 << 2


class ChangedMask(IntFlag):
    """Selects which transform fields are present on the wire."""

    NONE = 0
    POSITION = 1 << 0
    ROTATION = 1 << 1
    SCALE = 1 << 2
    ALL = POSITION | ROTATION | SCALE


class OwnershipReason(IntEnum):
    """Reason code for :class:`OwnershipEventMessage`."""

    GRANTED = 0
    REJECTED = 1
    REVOKED = 2
    RELEASED = 3


class JoinRejectReason(IntEnum):
    """Reason code returned by the server when a JOIN_ROOM is rejected.

    Unused codes are reserved for future expansion; 255 is the catch-all
    so clients can always surface a message even if the code is unknown.
    """

    SCENE_HASH_MISMATCH = 0
    ROOM_FULL = 1
    PROTOCOL_VERSION_MISMATCH = 2
    UNSPECIFIED = 255


@dataclass
class WireTransform:
    """Wire-level transform snapshot. v1 stores float32 components.

    ``scale`` defaults to ``(1, 1, 1)``; absence on the wire is encoded
    by clearing the scale bit in :class:`ChangedMask`.
    """

    position: tuple[float, float, float] = (0.0, 0.0, 0.0)
    rotation: tuple[float, float, float, float] = (0.0, 0.0, 0.0, 1.0)
    scale: tuple[float, float, float] = (1.0, 1.0, 1.0)


@dataclass
class StateUpdate:
    entity_id: int
    authority_epoch: int
    pose_seq: int
    flags: StateFlags
    changed_mask: ChangedMask
    state: WireTransform = field(default_factory=WireTransform)


@dataclass
class WireEntityRecord:
    """Wire-level entity record (used in snapshots and resync replies)."""

    entity_id: int
    authority_epoch: int
    owner_short_id: int
    pose_seq: int
    changed_mask: ChangedMask
    state: WireTransform = field(default_factory=WireTransform)


@dataclass
class JoinRoomMessage:
    room_id: str
    device_id: str
    scene_hash: str = ""


@dataclass
class RoomSnapshotMessage:
    """Snapshot delivered to a client on successful JOIN_ROOM.

    ``base_room_seq`` is ``RoomState.next_room_seq - 1`` and anchors
    subsequent STATE_BATCH deltas; a client can detect an out-of-order
    snapshot by comparing against later batch sequences.

    ``server_time_us`` is the server's wall clock in microseconds since
    the Unix epoch (``time.time_ns() // 1000`` on the server). Clients
    use it only for relative age calculations, so any monotonic drift
    between server and client is irrelevant.

    ``entities`` is sparse — only touched/owned entities; clients
    reconstruct unmodified scene objects from authored defaults.
    """

    room_id: str
    base_room_seq: int
    server_time_us: int
    entities: list[WireEntityRecord] = field(default_factory=list)


@dataclass
class JoinRejectMessage:
    """Server response when a JOIN_ROOM cannot be accepted."""

    room_id: str
    reason: JoinRejectReason
    reason_text: str = ""


@dataclass
class OwnershipRequestMessage:
    entity_id: int
    requester_short_id: int
    expected_epoch: int


@dataclass
class OwnershipEventMessage:
    entity_id: int
    new_owner_short_id: int
    new_authority_epoch: int
    reason: OwnershipReason


@dataclass
class ResyncRequestMessage:
    entity_ids: list[int] = field(default_factory=list)


@dataclass
class ResyncReplyMessage:
    entities: list[WireEntityRecord] = field(default_factory=list)


@dataclass
class StateBatchMessage:
    server_tick: int
    updates: list[StateUpdate] = field(default_factory=list)


__all__ = [
    "ChangedMask",
    "JoinRejectMessage",
    "JoinRejectReason",
    "JoinRoomMessage",
    "OwnershipEventMessage",
    "OwnershipReason",
    "OwnershipRequestMessage",
    "ResyncReplyMessage",
    "ResyncRequestMessage",
    "RoomSnapshotMessage",
    "StateBatchMessage",
    "StateFlags",
    "StateUpdate",
    "WireEntityRecord",
    "WireTransform",
]
