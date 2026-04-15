"""Unit tests for :class:`StateRelay`."""

from __future__ import annotations

from styly_netsync.replication.messages import (
    ChangedMask,
    StateBatchMessage,
    StateFlags,
    StateUpdate,
    WireTransform,
)
from styly_netsync.replication.models import (
    EntityKind,
    EntityRecord,
    TransformState,
)
from styly_netsync.replication.room_registry import RoomRegistry
from styly_netsync.replication.state_relay import StateRelay

LEASE_SEC = 2.0


def _make_relay(
    clock_value: float = 100.0,
) -> tuple[StateRelay, RoomRegistry, list[tuple[str, bytes]]]:
    registry = RoomRegistry()
    broadcast: list[tuple[str, bytes]] = []

    def _bcast(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    relay = StateRelay(
        room_registry=registry,
        broadcast=_bcast,
        lease_sec=LEASE_SEC,
        clock=lambda: clock_value,
        time_source_us=lambda: 0,
    )
    return relay, registry, broadcast


def _seed_owned_entity(
    registry: RoomRegistry,
    *,
    entity_id: int = 1,
    owner: int = 5,
    epoch: int = 1,
    pose_seq: int = 0,
) -> None:
    room = registry.get_or_create("lobby", "h")
    registry.upsert_entity(
        room,
        EntityRecord(
            entity_id=entity_id,
            entity_kind=EntityKind.SceneObject,
            owner_client_no=owner,
            authority_epoch=epoch,
            pose_seq=pose_seq,
        ),
    )


def _sample_update(
    *,
    entity_id: int = 1,
    epoch: int = 1,
    pose_seq: int = 1,
    flags: StateFlags = StateFlags.NONE,
    mask: ChangedMask = ChangedMask.POSITION | ChangedMask.ROTATION,
    position: tuple[float, float, float] = (1.0, 2.0, 3.0),
) -> StateUpdate:
    return StateUpdate(
        entity_id=entity_id,
        authority_epoch=epoch,
        pose_seq=pose_seq,
        flags=flags,
        changed_mask=mask,
        state=WireTransform(position=position),
    )


# --- accept_state_batch: happy path ---------------------------------------


def test_accept_valid_update_marks_dirty_and_updates_record() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None

    accepted = relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update()]),
    )

    assert len(accepted) == 1
    assert room.dirty_entity_ids == {1}

    record = room.entities[1]
    assert record.pose_seq == 1
    assert record.last_accepted_state is not None
    assert record.last_accepted_state.position == (1.0, 2.0, 3.0)
    assert record.last_server_time == 100.0
    assert record.lease_expire_at == 100.0 + LEASE_SEC


# --- accept_state_batch: drop paths ---------------------------------------


def test_reject_update_from_non_owner() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None

    accepted = relay.accept_state_batch(
        sender_client_no=9,  # not the owner
        room=room,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update()]),
    )

    assert accepted == []
    assert room.dirty_entity_ids == set()
    assert room.entities[1].pose_seq == 0
    assert room.entities[1].last_accepted_state is None


def test_reject_update_with_epoch_mismatch() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry, epoch=3)
    room = registry.get("lobby")
    assert room is not None

    accepted = relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update(epoch=2)]),
    )

    assert accepted == []
    assert room.dirty_entity_ids == set()


def test_reject_stale_pose_seq() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry, pose_seq=10)
    room = registry.get("lobby")
    assert room is not None

    accepted = relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0, updates=[_sample_update(pose_seq=10)]  # == not >
        ),
    )
    assert accepted == []

    accepted = relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0, updates=[_sample_update(pose_seq=9)]  # stale
        ),
    )
    assert accepted == []
    assert room.dirty_entity_ids == set()


def test_reject_unowned_entity_update() -> None:
    relay, registry, _ = _make_relay()
    room = registry.get_or_create("lobby", "h")
    registry.upsert_entity(
        room,
        EntityRecord(entity_id=1, entity_kind=EntityKind.SceneObject),
    )

    accepted = relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update()]),
    )
    assert accepted == []


def test_reject_update_for_unknown_entity() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None

    accepted = relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update(entity_id=999)]),
    )
    assert accepted == []
    assert room.dirty_entity_ids == set()


# --- heartbeat semantics --------------------------------------------------


def test_heartbeat_extends_lease_without_overwriting_transform() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None

    # Prime the record with an initial keyframe so we can detect that
    # the heartbeat does not overwrite it.
    relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0,
            updates=[
                _sample_update(
                    pose_seq=1,
                    flags=StateFlags.KEYFRAME,
                    position=(1.0, 1.0, 1.0),
                )
            ],
        ),
    )

    pre_heartbeat_state = room.entities[1].last_accepted_state
    assert pre_heartbeat_state is not None
    assert pre_heartbeat_state.position == (1.0, 1.0, 1.0)

    accepted = relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0,
            updates=[
                _sample_update(
                    pose_seq=2,
                    flags=StateFlags.HEARTBEAT,
                    # Position that would differ if it were applied.
                    position=(99.0, 99.0, 99.0),
                )
            ],
        ),
    )

    assert len(accepted) == 1
    record = room.entities[1]
    # Transform unchanged...
    assert record.last_accepted_state is not None
    assert record.last_accepted_state.position == (1.0, 1.0, 1.0)
    # ...but pose_seq advanced and lease extended.
    assert record.pose_seq == 2
    assert record.lease_expire_at == 100.0 + LEASE_SEC


def test_heartbeat_with_keyframe_flag_still_writes_transform() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None

    relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0,
            updates=[
                _sample_update(
                    pose_seq=1,
                    flags=StateFlags.HEARTBEAT | StateFlags.KEYFRAME,
                    position=(7.0, 8.0, 9.0),
                )
            ],
        ),
    )

    record = room.entities[1]
    assert record.last_accepted_state is not None
    assert record.last_accepted_state.position == (7.0, 8.0, 9.0)


# --- flush --------------------------------------------------------------


def test_flush_empty_room_returns_none() -> None:
    relay, registry, _ = _make_relay()
    room = registry.get_or_create("lobby", "h")

    assert relay.flush_room(room) is None


def test_flush_coalesces_multiple_updates_per_entity_last_wins() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None

    # Two updates to the same entity within a single tick; the latest
    # wins when the flush collapses them into one per-entity update.
    relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0,
            updates=[_sample_update(pose_seq=1, position=(1.0, 0.0, 0.0))],
        ),
    )
    relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0,
            updates=[_sample_update(pose_seq=2, position=(2.0, 0.0, 0.0))],
        ),
    )

    batch = relay.flush_room(room)
    assert batch is not None
    assert len(batch.updates) == 1
    assert batch.updates[0].pose_seq == 2
    assert batch.updates[0].state.position == (2.0, 0.0, 0.0)


def test_flush_clears_dirty_set_and_advances_room_seq() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None
    assert room.next_room_seq == 1

    relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update()]),
    )
    assert room.dirty_entity_ids == {1}

    batch = relay.flush_room(room)
    assert batch is not None
    # room_seq on the wire == the sequence number assigned at publish;
    # next_room_seq has advanced past it.
    assert batch.room_seq == 1
    assert room.next_room_seq == 2
    assert room.dirty_entity_ids == set()

    # Subsequent flush with no new activity returns None.
    assert relay.flush_room(room) is None


def test_tick_broadcasts_all_non_empty_rooms() -> None:
    relay, registry, broadcast = _make_relay()
    _seed_owned_entity(registry)
    room_a = registry.get("lobby")
    assert room_a is not None

    # Second room with an owned entity and a dirty update.
    room_b = registry.get_or_create("lobby-2", "h")
    registry.upsert_entity(
        room_b,
        EntityRecord(
            entity_id=2,
            entity_kind=EntityKind.SceneObject,
            owner_client_no=7,
            authority_epoch=1,
        ),
    )

    relay.accept_state_batch(
        sender_client_no=5,
        room=room_a,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update()]),
    )
    relay.accept_state_batch(
        sender_client_no=7,
        room=room_b,
        batch=StateBatchMessage(room_seq=0, updates=[_sample_update(entity_id=2)]),
    )

    flushed = relay.tick()
    assert flushed == 2
    assert len(broadcast) == 2

    # Empty tick → no traffic, no error.
    flushed = relay.tick()
    assert flushed == 0
    assert len(broadcast) == 2


def test_flush_emits_keyframe_update_for_stored_state() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None

    relay.accept_state_batch(
        sender_client_no=5,
        room=room,
        batch=StateBatchMessage(
            room_seq=0,
            updates=[
                _sample_update(
                    pose_seq=1,
                    mask=ChangedMask.POSITION,
                    position=(4.0, 5.0, 6.0),
                )
            ],
        ),
    )

    # The record picked up the incoming transform even though only
    # POSITION was set in the mask; flush emits a full-component
    # keyframe for the stored state.
    batch = relay.flush_room(room)
    assert batch is not None
    update = batch.updates[0]
    assert update.flags == StateFlags.KEYFRAME
    # POSITION + ROTATION bits (no scale since record had no scale set).
    assert update.changed_mask == ChangedMask.POSITION | ChangedMask.ROTATION
    assert update.state.position == (4.0, 5.0, 6.0)


def test_flush_preserves_stored_scale_only_when_present() -> None:
    relay, registry, _ = _make_relay()
    _seed_owned_entity(registry)
    room = registry.get("lobby")
    assert room is not None
    # Seed with a prior TransformState carrying scale.
    room.entities[1].last_accepted_state = TransformState(
        position=(0.0, 0.0, 0.0),
        rotation=(0.0, 0.0, 0.0, 1.0),
        scale=(2.0, 2.0, 2.0),
    )
    room.entities[1].pose_seq = 5
    room.dirty_entity_ids.add(1)

    batch = relay.flush_room(room)
    assert batch is not None
    update = batch.updates[0]
    assert (
        update.changed_mask
        == ChangedMask.POSITION | ChangedMask.ROTATION | ChangedMask.SCALE
    )
    assert update.state.scale == (2.0, 2.0, 2.0)
