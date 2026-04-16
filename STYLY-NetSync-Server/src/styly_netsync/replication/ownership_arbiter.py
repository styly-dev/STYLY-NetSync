"""Ownership arbiter for the replication core.

The arbiter is the single serialization point for authority decisions
on replicated entities. It is driven by the dispatcher on the receive
thread (acquire / release) and by the periodic thread (lease sweep);
both callers must hold the dispatcher's single-thread contract so the
arbiter itself is lock-free.

Outputs are returned as :class:`OwnershipOutcome` values — a domain
object the dispatcher converts to the wire-level
:class:`OwnershipEventMessage` at the transport boundary. Keeping the
arbiter's return type independent of the wire format lets the codec
evolve (reason-code additions, field renames) without churning the
authority logic.
"""

from __future__ import annotations

from dataclasses import dataclass

from .messages import OwnershipEventReasonCode, OwnershipResult
from .models import EntityKind, EntityRecord
from .room_registry import RoomState

# TODO: plumb from replication profile (§5.4). Hardcoded default for
# Phase 3 matches the spec's "v1 recommended default" of 2 s.
DEFAULT_LEASE_SEC: float = 2.0


@dataclass
class OwnershipOutcome:
    """Single decision produced by the arbiter.

    ``new_owner_client_no`` and ``new_authority_epoch`` reflect the
    entity's state **after** the decision; for ``DENIED`` outcomes the
    values are unchanged (current owner / epoch).
    """

    entity_id: int
    new_owner_client_no: int
    new_authority_epoch: int
    result: OwnershipResult
    reason: OwnershipEventReasonCode = OwnershipEventReasonCode.NONE


@dataclass
class OwnershipRequest:
    """Intent for :meth:`OwnershipArbiter.handle_request`.

    ``release`` distinguishes release from acquire. The dispatcher
    builds these from :class:`OwnershipRequestMessage`; the arbiter
    does not know about the wire format.
    """

    entity_id: int
    sender_client_no: int
    expected_epoch: int
    release: bool = False


class OwnershipArbiter:
    """Serial decision-maker for entity ownership.

    Not thread-safe by contract — the caller (dispatcher + periodic
    sweeper) must run all methods on a single thread.
    """

    def __init__(self, lease_sec: float = DEFAULT_LEASE_SEC) -> None:
        self._lease_sec = lease_sec

    # --- Request handling ----------------------------------------------

    def handle_request(
        self,
        room: RoomState,
        request: OwnershipRequest,
        now: float,
    ) -> OwnershipOutcome:
        """Apply an acquire or release request against ``room``."""
        record = room.entities.get(request.entity_id)
        if record is None:
            if request.release:
                # Cannot release an entity the server has never seen.
                return OwnershipOutcome(
                    entity_id=request.entity_id,
                    new_owner_client_no=0,
                    new_authority_epoch=0,
                    result=OwnershipResult.DENIED,
                    reason=OwnershipEventReasonCode.NOT_OWNER,
                )
            # Auto-register the entity as a SceneObject on first acquire.
            # Clients declare entities in their scene; the server learns
            # about them on the first ownership request.
            record = EntityRecord(
                entity_id=request.entity_id,
                entity_kind=EntityKind.SceneObject,
            )
            room.entities[request.entity_id] = record

        if request.release:
            return self._release(record, request)
        return self._acquire(record, request, now)

    def _acquire(
        self,
        record: EntityRecord,
        request: OwnershipRequest,
        now: float,
    ) -> OwnershipOutcome:
        if (
            record.owner_client_no != 0
            and record.owner_client_no != request.sender_client_no
        ):
            return OwnershipOutcome(
                entity_id=record.entity_id,
                new_owner_client_no=record.owner_client_no,
                new_authority_epoch=record.authority_epoch,
                result=OwnershipResult.DENIED,
                reason=OwnershipEventReasonCode.ALREADY_OWNED,
            )

        # If the sender already owns the entity, treat the request as a
        # lease refresh: epoch unchanged, lease extended, result still
        # GRANTED so the client sees confirmation.
        if record.owner_client_no != request.sender_client_no:
            record.authority_epoch += 1
        record.owner_client_no = request.sender_client_no
        record.lease_expire_at = now + self._lease_sec
        return OwnershipOutcome(
            entity_id=record.entity_id,
            new_owner_client_no=record.owner_client_no,
            new_authority_epoch=record.authority_epoch,
            result=OwnershipResult.GRANTED,
            reason=OwnershipEventReasonCode.NONE,
        )

    def _release(
        self,
        record: EntityRecord,
        request: OwnershipRequest,
    ) -> OwnershipOutcome:
        if record.owner_client_no != request.sender_client_no:
            return OwnershipOutcome(
                entity_id=record.entity_id,
                new_owner_client_no=record.owner_client_no,
                new_authority_epoch=record.authority_epoch,
                result=OwnershipResult.DENIED,
                reason=OwnershipEventReasonCode.NOT_OWNER,
            )
        if record.authority_epoch != request.expected_epoch:
            return OwnershipOutcome(
                entity_id=record.entity_id,
                new_owner_client_no=record.owner_client_no,
                new_authority_epoch=record.authority_epoch,
                result=OwnershipResult.DENIED,
                reason=OwnershipEventReasonCode.EPOCH_MISMATCH,
            )

        record.owner_client_no = 0
        record.authority_epoch += 1
        record.lease_expire_at = 0.0
        return OwnershipOutcome(
            entity_id=record.entity_id,
            new_owner_client_no=0,
            new_authority_epoch=record.authority_epoch,
            result=OwnershipResult.RELEASED,
            reason=OwnershipEventReasonCode.NONE,
        )

    # --- Lease sweep ---------------------------------------------------

    def sweep_expired(self, room: RoomState, now: float) -> list[OwnershipOutcome]:
        """Expire stale leases and emit an EXPIRED outcome per revoked entity.

        Called periodically (2-5 Hz is fine). Entities with
        ``owner_client_no == 0`` or ``lease_expire_at == 0.0`` are
        treated as un-leased and skipped.
        """
        expired: list[OwnershipOutcome] = []
        for record in room.entities.values():
            if record.owner_client_no == 0:
                continue
            if record.lease_expire_at == 0.0:
                continue
            if now < record.lease_expire_at:
                continue

            record.owner_client_no = 0
            record.authority_epoch += 1
            record.lease_expire_at = 0.0
            expired.append(
                OwnershipOutcome(
                    entity_id=record.entity_id,
                    new_owner_client_no=0,
                    new_authority_epoch=record.authority_epoch,
                    result=OwnershipResult.EXPIRED,
                    # Sweep uses NONE — EXPIRED already conveys the cause.
                    # LEASE_EXPIRED is reserved for client-initiated deny paths.
                    reason=OwnershipEventReasonCode.NONE,
                )
            )
        return expired


__all__ = [
    "DEFAULT_LEASE_SEC",
    "OwnershipArbiter",
    "OwnershipOutcome",
    "OwnershipRequest",
]
