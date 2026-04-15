"""Unit tests for :class:`SnapshotService`."""

from __future__ import annotations

from styly_netsync.replication.messages import ChangedMask
from styly_netsync.replication.models import (
    EntityKind,
    EntityRecord,
    TransformState,
)
from styly_netsync.replication.room_registry import RoomRegistry
from styly_netsync.replication.snapshot_service import SnapshotService


def _fixed_time() -> int:
    return 1_700_000_000_000_000


def test_build_snapshot_empty_room() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")
    service = SnapshotService(time_source=_fixed_time)

    snapshot = service.build_snapshot(room)

    assert snapshot.room_id == "room-a"
    # next_room_seq defaults to 1 before any tick, so baseline is 0.
    assert snapshot.base_room_seq == 0
    assert snapshot.server_time_us == _fixed_time()
    assert snapshot.entities == []


def test_build_snapshot_includes_only_touched_entities() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")

    # Untouched entity (all zeros, no state) must be skipped.
    registry.upsert_entity(
        room, EntityRecord(entity_id=1, entity_kind=EntityKind.SceneObject)
    )

    # Touched entity with last_accepted_state — must be included.
    touched = EntityRecord(
        entity_id=2,
        entity_kind=EntityKind.SceneObject,
        owner_client_no=5,
        authority_epoch=3,
        pose_seq=12,
        last_accepted_state=TransformState(
            position=(1.0, 2.0, 3.0),
            rotation=(0.0, 0.0, 0.0, 1.0),
            scale=(1.5, 1.5, 1.5),
        ),
    )
    registry.upsert_entity(room, touched)

    # Entity with no state but nonzero pose_seq — must be included.
    seen = EntityRecord(
        entity_id=3,
        entity_kind=EntityKind.Avatar,
        pose_seq=1,
    )
    registry.upsert_entity(room, seen)

    service = SnapshotService(time_source=_fixed_time)
    snapshot = service.build_snapshot(room)

    ids = {e.entity_id for e in snapshot.entities}
    assert ids == {2, 3}

    record_2 = next(e for e in snapshot.entities if e.entity_id == 2)
    assert record_2.authority_epoch == 3
    assert record_2.owner_short_id == 5
    assert record_2.pose_seq == 12
    assert record_2.changed_mask == ChangedMask.ALL
    assert record_2.state.position == (1.0, 2.0, 3.0)
    assert record_2.state.scale == (1.5, 1.5, 1.5)

    record_3 = next(e for e in snapshot.entities if e.entity_id == 3)
    # No state reported yet → mask is NONE, transform defaults apply.
    assert record_3.changed_mask == ChangedMask.NONE
    assert record_3.state.position == (0.0, 0.0, 0.0)
    assert record_3.state.scale == (1.0, 1.0, 1.0)


def test_build_snapshot_without_scale_masks_scale_off() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")

    no_scale = EntityRecord(
        entity_id=9,
        entity_kind=EntityKind.Avatar,
        authority_epoch=1,
        last_accepted_state=TransformState(
            position=(0.0, 1.0, 0.0),
            rotation=(0.0, 0.0, 0.0, 1.0),
            scale=None,
        ),
    )
    registry.upsert_entity(room, no_scale)

    service = SnapshotService(time_source=_fixed_time)
    snapshot = service.build_snapshot(room)

    assert len(snapshot.entities) == 1
    record = snapshot.entities[0]
    assert record.changed_mask == ChangedMask.POSITION | ChangedMask.ROTATION
    # Scale defaulted to identity on the wire because mask is clear.
    assert record.state.scale == (1.0, 1.0, 1.0)


def test_build_snapshot_uses_next_room_seq_minus_one() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")
    room.next_room_seq = 42

    service = SnapshotService(time_source=_fixed_time)
    snapshot = service.build_snapshot(room)

    assert snapshot.base_room_seq == 41
