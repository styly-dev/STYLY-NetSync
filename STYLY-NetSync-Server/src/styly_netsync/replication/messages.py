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


class OwnershipResult(IntEnum):
    """Outcome of an ownership transition, carried in
    :class:`OwnershipEventMessage`.

    ``EXPIRED`` is used by the server's proactive lease sweep to signal
    that a lease has been released without client participation; it is
    distinct from a ``DENIED`` with :attr:`OwnershipEventReasonCode.LEASE_EXPIRED`,
    which describes a request that arrived after the requester's lease
    had already expired.
    """

    GRANTED = 0
    DENIED = 1
    RELEASED = 2
    EXPIRED = 3


class OwnershipEventReasonCode(IntEnum):
    """Auxiliary reason code carried alongside :class:`OwnershipResult`.

    ``NONE`` is used for success cases (``GRANTED``/``RELEASED``) and for
    server-initiated ``EXPIRED`` sweeps. The remaining codes describe
    why a ``DENIED`` result was produced. ``TIMEOUT`` is reserved for
    Unity-side use (a client-local "request never got an answer") and
    is never produced by the server arbiter.
    """

    NONE = 0
    ALREADY_OWNED = 1
    NOT_OWNER = 2
    EPOCH_MISMATCH = 3
    LEASE_EXPIRED = 4
    TIMEOUT = 5


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

    ``your_client_no`` is the short client id the server assigned to
    the joining client; it lets the replication layer learn its own
    identity from the snapshot rather than needing an external wiring.

    ``entities`` is sparse — only touched/owned entities; clients
    reconstruct unmodified scene objects from authored defaults.
    """

    room_id: str
    base_room_seq: int
    server_time_us: int
    your_client_no: int = 0
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
    result: OwnershipResult
    reason_code: OwnershipEventReasonCode = OwnershipEventReasonCode.NONE


@dataclass
class ResyncRequestMessage:
    """Client asks the server for a fresh snapshot.

    ``last_applied_room_seq`` is the highest ``STATE_BATCH.room_seq``
    the client has successfully applied. The server replies with a
    targeted :class:`ResyncReplyMessage` carrying the full authoritative
    room state. A per-entity resync variant may be added in a future
    protocol revision if bandwidth profiling justifies it; v1 is
    full-snapshot only.
    """

    last_applied_room_seq: int = 0


@dataclass
class ResyncReplyMessage:
    """Targeted snapshot unicast back to a requesting client.

    Shares the wire layout of :class:`RoomSnapshotMessage` (minus
    ``your_client_no`` — resync is post-join so the client already
    knows its identity). Kept as a distinct type so call sites can tell
    a resync reply apart from the initial join snapshot.
    """

    room_id: str = ""
    base_room_seq: int = 0
    server_time_us: int = 0
    entities: list[WireEntityRecord] = field(default_factory=list)


@dataclass
class StateBatchMessage:
    """Per-tick batch of entity state updates.

    ``room_seq`` is the ``RoomState.next_room_seq - 1`` value at the
    moment the batch is published. It is monotonically non-decreasing
    and aligns with :class:`RoomSnapshotMessage.base_room_seq`, so
    clients can drop batches that pre-date their last applied snapshot
    and re-order late arrivals.

    ``server_time_us`` is the server wall clock in microseconds since
    the Unix epoch at publish time; clients use it for age-gating
    stale batches on slow links. Defaults to ``0`` for call sites that
    don't care (e.g. tests), but the wire always carries it.
    """

    room_seq: int
    server_time_us: int = 0
    updates: list[StateUpdate] = field(default_factory=list)


__all__ = [
    "ChangedMask",
    "JoinRejectMessage",
    "JoinRejectReason",
    "JoinRoomMessage",
    "OwnershipEventMessage",
    "OwnershipEventReasonCode",
    "OwnershipRequestMessage",
    "OwnershipResult",
    "ResyncReplyMessage",
    "ResyncRequestMessage",
    "RoomSnapshotMessage",
    "StateBatchMessage",
    "StateFlags",
    "StateUpdate",
    "WireEntityRecord",
    "WireTransform",
]
