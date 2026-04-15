"""Unit tests for replication.room_registry."""

from __future__ import annotations

import pytest

from styly_netsync.replication.models import EntityKind, EntityRecord
from styly_netsync.replication.room_registry import (
    DuplicateEntityError,
    RoomRegistry,
    SceneHashMismatchError,
)


def test_get_or_create_creates_room_with_defaults() -> None:
    registry = RoomRegistry()

    room = registry.get_or_create("room-a", "hash-1")

    assert room.room_id == "room-a"
    assert room.scene_hash == "hash-1"
    assert room.next_room_seq == 1
    assert room.next_client_no == 1
    assert room.entities == {}
    assert room.dirty_entity_ids == set()
    assert room.connected_clients == {}


def test_get_or_create_returns_same_room_on_matching_hash() -> None:
    registry = RoomRegistry()

    first = registry.get_or_create("room-a", "hash-1")
    second = registry.get_or_create("room-a", "hash-1")

    assert first is second


def test_get_or_create_raises_on_scene_hash_mismatch() -> None:
    registry = RoomRegistry()
    registry.get_or_create("room-a", "hash-1")

    with pytest.raises(SceneHashMismatchError):
        registry.get_or_create("room-a", "hash-2")


def test_dirty_set_add_and_pop_clears() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")

    registry.add_dirty(room, 10)
    registry.add_dirty(room, 20)
    registry.add_dirty(room, 10)  # idempotent

    dirty = registry.pop_dirty(room)

    assert dirty == {10, 20}
    assert room.dirty_entity_ids == set()

    # Popping again returns an empty set without side effects.
    assert registry.pop_dirty(room) == set()


def test_upsert_entity_inserts_and_returns_existing() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")
    record = EntityRecord(entity_id=5, entity_kind=EntityKind.SceneObject)

    inserted = registry.upsert_entity(room, record)
    assert inserted is record
    assert registry.get_entity(room, 5) is record

    # Re-upserting with the same kind is a no-op returning the existing record.
    duplicate = EntityRecord(entity_id=5, entity_kind=EntityKind.SceneObject)
    again = registry.upsert_entity(room, duplicate)
    assert again is record


def test_upsert_entity_rejects_kind_conflict() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")
    registry.upsert_entity(
        room, EntityRecord(entity_id=7, entity_kind=EntityKind.Avatar)
    )

    with pytest.raises(DuplicateEntityError):
        registry.upsert_entity(
            room, EntityRecord(entity_id=7, entity_kind=EntityKind.SceneObject)
        )


def test_remove_room() -> None:
    registry = RoomRegistry()
    registry.get_or_create("room-a", "hash-1")

    registry.remove("room-a")
    assert registry.get("room-a") is None

    # Removing a missing room is a no-op.
    registry.remove("does-not-exist")


def test_get_entity_returns_none_when_missing() -> None:
    registry = RoomRegistry()
    room = registry.get_or_create("room-a", "hash-1")

    assert registry.get_entity(room, 999) is None
