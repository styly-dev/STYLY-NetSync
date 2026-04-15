"""Control-plane dispatcher for replication protocol v1.

The dispatcher owns the JOIN_ROOM handling path and will grow to cover
the remaining replication messages (OWNERSHIP_*, RESYNC_*, STATE_BATCH)
in later phases. It is deliberately transport-agnostic: the caller
provides a ``send`` callable that pushes an already-encoded frame to a
specific client identity, so the same code can drive a real
ROUTER socket or a unit-test in-memory sink.
"""

from __future__ import annotations

import logging
import time
from collections.abc import Callable
from dataclasses import dataclass

from .message_codec import (
    MSG_REPL_JOIN_ROOM,
    MessageCodec,
    TransformCodec,
    TransformCodecV1,
)
from .messages import JoinRejectMessage, JoinRejectReason
from .room_registry import ClientState, RoomRegistry, SceneHashMismatchError
from .snapshot_service import SnapshotService

logger = logging.getLogger(__name__)


# Sentinel reserved for clients we have not yet issued a short id to.
# The ownership arbiter will populate this in a later phase.
_PENDING_CLIENT_NO = 0


SendFn = Callable[[bytes, bytes], None]
"""Callable: ``send(client_identity, frame_bytes) -> None``."""


@dataclass
class DispatchResult:
    """Outcome of a single :meth:`Dispatcher.handle_frame` call.

    The server uses this for metrics / logging; it is intentionally
    decoupled from the actual reply bytes (those are pushed through the
    ``send`` callable).
    """

    handled: bool
    accepted: bool = False
    reject_reason: JoinRejectReason | None = None


class ReplicationDispatcher:
    """Route replication-protocol-v1 frames into room state mutations.

    Phase 2 handles only JOIN_ROOM; unknown / not-yet-implemented
    replication message types are reported as unhandled so the caller
    can log + drop without crashing.
    """

    def __init__(
        self,
        room_registry: RoomRegistry,
        snapshot_service: SnapshotService | None = None,
        transform_codec: TransformCodec | None = None,
        clock: Callable[[], float] = time.time,
    ) -> None:
        self._rooms = room_registry
        self._snapshots = (
            snapshot_service if snapshot_service is not None else SnapshotService()
        )
        self._codec = (
            transform_codec if transform_codec is not None else TransformCodecV1()
        )
        self._clock = clock

    def handle_frame(
        self,
        client_identity: bytes,
        frame: bytes,
        send: SendFn,
    ) -> DispatchResult:
        """Dispatch a single control-plane frame.

        Returns ``handled=False`` for message types outside the
        replication range or not yet implemented; the caller should
        treat those as no-ops (or log + drop) rather than errors.
        """
        msg_type = MessageCodec.peek_message_type(frame)
        if msg_type == MSG_REPL_JOIN_ROOM:
            return self._handle_join_room(client_identity, frame, send)
        return DispatchResult(handled=False)

    # --- JOIN_ROOM ------------------------------------------------------

    def _handle_join_room(
        self,
        client_identity: bytes,
        frame: bytes,
        send: SendFn,
    ) -> DispatchResult:
        try:
            msg = MessageCodec.decode_join_room(frame)
        except (ValueError, IndexError) as exc:
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

        # Phase 2: we don't yet assign short ids here; the ownership
        # arbiter will populate ClientState.client_no when it lands.
        # For now we key by identity bytes via a placeholder entry so
        # the room knows the client is present.
        now = self._clock()
        client_key = self._identity_to_key(client_identity)
        existing = room.connected_clients.get(client_key)
        if existing is None:
            room.connected_clients[client_key] = ClientState(
                client_no=_PENDING_CLIENT_NO,
                connected_at=now,
                last_seen=now,
            )
        else:
            existing.last_seen = now

        snapshot = self._snapshots.build_snapshot(room)
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

    @staticmethod
    def _identity_to_key(client_identity: bytes) -> int:
        """Map a ROUTER identity to the ``connected_clients`` dict key.

        ``ClientState`` keys are ``int`` per the Phase 1 type contract;
        until the ownership arbiter assigns short ids we use a hash of
        the identity bytes as a stable pseudo-key. Collisions between
        distinct identities would be a bug the arbiter must avoid, but
        in Phase 2 there is exactly one pending entry per identity.
        """
        # Python's built-in hash is deterministic within a single
        # interpreter run (with PYTHONHASHSEED stabilization for tests)
        # which is all we need for Phase 2 book-keeping.
        return hash(client_identity)


__all__ = ["DispatchResult", "ReplicationDispatcher", "SendFn"]
