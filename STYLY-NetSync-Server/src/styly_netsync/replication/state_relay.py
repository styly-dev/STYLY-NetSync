"""State relay for replication protocol v1.

Owns the authoritative pose pipeline for replicated entities:

1. ``accept_state_batch`` validates incoming ``STATE_BATCH`` frames
   against the current owner / authority epoch / pose seq, merges
   accepted transforms into each :class:`EntityRecord`, and marks the
   entity dirty.
2. ``tick`` / ``flush_room`` drain the dirty sets at a fixed cadence
   (~30 Hz by default), build a per-room ``STATE_BATCH`` carrying the
   latest state of each touched entity, and fan it out via the
   dispatcher's broadcast channel.

Single-threaded by contract: every entry point runs under the
dispatcher's lock. The relay is stateless between ticks aside from
what lives on ``RoomState.dirty_entity_ids`` and ``EntityRecord``.
"""

from __future__ import annotations

import logging
import time
from collections.abc import Callable

from .message_codec import MessageCodec, TransformCodec, TransformCodecV1
from .messages import (
    ChangedMask,
    StateBatchMessage,
    StateFlags,
    StateUpdate,
    WireTransform,
)
from .models import EntityRecord, TransformState
from .ownership_arbiter import DEFAULT_LEASE_SEC
from .room_registry import RoomRegistry, RoomState
from .snapshot_service import _wire_transform_from_state

logger = logging.getLogger(__name__)

# Default flush cadence for :meth:`StateRelay.tick`. 33 ms ≈ 30 Hz —
# well above perceptual thresholds for XR pose playback and cheap
# relative to the server's other periodic work.
DEFAULT_FLUSH_INTERVAL_SEC: float = 0.033


BroadcastFn = Callable[[str, bytes], None]


def _now_us() -> int:
    """Server wall-clock timestamp in microseconds since the Unix epoch."""
    return time.time_ns() // 1000


def _apply_changed_mask(
    existing: TransformState | None,
    wire: WireTransform,
    mask: ChangedMask,
) -> TransformState:
    """Merge a wire-level update into an authoritative :class:`TransformState`.

    Fields absent from ``mask`` are carried forward from ``existing``;
    an unset scale is represented as ``None`` so snapshot builders can
    omit the SCALE bit on the wire. The first update for an entity is
    treated as a keyframe: missing components default to
    ``WireTransform`` defaults, matching what a client would see on a
    fresh snapshot.
    """
    if existing is None:
        base_position = (0.0, 0.0, 0.0)
        base_rotation = (0.0, 0.0, 0.0, 1.0)
        base_scale: tuple[float, float, float] | None = None
    else:
        base_position = existing.position
        base_rotation = existing.rotation
        base_scale = existing.scale

    position = wire.position if mask & ChangedMask.POSITION else base_position
    rotation = wire.rotation if mask & ChangedMask.ROTATION else base_rotation
    if mask & ChangedMask.SCALE:
        scale: tuple[float, float, float] | None = wire.scale
    else:
        scale = base_scale

    return TransformState(position=position, rotation=rotation, scale=scale)


class StateRelay:
    """Authoritative state pipeline + batch publisher."""

    def __init__(
        self,
        room_registry: RoomRegistry,
        broadcast: BroadcastFn,
        transform_codec: TransformCodec | None = None,
        flush_interval_sec: float = DEFAULT_FLUSH_INTERVAL_SEC,
        lease_sec: float = DEFAULT_LEASE_SEC,
        clock: Callable[[], float] = time.time,
        time_source_us: Callable[[], int] = _now_us,
    ) -> None:
        self._rooms = room_registry
        self._broadcast = broadcast
        self._codec = (
            transform_codec if transform_codec is not None else TransformCodecV1()
        )
        self._flush_interval_sec = flush_interval_sec
        self._lease_sec = lease_sec
        self._clock = clock
        self._time_source_us = time_source_us

    @property
    def flush_interval_sec(self) -> float:
        """Target cadence for :meth:`tick`; exposed for the server loop."""
        return self._flush_interval_sec

    # --- Ingress --------------------------------------------------------

    def accept_state_batch(
        self,
        sender_client_no: int,
        room: RoomState,
        batch: StateBatchMessage,
    ) -> list[StateUpdate]:
        """Validate and apply the updates in ``batch`` against ``room``.

        Returns the list of updates that passed all checks. Callers use
        it for structured logging + tests; there is no failure channel
        back to the client — drops are reported via metrics/logs only.
        """
        now = self._clock()
        accepted: list[StateUpdate] = []
        for update in batch.updates:
            record = room.entities.get(update.entity_id)
            if record is None:
                logger.debug(
                    "Dropping state update for unknown entity %d in %r",
                    update.entity_id,
                    room.room_id,
                )
                continue
            if (
                record.owner_client_no == 0
                or record.owner_client_no != sender_client_no
            ):
                logger.debug(
                    "Dropping state update for entity %d: sender=%d is not owner=%d",
                    update.entity_id,
                    sender_client_no,
                    record.owner_client_no,
                )
                continue
            if update.authority_epoch != record.authority_epoch:
                logger.debug(
                    "Dropping state update for entity %d: epoch %d != %d",
                    update.entity_id,
                    update.authority_epoch,
                    record.authority_epoch,
                )
                continue
            if update.pose_seq <= record.pose_seq:
                logger.debug(
                    "Dropping stale state update for entity %d: seq %d <= %d",
                    update.entity_id,
                    update.pose_seq,
                    record.pose_seq,
                )
                continue

            self._apply_update(record, update, now)
            room.dirty_entity_ids.add(update.entity_id)
            accepted.append(update)
        return accepted

    @staticmethod
    def _is_heartbeat_only(flags: StateFlags) -> bool:
        """A heartbeat-only update extends the lease without rewriting state.

        Keyframe or Teleport flags override the heartbeat semantic per
        spec §10.5 — those flags guarantee the transform is meaningful
        and must be applied.
        """
        if not (flags & StateFlags.HEARTBEAT):
            return False
        if flags & (StateFlags.KEYFRAME | StateFlags.TELEPORT):
            return False
        return True

    def _apply_update(
        self,
        record: EntityRecord,
        update: StateUpdate,
        now: float,
    ) -> None:
        """Mutate ``record`` in place with an accepted update."""
        if self._is_heartbeat_only(update.flags):
            # Heartbeat: advance pose_seq + extend the lease but leave
            # the stored transform alone. Dirty-marking still happens
            # at the caller so clients learn the new pose_seq.
            record.pose_seq = update.pose_seq
            record.last_server_time = now
            record.lease_expire_at = now + self._lease_sec
            return

        record.last_accepted_state = _apply_changed_mask(
            record.last_accepted_state, update.state, update.changed_mask
        )
        record.pose_seq = update.pose_seq
        record.last_server_time = now
        record.lease_expire_at = now + self._lease_sec

    # --- Egress ---------------------------------------------------------

    def flush_room(self, room: RoomState) -> StateBatchMessage | None:
        """Build one coalesced STATE_BATCH for ``room`` and clear dirty set.

        Returns ``None`` when the dirty set is empty so the caller can
        skip the broadcast entirely. Coalescing is implicit: each entity
        appears at most once because we key off the dirty set, and the
        emitted :class:`StateUpdate` carries that entity's *latest*
        accepted state (last-wins across the tick).
        """
        if not room.dirty_entity_ids:
            return None

        updates: list[StateUpdate] = []
        for entity_id in sorted(room.dirty_entity_ids):
            record = room.entities.get(entity_id)
            if record is None:
                # Entity evicted between accept and flush — skip.
                continue
            updates.append(self._build_update(record))

        room.dirty_entity_ids = set()

        if not updates:
            # All dirty entities were evicted; nothing to broadcast.
            return None

        # room_seq on the wire is the latest **published** sequence; we
        # consume RoomState.next_room_seq and advance the counter.
        room_seq = room.next_room_seq
        room.next_room_seq += 1
        return StateBatchMessage(
            room_seq=room_seq,
            server_time_us=self._time_source_us(),
            updates=updates,
        )

    @staticmethod
    def _build_update(record: EntityRecord) -> StateUpdate:
        """Serialize ``record`` as a wire-level :class:`StateUpdate`.

        The flush path is a keyframe for the client: every accepted
        component is marked present so receivers can reconstruct the
        full state without a prior snapshot. A record with no accepted
        state yet (e.g. ownership granted but no pose received) carries
        ``ChangedMask.NONE`` and default transform values — clients
        should treat that as "no-op, acknowledge ownership only".
        """
        if record.last_accepted_state is None:
            return StateUpdate(
                entity_id=record.entity_id,
                authority_epoch=record.authority_epoch,
                pose_seq=record.pose_seq,
                flags=StateFlags.KEYFRAME,
                changed_mask=ChangedMask.NONE,
                state=WireTransform(),
            )
        transform, mask = _wire_transform_from_state(record.last_accepted_state)
        return StateUpdate(
            entity_id=record.entity_id,
            authority_epoch=record.authority_epoch,
            pose_seq=record.pose_seq,
            flags=StateFlags.KEYFRAME,
            changed_mask=mask,
            state=transform,
        )

    def tick(self) -> int:
        """Flush every registered room and broadcast any non-empty batch.

        Returns the number of rooms for which a batch was actually
        broadcast (useful for metrics / tests). Rooms with an empty
        dirty set are skipped entirely — no empty frames on the wire.
        """
        flushed = 0
        for room_id, room in self._rooms.items():
            batch = self.flush_room(room)
            if batch is None:
                continue
            frame = MessageCodec.encode_state_batch(batch, self._codec)
            self._broadcast(room_id, frame)
            flushed += 1
        return flushed


__all__ = [
    "BroadcastFn",
    "DEFAULT_FLUSH_INTERVAL_SEC",
    "StateRelay",
]
