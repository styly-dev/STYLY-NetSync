"""Control-plane dispatcher for replication protocol v1.

The dispatcher owns control-plane messages (JOIN_ROOM, OWNERSHIP_*)
and will grow to cover the remaining replication messages
(RESYNC_*, STATE_BATCH) in later phases. It is deliberately
transport-agnostic: the caller provides ``send`` (unicast to one
client identity) and ``broadcast`` (fan-out to a room) callables so
the same code can drive real ZMQ sockets or unit-test sinks.
"""

from __future__ import annotations

import logging
import struct
import time
from collections.abc import Callable
from dataclasses import dataclass

from .message_codec import (
    MSG_REPL_JOIN_ROOM,
    MSG_REPL_OWNERSHIP_REQUEST,
    MSG_REPL_RESYNC_REQUEST,
    MSG_REPL_STATE_BATCH,
    MessageCodec,
    TransformCodec,
    TransformCodecV1,
)
from .messages import (
    JoinRejectMessage,
    JoinRejectReason,
    OwnershipEventMessage,
    OwnershipEventReasonCode,
    OwnershipResult,
    ResyncReplyMessage,
)
from .ownership_arbiter import (
    OwnershipArbiter,
    OwnershipOutcome,
    OwnershipRequest,
)
from .room_registry import ClientState, RoomRegistry, RoomState, SceneHashMismatchError
from .snapshot_service import SnapshotService
from .state_relay import StateRelay

logger = logging.getLogger(__name__)


# Default quiet-window after which a client is treated as disconnected
# and their owned entities are reclaimed via OWNERSHIP_EVENT(EXPIRED).
# Matches the legacy v3 pose-relay ``CLIENT_TIMEOUT`` default of 10 s.
DEFAULT_CLIENT_TIMEOUT_SEC: float = 10.0

# How far a client's last_applied_room_seq can lag the current
# ``next_room_seq - 1`` before we log a warning on RESYNC_REQUEST.
# Informational only — the reply is always a full current snapshot.
_RESYNC_GAP_WARN_THRESHOLD: int = 1000


SendFn = Callable[[bytes, bytes], None]
"""Callable: ``send(client_identity, frame_bytes) -> None``."""

BroadcastFn = Callable[[str, bytes], None]
"""Callable: ``broadcast(room_id, frame_bytes) -> None``.

Delivery semantics are chosen by the caller. For OWNERSHIP_EVENT the
server fans the frame out via ROUTER unicast to every connected client
identity in the room; PUB would also work but ROUTER-unicast matches
the legacy control-plane delivery discipline.
"""


@dataclass
class DispatchResult:
    """Outcome of a single :meth:`Dispatcher.handle_frame` call."""

    handled: bool
    accepted: bool = False
    reject_reason: JoinRejectReason | None = None
    ownership_result: OwnershipResult | None = None


class ReplicationDispatcher:
    """Route replication-protocol-v1 frames into room state mutations.

    The dispatcher is single-threaded by contract — the server calls
    it from the receive thread (message handling) and the periodic
    thread (lease sweep). Callers must not run these concurrently; the
    arbiter relies on that serialization.
    """

    def __init__(
        self,
        room_registry: RoomRegistry,
        snapshot_service: SnapshotService | None = None,
        transform_codec: TransformCodec | None = None,
        ownership_arbiter: OwnershipArbiter | None = None,
        state_relay: StateRelay | None = None,
        client_timeout_sec: float = DEFAULT_CLIENT_TIMEOUT_SEC,
        clock: Callable[[], float] = time.time,
    ) -> None:
        self._rooms = room_registry
        self._snapshots = (
            snapshot_service if snapshot_service is not None else SnapshotService()
        )
        self._codec = (
            transform_codec if transform_codec is not None else TransformCodecV1()
        )
        self._arbiter = (
            ownership_arbiter if ownership_arbiter is not None else OwnershipArbiter()
        )
        # The relay is optional so existing tests can construct the
        # dispatcher without a broadcast callback; a no-op broadcast is
        # used when the caller omits one. Real servers always pass
        # their own :class:`StateRelay` so the flush path emits frames.
        self._relay = state_relay
        self._client_timeout_sec = client_timeout_sec
        self._clock = clock

    def handle_frame(
        self,
        client_identity: bytes,
        room_id: str,
        frame: bytes,
        send: SendFn,
        broadcast: BroadcastFn,
    ) -> DispatchResult:
        """Dispatch a single control-plane frame.

        ``room_id`` is the room the frame was addressed to (from the
        transport envelope). JOIN_ROOM carries its own roomId on the
        wire and ignores the envelope value; OWNERSHIP_REQUEST relies
        on the envelope since its body only carries the entity id.

        Returns ``handled=False`` for message types outside the
        replication range or not yet implemented; the caller should
        log + drop those rather than error out.
        """
        msg_type = MessageCodec.peek_message_type(frame)
        if msg_type == MSG_REPL_JOIN_ROOM:
            return self._handle_join_room(client_identity, frame, send)
        if msg_type == MSG_REPL_OWNERSHIP_REQUEST:
            return self._handle_ownership_request(
                client_identity, room_id, frame, send, broadcast
            )
        if msg_type == MSG_REPL_STATE_BATCH:
            return self._handle_state_batch(client_identity, room_id, frame)
        if msg_type == MSG_REPL_RESYNC_REQUEST:
            return self._handle_resync_request(client_identity, room_id, frame, send)
        return DispatchResult(handled=False)

    # --- Periodic tasks --------------------------------------------------

    def sweep_expired_leases(self, broadcast: BroadcastFn) -> int:
        """Run the lease sweep across every registered room.

        Returns the total number of EXPIRED outcomes emitted. Intended
        to be called from the server's periodic thread at 2-5 Hz.
        """
        now = self._clock()
        total = 0
        for room_id, room in self._rooms.items():
            outcomes = self._arbiter.sweep_expired(room, now)
            for outcome in outcomes:
                frame = MessageCodec.encode_ownership_event(
                    self._outcome_to_event(outcome)
                )
                broadcast(room_id, frame)
                total += 1
        return total

    def flush_pending_batches(self) -> int:
        """Drain dirty entity sets into STATE_BATCH frames.

        Delegates to the configured :class:`StateRelay` which owns the
        broadcast callback. Returns the number of rooms actually
        flushed (0 if no relay is wired — tests can omit it).
        """
        if self._relay is None:
            return 0
        return self._relay.tick()

    def sweep_disconnected_clients(self, broadcast: BroadcastFn) -> int:
        """Evict clients that have gone silent and reclaim their entities.

        A client is disconnected when ``now - last_seen`` exceeds
        :attr:`_client_timeout_sec`. Each evicted client's owned
        entities have their ownership cleared (epoch bumped, lease
        zeroed) and an ``OWNERSHIP_EVENT(EXPIRED)`` is broadcast to
        the room so peers know to stop interpolating their poses.

        Returns the total number of EXPIRED events emitted across all
        rooms. Intended to be called from the server's periodic thread
        at the same cadence as the lease sweep (2-5 Hz).
        """
        now = self._clock()
        total = 0
        cutoff = now - self._client_timeout_sec
        for room_id, room in self._rooms.items():
            # Collect keys first so we can mutate the dict while iterating.
            stale_keys = [
                key
                for key, state in room.connected_clients.items()
                if state.last_seen < cutoff
            ]
            for key in stale_keys:
                state = room.connected_clients.pop(key)
                logger.info(
                    "Evicting disconnected client %d from room %r "
                    "(last_seen age=%.1fs)",
                    state.client_no,
                    room_id,
                    now - state.last_seen,
                )
                if state.client_no == 0:
                    # No short id was ever allocated (should not happen
                    # post-Task #12); nothing to reclaim.
                    continue
                for record in room.entities.values():
                    if record.owner_client_no != state.client_no:
                        continue
                    record.owner_client_no = 0
                    record.authority_epoch += 1
                    record.lease_expire_at = 0.0
                    event = OwnershipEventMessage(
                        entity_id=record.entity_id,
                        new_owner_short_id=0,
                        new_authority_epoch=record.authority_epoch,
                        result=OwnershipResult.EXPIRED,
                        reason_code=OwnershipEventReasonCode.LEASE_EXPIRED,
                    )
                    broadcast(room_id, MessageCodec.encode_ownership_event(event))
                    total += 1
        return total

    # --- JOIN_ROOM ------------------------------------------------------

    def _handle_join_room(
        self,
        client_identity: bytes,
        frame: bytes,
        send: SendFn,
    ) -> DispatchResult:
        try:
            msg = MessageCodec.decode_join_room(frame)
        except (ValueError, IndexError, struct.error) as exc:
            logger.warning("Malformed JOIN_ROOM from %r: %s", client_identity, exc)
            return DispatchResult(handled=True, accepted=False)

        try:
            room = self._rooms.get_or_create(msg.room_id, msg.scene_hash)
        except SceneHashMismatchError as exc:
            logger.info(
                "Rejecting JOIN_ROOM for room %r: scene hash mismatch (%s)",
                msg.room_id,
                exc,
            )
            self._send_reject(
                client_identity,
                msg.room_id,
                JoinRejectReason.SCENE_HASH_MISMATCH,
                str(exc),
                send,
            )
            return DispatchResult(
                handled=True,
                accepted=False,
                reject_reason=JoinRejectReason.SCENE_HASH_MISMATCH,
            )

        # Assign a per-room short id on first join; reuse it on rejoin
        # from the same ROUTER identity so ownership records that
        # reference the client's short id remain valid across a
        # reconnect. The replication plane owns its own counter inside
        # ``RoomState`` and does not touch the legacy v3 pose-relay
        # short-id space. Disconnect reclaim is deferred to Phase 5.
        now = self._clock()
        client_key = self._identity_to_key(client_identity)
        existing = room.connected_clients.get(client_key)
        if existing is None:
            client_no = room.next_client_no
            room.next_client_no += 1
            room.connected_clients[client_key] = ClientState(
                client_no=client_no,
                connected_at=now,
                last_seen=now,
                identity=client_identity,
            )
        else:
            existing.last_seen = now
            # Identity bytes can change on reconnect if the ZMQ_IDENTITY
            # changes, but the dict key is stable so we keep the same
            # short id. Refresh the cached identity in case a future
            # lookup keys off it.
            existing.identity = client_identity
            client_no = existing.client_no

        snapshot = self._snapshots.build_snapshot(room, your_client_no=client_no)
        frame_out = MessageCodec.encode_room_snapshot(snapshot, self._codec)
        send(client_identity, frame_out)
        logger.info(
            "Replication JOIN_ROOM accepted: room=%r client_no=%d "
            "identity=%r entities=%d connected=%d",
            msg.room_id,
            client_no,
            client_identity,
            len(room.entities),
            len(room.connected_clients),
        )
        return DispatchResult(handled=True, accepted=True)

    def _send_reject(
        self,
        client_identity: bytes,
        room_id: str,
        reason: JoinRejectReason,
        reason_text: str,
        send: SendFn,
    ) -> None:
        reject = JoinRejectMessage(
            room_id=room_id, reason=reason, reason_text=reason_text
        )
        send(client_identity, MessageCodec.encode_join_reject(reject))

    # --- OWNERSHIP_REQUEST ---------------------------------------------

    def _handle_ownership_request(
        self,
        client_identity: bytes,
        room_id: str,
        frame: bytes,
        send: SendFn,
        broadcast: BroadcastFn,
    ) -> DispatchResult:
        try:
            msg = MessageCodec.decode_ownership_request(frame)
        except (ValueError, IndexError, struct.error) as exc:
            logger.warning(
                "Malformed OWNERSHIP_REQUEST from %r: %s", client_identity, exc
            )
            return DispatchResult(handled=True, accepted=False)

        room = self._rooms.get(room_id)
        if room is None:
            logger.warning(
                "OWNERSHIP_REQUEST for unknown room %r (client %r)",
                room_id,
                client_identity,
            )
            return DispatchResult(handled=True, accepted=False)

        sender_client_no = self._client_no_for_identity(room, client_identity)
        if sender_client_no == 0:
            logger.warning(
                "OWNERSHIP_REQUEST from unknown identity %r in room %r",
                client_identity,
                room_id,
            )
            return DispatchResult(handled=True, accepted=False)

        now = self._clock()
        self._touch_last_seen(room, client_identity, now)

        # Use the server-resolved sender_client_no rather than the
        # wire-supplied requester_short_id to prevent spoofing.
        if sender_client_no != msg.requester_short_id:
            logger.warning(
                "OWNERSHIP_REQUEST client_no mismatch: identity resolved "
                "to %d but wire carries %d in room %r; using server value",
                sender_client_no,
                msg.requester_short_id,
                room_id,
            )

        outcome = self._arbiter.handle_request(
            room,
            OwnershipRequest(
                entity_id=msg.entity_id,
                sender_client_no=sender_client_no,
                expected_epoch=msg.expected_epoch,
                release=msg.release,
            ),
            now=now,
        )

        event_frame = MessageCodec.encode_ownership_event(
            self._outcome_to_event(outcome)
        )
        # Reply to the requester so they have an immediate acknowledgement,
        # then broadcast the authoritative result so every peer learns the
        # new owner / epoch at the same time. Duplicate delivery to the
        # requester is intentional — their OWNERSHIP_EVENT decoder is
        # idempotent on (entity_id, authority_epoch).
        send(client_identity, event_frame)
        broadcast(room_id, event_frame)

        if outcome.result is OwnershipResult.GRANTED:
            logger.info(
                "OWNERSHIP GRANTED: room=%r entity=0x%X owner=%d epoch=%d",
                room_id,
                outcome.entity_id,
                outcome.new_owner_client_no,
                outcome.new_authority_epoch,
            )
        else:
            logger.info(
                "OWNERSHIP DENIED: room=%r entity=0x%X sender=%d "
                "result=%s reason=%s current_owner=%d epoch=%d",
                room_id,
                outcome.entity_id,
                sender_client_no,
                outcome.result.name,
                outcome.reason.name,
                outcome.new_owner_client_no,
                outcome.new_authority_epoch,
            )

        return DispatchResult(
            handled=True,
            accepted=outcome.result is OwnershipResult.GRANTED,
            ownership_result=outcome.result,
        )

    # --- STATE_BATCH (inbound) -----------------------------------------

    def _handle_state_batch(
        self,
        client_identity: bytes,
        room_id: str,
        frame: bytes,
    ) -> DispatchResult:
        """Route an inbound STATE_BATCH from an owning client.

        STATE_BATCH is fire-and-forget: there is no synchronous reply
        on the wire. Accepted updates show up on the next flush via
        :meth:`flush_pending_batches`; rejected updates are log-only.
        """
        if self._relay is None:
            # No relay configured — defensively drop so tests that
            # construct the dispatcher without a broadcast channel stay
            # valid.
            return DispatchResult(handled=True, accepted=False)

        try:
            batch = MessageCodec.decode_state_batch(frame, self._codec)
        except (ValueError, IndexError, struct.error) as exc:
            logger.warning("Malformed STATE_BATCH from %r: %s", client_identity, exc)
            return DispatchResult(handled=True, accepted=False)

        room = self._rooms.get(room_id)
        if room is None:
            logger.warning(
                "STATE_BATCH for unknown room %r (client %r)",
                room_id,
                client_identity,
            )
            return DispatchResult(handled=True, accepted=False)

        sender_client_no = self._client_no_for_identity(room, client_identity)
        if sender_client_no == 0:
            logger.warning(
                "STATE_BATCH from unknown identity %r in room %r",
                client_identity,
                room_id,
            )
            return DispatchResult(handled=True, accepted=False)

        self._touch_last_seen(room, client_identity, self._clock())
        accepted = self._relay.accept_state_batch(sender_client_no, room, batch)
        n_updates = len(batch.updates)
        n_accepted = len(accepted)
        if n_updates > 0 and n_accepted < n_updates:
            # Log only when updates are dropped — successful flow is quiet.
            for u in batch.updates:
                rec = room.entities.get(u.entity_id)
                if rec is None:
                    logger.info(
                        "STATE_BATCH DROPPED: room=%r entity=0x%X "
                        "reason=unknown_entity",
                        room_id,
                        u.entity_id,
                    )
                elif rec.owner_client_no != sender_client_no:
                    logger.info(
                        "STATE_BATCH DROPPED: room=%r entity=0x%X "
                        "reason=not_owner (sender=%d, owner=%d)",
                        room_id,
                        u.entity_id,
                        sender_client_no,
                        rec.owner_client_no,
                    )
                elif u.authority_epoch != rec.authority_epoch:
                    logger.info(
                        "STATE_BATCH DROPPED: room=%r entity=0x%X "
                        "reason=epoch_mismatch (sent=%d, server=%d)",
                        room_id,
                        u.entity_id,
                        u.authority_epoch,
                        rec.authority_epoch,
                    )
        return DispatchResult(handled=True, accepted=bool(accepted))

    # --- RESYNC_REQUEST ------------------------------------------------

    def _handle_resync_request(
        self,
        client_identity: bytes,
        room_id: str,
        frame: bytes,
        send: SendFn,
    ) -> DispatchResult:
        """Respond to a client RESYNC_REQUEST with a targeted snapshot.

        The reply mirrors ``ROOM_SNAPSHOT`` in shape (sans
        ``your_client_no`` — the client already knows its short id
        from the initial join). ``last_applied_room_seq`` on the
        request is used only for gap telemetry today; a future
        revision could turn it into a delta-replay hint.
        """
        try:
            msg = MessageCodec.decode_resync_request(frame)
        except (ValueError, IndexError, struct.error) as exc:
            logger.warning("Malformed RESYNC_REQUEST from %r: %s", client_identity, exc)
            return DispatchResult(handled=True, accepted=False)

        room = self._rooms.get(room_id)
        if room is None:
            logger.warning(
                "RESYNC_REQUEST for unknown room %r (client %r)",
                room_id,
                client_identity,
            )
            return DispatchResult(handled=True, accepted=False)

        sender_client_no = self._client_no_for_identity(room, client_identity)
        if sender_client_no == 0:
            logger.warning(
                "RESYNC_REQUEST from unknown identity %r in room %r",
                client_identity,
                room_id,
            )
            return DispatchResult(handled=True, accepted=False)

        self._touch_last_seen(room, client_identity, self._clock())

        # Gap telemetry. The room's current sequence is
        # ``next_room_seq - 1`` (matches the SnapshotService baseline);
        # anything more than the configured window behind is suspicious
        # but not fatal — we still reply with the current state.
        current_seq = max(room.next_room_seq - 1, 0)
        if (
            msg.last_applied_room_seq > 0
            and current_seq > msg.last_applied_room_seq + _RESYNC_GAP_WARN_THRESHOLD
        ):
            logger.warning(
                "Client %r in room %r is %d batches behind current "
                "(last_applied=%d, current=%d); replying with full snapshot",
                client_identity,
                room_id,
                current_seq - msg.last_applied_room_seq,
                msg.last_applied_room_seq,
                current_seq,
            )

        snapshot = self._snapshots.build_snapshot(room)
        reply = ResyncReplyMessage(
            room_id=snapshot.room_id,
            base_room_seq=snapshot.base_room_seq,
            server_time_us=snapshot.server_time_us,
            entities=snapshot.entities,
        )
        send(client_identity, MessageCodec.encode_resync_reply(reply, self._codec))
        return DispatchResult(handled=True, accepted=True)

    def _touch_last_seen(
        self, room: RoomState, client_identity: bytes, now: float
    ) -> None:
        """Bump ``last_seen`` on every inbound frame from ``client_identity``.

        No-op for unknown identities; the disconnect sweep treats those
        as already-evicted.
        """
        key = self._identity_to_key(client_identity)
        state = room.connected_clients.get(key)
        if state is None:
            return
        state.last_seen = now

    def _client_no_for_identity(self, room: RoomState, client_identity: bytes) -> int:
        """Resolve a ROUTER identity to its room-local short id.

        Returns 0 when the identity is not registered against the room
        — the caller treats that as an invalid sender and drops the
        frame.
        """
        key = self._identity_to_key(client_identity)
        state = room.connected_clients.get(key)
        if state is None:
            return 0
        return state.client_no

    @staticmethod
    def _outcome_to_event(outcome: OwnershipOutcome) -> OwnershipEventMessage:
        """Map an arbiter :class:`OwnershipOutcome` to the wire event.

        The wire protocol now carries both :class:`OwnershipResult` and
        :class:`OwnershipEventReasonCode` directly, so this mapping is a
        straight field copy.
        """
        return OwnershipEventMessage(
            entity_id=outcome.entity_id,
            new_owner_short_id=outcome.new_owner_client_no,
            new_authority_epoch=outcome.new_authority_epoch,
            result=outcome.result,
            reason_code=outcome.reason,
        )

    @staticmethod
    def _identity_to_key(client_identity: bytes) -> int:
        """Map a ROUTER identity to the ``connected_clients`` dict key.

        Python's built-in hash is deterministic within a single
        interpreter run (with PYTHONHASHSEED stabilization for tests)
        which is all we need for Phase 2/3 book-keeping.
        """
        return hash(client_identity)


__all__ = ["BroadcastFn", "DispatchResult", "ReplicationDispatcher", "SendFn"]
