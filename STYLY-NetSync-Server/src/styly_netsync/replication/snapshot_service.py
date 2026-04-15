"""Snapshot service.

Builds :class:`RoomSnapshotMessage` payloads from authoritative server
state and converts in-memory :class:`EntityRecord` rows into wire-level
:class:`WireEntityRecord` rows.

Snapshots are **sparse**: only entities the server has observed or is
holding authority for are included. Unmodified scene objects are
reconstructed by the client from authored defaults keyed by entity id,
so shipping every registered entity would waste bandwidth on a large
scene.

Time convention: ``server_time_us`` is wall clock in microseconds since
the Unix epoch (``time.time_ns() // 1000``). Wall clock is chosen over
monotonic so that clients with a roughly-synchronized clock can age
incoming snapshots against their own clock; the replication protocol
does not require high-precision sync.
"""

from __future__ import annotations

import time
from collections.abc import Callable, Iterable

from .messages import (
    ChangedMask,
    RoomSnapshotMessage,
    WireEntityRecord,
    WireTransform,
)
from .models import EntityRecord, TransformState
from .room_registry import RoomState


def _now_us() -> int:
    """Server wall-clock timestamp in microseconds since the Unix epoch."""
    return time.time_ns() // 1000


def _wire_transform_from_state(
    state: TransformState,
) -> tuple[WireTransform, ChangedMask]:
    """Convert a domain :class:`TransformState` to wire form.

    Position and rotation are always considered present; scale is only
    reported when the domain record carries one.
    """
    mask = ChangedMask.POSITION | ChangedMask.ROTATION
    if state.scale is None:
        scale: tuple[float, float, float] = (1.0, 1.0, 1.0)
    else:
        scale = state.scale
        mask |= ChangedMask.SCALE
    return (
        WireTransform(position=state.position, rotation=state.rotation, scale=scale),
        mask,
    )


def _wire_record_from_entity(record: EntityRecord) -> WireEntityRecord:
    """Convert an authoritative :class:`EntityRecord` to wire form.

    Entities that have not yet received their first state keep the
    defaults on :class:`WireTransform` and a :class:`ChangedMask.NONE`
    mask so clients know nothing authoritative has been reported.
    """
    if record.last_accepted_state is None:
        return WireEntityRecord(
            entity_id=record.entity_id,
            authority_epoch=record.authority_epoch,
            owner_short_id=record.owner_client_no,
            pose_seq=record.pose_seq,
            changed_mask=ChangedMask.NONE,
            state=WireTransform(),
        )
    transform, mask = _wire_transform_from_state(record.last_accepted_state)
    return WireEntityRecord(
        entity_id=record.entity_id,
        authority_epoch=record.authority_epoch,
        owner_short_id=record.owner_client_no,
        pose_seq=record.pose_seq,
        changed_mask=mask,
        state=transform,
    )


class SnapshotService:
    """Builds :class:`RoomSnapshotMessage` payloads from room state."""

    def __init__(self, time_source: Callable[[], int] = _now_us) -> None:
        """Create a snapshot service.

        ``time_source`` returns microseconds since the Unix epoch; the
        default uses ``time.time_ns() // 1000``. Tests can inject a
        deterministic source.
        """
        self._time_source = time_source

    def build_snapshot(
        self, room: RoomState, your_client_no: int = 0
    ) -> RoomSnapshotMessage:
        """Build a :class:`RoomSnapshotMessage` for ``room``.

        ``base_room_seq`` is ``next_room_seq - 1`` so it anchors the
        client against the most recently published state-plane tick;
        if the room has never ticked, this evaluates to 0.

        ``your_client_no`` is the short client id the server assigned
        to the recipient. Callers that know the target client should
        pass it so the receiver can learn its own identity from the
        snapshot; defaults to 0 for paths that don't target a
        specific client (e.g. tests).

        Entities are filtered to the set the server has observed —
        anything with a non-zero pose_seq, a current owner, an
        authority epoch, or an accepted state. Freshly registered
        records with all zeros and no state are skipped.
        """
        base_room_seq = max(room.next_room_seq - 1, 0)
        wire_records = [
            _wire_record_from_entity(record)
            for record in self._iter_touched_records(room.entities.values())
        ]
        return RoomSnapshotMessage(
            room_id=room.room_id,
            base_room_seq=base_room_seq,
            server_time_us=self._time_source(),
            your_client_no=your_client_no,
            entities=wire_records,
        )

    @staticmethod
    def _iter_touched_records(
        records: Iterable[EntityRecord],
    ) -> Iterable[EntityRecord]:
        for record in records:
            if (
                record.pose_seq != 0
                or record.owner_client_no != 0
                or record.authority_epoch != 0
                or record.last_accepted_state is not None
            ):
                yield record


__all__ = ["SnapshotService"]
