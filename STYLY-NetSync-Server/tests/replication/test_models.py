"""Unit tests for replication.models."""

from __future__ import annotations

from styly_netsync.replication.models import (
    EntityKind,
    EntityRecord,
    TransformState,
)


def test_entity_record_defaults() -> None:
    record = EntityRecord(entity_id=42, entity_kind=EntityKind.SceneObject)

    assert record.entity_id == 42
    assert record.entity_kind is EntityKind.SceneObject
    assert record.owner_client_no == 0
    assert record.authority_epoch == 0
    assert record.pose_seq == 0
    assert record.last_accepted_state is None
    assert record.last_server_time == 0.0
    assert record.lease_expire_at == 0.0
    assert record.profile_id == 0


def test_entity_record_mutable_in_place() -> None:
    record = EntityRecord(entity_id=1, entity_kind=EntityKind.Avatar)
    state = TransformState(
        position=(1.0, 2.0, 3.0),
        rotation=(0.0, 0.0, 0.0, 1.0),
        scale=None,
    )

    record.owner_client_no = 7
    record.authority_epoch = 3
    record.pose_seq = 12
    record.last_accepted_state = state

    assert record.owner_client_no == 7
    assert record.authority_epoch == 3
    assert record.pose_seq == 12
    assert record.last_accepted_state is state


def test_transform_state_optional_scale() -> None:
    without_scale = TransformState(
        position=(0.0, 0.0, 0.0),
        rotation=(0.0, 0.0, 0.0, 1.0),
    )
    with_scale = TransformState(
        position=(0.0, 0.0, 0.0),
        rotation=(0.0, 0.0, 0.0, 1.0),
        scale=(2.0, 2.0, 2.0),
    )

    assert without_scale.scale is None
    assert with_scale.scale == (2.0, 2.0, 2.0)


def test_entity_kind_values_are_stable() -> None:
    # Wire format pins these integer values; guard against reordering.
    assert int(EntityKind.Avatar) == 1
    assert int(EntityKind.SceneObject) == 2
