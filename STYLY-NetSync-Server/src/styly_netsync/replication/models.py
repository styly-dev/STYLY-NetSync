"""Domain models for the replication core.

These dataclasses hold the in-memory authoritative state of entities
owned by a room. They are mutated in place by the ownership arbiter
and state relay; they are intentionally not frozen.

See replication-protocol-v1 spec §5.3 for field definitions.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum


class EntityKind(IntEnum):
    """Kind discriminator for an entity record.

    Avatars are owned by exactly one connected client and vanish on
    disconnect. SceneObjects are authored in the Unity scene and
    persist across client sessions; ownership can change via the
    ownership arbiter.
    """

    Avatar = 1
    SceneObject = 2


@dataclass(slots=True)
class TransformState:
    """A full transform snapshot for an entity.

    ``position`` and ``rotation`` are always present. ``scale`` is
    optional; ``None`` means "scale not replicated for this entity".
    Rotation is stored as a unit quaternion (x, y, z, w).
    """

    position: tuple[float, float, float]
    rotation: tuple[float, float, float, float]
    scale: tuple[float, float, float] | None = None


@dataclass
class EntityRecord:
    """Authoritative server-side record for a single replicated entity.

    Fields mirror the spec §5.3 EntityRecord layout. All numeric
    fields default to zero so a freshly-minted record can be populated
    incrementally as the first authoritative state arrives.
    """

    entity_id: int
    entity_kind: EntityKind
    owner_client_no: int = 0
    authority_epoch: int = 0
    pose_seq: int = 0
    last_accepted_state: TransformState | None = None
    last_server_time: float = 0.0
    lease_expire_at: float = 0.0
    profile_id: int = 0


__all__ = ["EntityKind", "EntityRecord", "TransformState"]
