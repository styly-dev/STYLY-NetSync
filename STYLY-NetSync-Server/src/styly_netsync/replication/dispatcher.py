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
    MessageCodec,
    TransformCodec,
    TransformCodecV1,
)
from .messages import (
    JoinRejectMessage,
    JoinRejectReason,
    OwnershipEventMessage,
    OwnershipResult,
)
from .ownership_arbiter import (
    OwnershipArbiter,
    OwnershipOutcome,
    OwnershipRequest,
)
from .room_registry import ClientState, RoomRegistry, SceneHashMismatchError
from .snapshot_service import SnapshotService

logger = logging.getLogger(__name__)


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

        now = self._clock()
        outcome = self._arbiter.handle_request(
            room,
            OwnershipRequest(
                entity_id=msg.entity_id,
                sender_client_no=msg.requester_short_id,
                expected_epoch=msg.expected_epoch,
                release=False,
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

        if outcome.result is not OwnershipResult.GRANTED:
            # Deny reason is now carried on the wire via reason_code;
            # log it here too for operator visibility.
            logger.info(
                "Ownership request not granted: room=%r entity=%d sender=%d "
                "result=%s reason=%s",
                room_id,
                outcome.entity_id,
                msg.requester_short_id,
                outcome.result.name,
                outcome.reason.name,
            )

        return DispatchResult(
            handled=True,
            accepted=outcome.result is OwnershipResult.GRANTED,
            ownership_result=outcome.result,
        )

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
