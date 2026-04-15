"""Room registry for the replication core.

Tracks rooms, their scene hash, the set of connected clients, and
per-entity authoritative records. This module is deliberately
transport-agnostic: it is driven by the ownership arbiter and state
relay rather than touching ZeroMQ directly.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .models import EntityKind, EntityRecord


class SceneHashMismatchError(RuntimeError):
    """Raised when a client joins a room with a different scene hash
    than the one the room was created with.

    Phase 1 treats scene hash as immutable per room lifetime; later
    phases may introduce re-keying on empty rooms.
    """


class DuplicateEntityError(RuntimeError):
    """Raised when attempting to insert an entity whose id already
    exists in the room but with a conflicting ``EntityKind``.
    """


@dataclass
class ClientState:
    """Minimal per-client bookkeeping.

    Later phases will add ownership lease tracking, last-seen pose
    sequence per entity, and flow-control state.
    """

    client_no: int
    connected_at: float = 0.0
    last_seen: float = 0.0


@dataclass
class RoomState:
    """Per-room state held by the registry."""

    room_id: str
    scene_hash: str
    next_room_seq: int = 1
    entities: dict[int, EntityRecord] = field(default_factory=dict)
    dirty_entity_ids: set[int] = field(default_factory=set)
    connected_clients: dict[int, ClientState] = field(default_factory=dict)


class RoomRegistry:
    """In-memory registry of active rooms.

    The registry is single-threaded by contract; callers funnel all
    mutations through the server's receive thread (same discipline as
    the existing ``NetSyncServer``).
    """

    def __init__(self) -> None:
        self._rooms: dict[str, RoomState] = {}

    def get_or_create(self, room_id: str, scene_hash: str) -> RoomState:
        """Return the room state for ``room_id``, creating it with
        ``scene_hash`` if it does not exist.

        Raises ``SceneHashMismatchError`` if the room already exists
        with a different scene hash.
        """
        room = self._rooms.get(room_id)
        if room is None:
            room = RoomState(room_id=room_id, scene_hash=scene_hash)
            self._rooms[room_id] = room
            return room
        if room.scene_hash != scene_hash:
            raise SceneHashMismatchError(
                f"room {room_id!r} expects scene_hash={room.scene_hash!r} "
                f"but client reported {scene_hash!r}"
            )
        return room

    def get(self, room_id: str) -> RoomState | None:
        """Return the room state or ``None`` if no such room exists."""
        return self._rooms.get(room_id)

    def remove(self, room_id: str) -> None:
        """Remove a room if present; no-op otherwise."""
        self._rooms.pop(room_id, None)

    # --- entity helpers -------------------------------------------------

    def upsert_entity(self, room: RoomState, record: EntityRecord) -> EntityRecord:
        """Insert ``record`` into ``room`` or return the existing one.

        If an entity with the same id but different ``entity_kind``
        already exists, ``DuplicateEntityError`` is raised.
        """
        existing = room.entities.get(record.entity_id)
        if existing is None:
            room.entities[record.entity_id] = record
            return record
        if existing.entity_kind != record.entity_kind:
            raise DuplicateEntityError(
                f"entity {record.entity_id} already registered in room "
                f"{room.room_id!r} as {existing.entity_kind.name}, cannot "
                f"re-register as {record.entity_kind.name}"
            )
        return existing

    def get_entity(self, room: RoomState, entity_id: int) -> EntityRecord | None:
        """Return the record for ``entity_id`` in ``room`` or ``None``."""
        return room.entities.get(entity_id)

    def add_dirty(self, room: RoomState, entity_id: int) -> None:
        """Mark ``entity_id`` as needing re-broadcast on the next tick."""
        room.dirty_entity_ids.add(entity_id)

    def pop_dirty(self, room: RoomState) -> set[int]:
        """Return and clear the current dirty entity set.

        Returns a new set so the caller can iterate without worrying
        about concurrent mutation via further ``add_dirty`` calls on
        the same room.
        """
        dirty = room.dirty_entity_ids
        room.dirty_entity_ids = set()
        return dirty


__all__ = [
    "ClientState",
    "DuplicateEntityError",
    "RoomRegistry",
    "RoomState",
    "SceneHashMismatchError",
    "EntityKind",
]
