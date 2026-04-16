"""Unit tests for :class:`OwnershipArbiter`."""

from __future__ import annotations

from styly_netsync.replication.messages import (
    OwnershipEventReasonCode,
    OwnershipResult,
)
from styly_netsync.replication.models import EntityKind, EntityRecord
from styly_netsync.replication.ownership_arbiter import (
    OwnershipArbiter,
    OwnershipRequest,
)
from styly_netsync.replication.room_registry import RoomRegistry, RoomState

LEASE_SEC = 2.0


def _fresh_room_with_entity(
    entity_id: int = 1, kind: EntityKind = EntityKind.SceneObject
) -> RoomState:
    registry = RoomRegistry()
    room = registry.get_or_create("room", "hash")
    registry.upsert_entity(room, EntityRecord(entity_id=entity_id, entity_kind=kind))
    return room


def test_acquire_unowned_grants_and_bumps_epoch() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )

    assert outcome.result is OwnershipResult.GRANTED
    assert outcome.reason is OwnershipEventReasonCode.NONE
    assert outcome.new_owner_client_no == 5
    assert outcome.new_authority_epoch == 1

    record = room.entities[1]
    assert record.owner_client_no == 5
    assert record.authority_epoch == 1
    assert record.lease_expire_at == 100.0 + LEASE_SEC


def test_acquire_owned_by_other_denies_with_already_owned() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()
    arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=9, expected_epoch=0),
        now=100.1,
    )

    assert outcome.result is OwnershipResult.DENIED
    assert outcome.reason is OwnershipEventReasonCode.ALREADY_OWNED
    # Epoch unchanged — still owned by 5.
    assert outcome.new_owner_client_no == 5
    assert outcome.new_authority_epoch == 1
    assert room.entities[1].authority_epoch == 1


def test_acquire_by_existing_owner_refreshes_lease_without_epoch_bump() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()
    arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=1),
        now=101.5,
    )

    assert outcome.result is OwnershipResult.GRANTED
    assert outcome.new_authority_epoch == 1  # unchanged
    assert room.entities[1].lease_expire_at == 101.5 + LEASE_SEC


def test_release_non_owner_denies_with_not_owner() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()
    arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(
            entity_id=1, sender_client_no=9, expected_epoch=1, release=True
        ),
        now=100.1,
    )

    assert outcome.result is OwnershipResult.DENIED
    assert outcome.reason is OwnershipEventReasonCode.NOT_OWNER
    assert room.entities[1].owner_client_no == 5
    assert room.entities[1].authority_epoch == 1


def test_release_wrong_epoch_denies_with_epoch_mismatch() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()
    arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(
            entity_id=1, sender_client_no=5, expected_epoch=99, release=True
        ),
        now=100.1,
    )

    assert outcome.result is OwnershipResult.DENIED
    assert outcome.reason is OwnershipEventReasonCode.EPOCH_MISMATCH
    assert room.entities[1].owner_client_no == 5
    assert room.entities[1].authority_epoch == 1


def test_release_success_clears_owner_and_bumps_epoch() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()
    arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(
            entity_id=1, sender_client_no=5, expected_epoch=1, release=True
        ),
        now=100.1,
    )

    assert outcome.result is OwnershipResult.RELEASED
    assert outcome.reason is OwnershipEventReasonCode.NONE
    assert outcome.new_owner_client_no == 0
    assert outcome.new_authority_epoch == 2

    record = room.entities[1]
    assert record.owner_client_no == 0
    assert record.authority_epoch == 2
    assert record.lease_expire_at == 0.0


def test_sweep_expired_emits_expired_event_and_clears_owner() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()
    arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )
    # Entity owned with lease expiring at 100 + 2 = 102.

    before_expiry = arbiter.sweep_expired(room, now=101.9)
    assert before_expiry == []
    assert room.entities[1].owner_client_no == 5

    expired = arbiter.sweep_expired(room, now=102.0)
    assert len(expired) == 1
    outcome = expired[0]
    assert outcome.result is OwnershipResult.EXPIRED
    # Sweep uses NONE; LEASE_EXPIRED is reserved for client-initiated denies.
    assert outcome.reason is OwnershipEventReasonCode.NONE
    assert outcome.entity_id == 1
    assert outcome.new_owner_client_no == 0
    assert outcome.new_authority_epoch == 2

    record = room.entities[1]
    assert record.owner_client_no == 0
    assert record.authority_epoch == 2
    assert record.lease_expire_at == 0.0


def test_sweep_ignores_unowned_entities() -> None:
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()

    assert arbiter.sweep_expired(room, now=10_000.0) == []


def test_unknown_entity_auto_registers_on_acquire() -> None:
    """First acquire for an unknown entity auto-creates a SceneObject record."""
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    registry = RoomRegistry()
    room = registry.get_or_create("room", "hash")

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=999, sender_client_no=1, expected_epoch=0),
        now=1.0,
    )

    assert outcome.result is OwnershipResult.GRANTED
    assert outcome.new_owner_client_no == 1
    assert outcome.entity_id == 999
    # The entity should now exist in the room.
    assert 999 in room.entities


def test_unknown_entity_release_returns_hard_deny() -> None:
    """Release for an unknown entity is still denied."""
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    registry = RoomRegistry()
    room = registry.get_or_create("room", "hash")

    outcome = arbiter.handle_request(
        room,
        OwnershipRequest(
            entity_id=999, sender_client_no=1, expected_epoch=0, release=True
        ),
        now=1.0,
    )

    assert outcome.result is OwnershipResult.DENIED
    assert outcome.reason is OwnershipEventReasonCode.NOT_OWNER
    assert outcome.entity_id == 999


def test_serial_acquire_ordering_first_wins() -> None:
    """Two acquires in sequence: the first grants, the second is denied.

    The dispatcher guarantees serial execution on a single thread, so
    the arbiter relies on ``dict`` ordering of visible state at each
    call. This test documents that guarantee.
    """
    arbiter = OwnershipArbiter(lease_sec=LEASE_SEC)
    room = _fresh_room_with_entity()

    first = arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=5, expected_epoch=0),
        now=100.0,
    )
    second = arbiter.handle_request(
        room,
        OwnershipRequest(entity_id=1, sender_client_no=6, expected_epoch=0),
        now=100.0,
    )

    assert first.result is OwnershipResult.GRANTED
    assert second.result is OwnershipResult.DENIED
    assert second.reason is OwnershipEventReasonCode.ALREADY_OWNED
