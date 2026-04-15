"""Tests for the client disconnect sweep on :class:`ReplicationDispatcher`.

Disconnect handling is the Phase 5 half of ownership reclaim: clients
that go silent past ``client_timeout_sec`` are evicted from the room's
``connected_clients`` map and every entity they own has its authority
cleared + an OWNERSHIP_EVENT(EXPIRED) broadcast to the room.
"""

from __future__ import annotations

from styly_netsync.replication.dispatcher import ReplicationDispatcher
from styly_netsync.replication.message_codec import (
    MSG_REPL_OWNERSHIP_EVENT,
    MessageCodec,
    TransformCodecV1,
)
from styly_netsync.replication.messages import (
    JoinRoomMessage,
    OwnershipEventReasonCode,
    OwnershipRequestMessage,
    OwnershipResult,
)
from styly_netsync.replication.models import EntityKind, EntityRecord
from styly_netsync.replication.ownership_arbiter import OwnershipArbiter
from styly_netsync.replication.room_registry import RoomRegistry
from styly_netsync.replication.snapshot_service import SnapshotService
from styly_netsync.replication.state_relay import StateRelay

CLIENT_TIMEOUT = 10.0


def _make_dispatcher() -> tuple[
    ReplicationDispatcher,
    RoomRegistry,
    list[tuple[bytes, bytes]],
    list[tuple[str, bytes]],
    dict[str, float],
]:
    """Build a dispatcher wired with a mutable clock for sweep tests."""
    registry = RoomRegistry()
    sent: list[tuple[bytes, bytes]] = []
    broadcast: list[tuple[str, bytes]] = []
    tick: dict[str, float] = {"now": 1000.0}

    def send_fn(identity: bytes, frame: bytes) -> None:
        sent.append((identity, frame))

    def broadcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    relay = StateRelay(
        room_registry=registry,
        broadcast=broadcast_fn,
        transform_codec=TransformCodecV1(),
        clock=lambda: tick["now"],
    )
    dispatcher = ReplicationDispatcher(
        room_registry=registry,
        snapshot_service=SnapshotService(time_source=lambda: 0),
        transform_codec=TransformCodecV1(),
        ownership_arbiter=OwnershipArbiter(),
        state_relay=relay,
        client_timeout_sec=CLIENT_TIMEOUT,
        clock=lambda: tick["now"],
    )
    return dispatcher, registry, sent, broadcast, tick


def _seed_entity(
    registry: RoomRegistry, entity_id: int, room_id: str = "lobby"
) -> None:
    room = registry.get_or_create(room_id, "h")
    registry.upsert_entity(
        room,
        EntityRecord(entity_id=entity_id, entity_kind=EntityKind.SceneObject),
    )


def _join_and_acquire(
    dispatcher: ReplicationDispatcher,
    send: list[tuple[bytes, bytes]],
    broadcast: list[tuple[str, bytes]],
    identity: bytes,
    entity_id: int,
) -> int:
    """Join the room and acquire ownership of ``entity_id``."""

    def send_fn(ident: bytes, frame: bytes) -> None:
        send.append((ident, frame))

    def broadcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev", scene_hash="h")
    )
    dispatcher.handle_frame(identity, "lobby", join_frame, send_fn, broadcast_fn)
    snapshot = MessageCodec.decode_room_snapshot(send[-1][1], TransformCodecV1())
    client_no = snapshot.your_client_no

    req_frame = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(
            entity_id=entity_id, requester_short_id=client_no, expected_epoch=0
        )
    )
    dispatcher.handle_frame(identity, "lobby", req_frame, send_fn, broadcast_fn)
    return client_no


def test_disconnect_after_timeout_reclaims_ownership_and_broadcasts_expired() -> None:
    dispatcher, registry, sent, broadcast, tick = _make_dispatcher()
    _seed_entity(registry, entity_id=100)

    client_no = _join_and_acquire(
        dispatcher, sent, broadcast, b"client-a", entity_id=100
    )
    room = registry.get("lobby")
    assert room is not None
    assert room.entities[100].owner_client_no == client_no

    # Advance beyond the timeout; the client has produced no frames.
    tick["now"] = 1000.0 + CLIENT_TIMEOUT + 0.1
    broadcast_count_before = len(broadcast)

    emitted = dispatcher.sweep_disconnected_clients(
        lambda room_id, frame: broadcast.append((room_id, frame))
    )

    assert emitted == 1
    # Ownership cleared, epoch bumped, lease zeroed.
    record = room.entities[100]
    assert record.owner_client_no == 0
    assert record.authority_epoch == 2  # 1 on acquire, +1 on eviction
    assert record.lease_expire_at == 0.0
    # Client state removed.
    assert room.connected_clients == {}

    # Broadcast carries OWNERSHIP_EVENT(EXPIRED, LEASE_EXPIRED).
    assert len(broadcast) == broadcast_count_before + 1
    frame = broadcast[-1][1]
    assert frame[0] == MSG_REPL_OWNERSHIP_EVENT
    event = MessageCodec.decode_ownership_event(frame)
    assert event.entity_id == 100
    assert event.new_owner_short_id == 0
    assert event.new_authority_epoch == 2
    assert event.result is OwnershipResult.EXPIRED
    assert event.reason_code is OwnershipEventReasonCode.LEASE_EXPIRED


def test_active_client_is_not_evicted() -> None:
    dispatcher, registry, sent, broadcast, tick = _make_dispatcher()
    _seed_entity(registry, entity_id=100)

    _join_and_acquire(dispatcher, sent, broadcast, b"client-a", entity_id=100)

    # Another ownership request (or any inbound frame) refreshes last_seen.
    tick["now"] = 1005.0
    req_frame = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(entity_id=100, requester_short_id=1, expected_epoch=1)
    )
    dispatcher.handle_frame(
        b"client-a",
        "lobby",
        req_frame,
        lambda ident, frame: sent.append((ident, frame)),
        lambda room_id, frame: broadcast.append((room_id, frame)),
    )

    # Advance to just past the initial join's timeout, but the refresh
    # at t=1005 keeps the client within the active window.
    tick["now"] = 1005.0 + CLIENT_TIMEOUT - 0.1
    emitted = dispatcher.sweep_disconnected_clients(
        lambda room_id, frame: broadcast.append((room_id, frame))
    )
    assert emitted == 0

    room = registry.get("lobby")
    assert room is not None
    assert len(room.connected_clients) == 1
    assert room.entities[100].owner_client_no == 1


def test_sweep_with_no_clients_is_noop() -> None:
    dispatcher, _registry, _sent, broadcast, tick = _make_dispatcher()
    tick["now"] = 50_000.0
    assert (
        dispatcher.sweep_disconnected_clients(
            lambda room_id, frame: broadcast.append((room_id, frame))
        )
        == 0
    )


def test_disconnect_without_ownership_only_evicts_client() -> None:
    dispatcher, registry, sent, broadcast, tick = _make_dispatcher()

    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev", scene_hash="h")
    )
    dispatcher.handle_frame(
        b"observer",
        "lobby",
        join_frame,
        lambda ident, frame: sent.append((ident, frame)),
        lambda room_id, frame: broadcast.append((room_id, frame)),
    )

    tick["now"] = 1000.0 + CLIENT_TIMEOUT + 0.1
    emitted = dispatcher.sweep_disconnected_clients(
        lambda room_id, frame: broadcast.append((room_id, frame))
    )
    # No entities owned → no EXPIRED events, but the client is gone.
    assert emitted == 0
    room = registry.get("lobby")
    assert room is not None
    assert room.connected_clients == {}


def test_state_batch_refreshes_last_seen() -> None:
    """An accepted STATE_BATCH must bump last_seen on the sender.

    Without this, a client driving a stream of pose updates would be
    swept out after 10 s even while it is actively publishing.
    """
    from styly_netsync.replication.messages import (
        ChangedMask,
        StateBatchMessage,
        StateFlags,
        StateUpdate,
        WireTransform,
    )

    dispatcher, registry, sent, broadcast, tick = _make_dispatcher()
    _seed_entity(registry, entity_id=100)

    client_no = _join_and_acquire(
        dispatcher, sent, broadcast, b"client-a", entity_id=100
    )

    tick["now"] = 1005.0
    state_frame = MessageCodec.encode_state_batch(
        StateBatchMessage(
            room_seq=0,
            server_time_us=0,
            updates=[
                StateUpdate(
                    entity_id=100,
                    authority_epoch=1,
                    pose_seq=1,
                    flags=StateFlags.NONE,
                    changed_mask=ChangedMask.POSITION,
                    state=WireTransform(position=(1.0, 0.0, 0.0)),
                )
            ],
        ),
        TransformCodecV1(),
    )
    dispatcher.handle_frame(
        b"client-a",
        "lobby",
        state_frame,
        lambda ident, frame: sent.append((ident, frame)),
        lambda room_id, frame: broadcast.append((room_id, frame)),
    )

    tick["now"] = 1005.0 + CLIENT_TIMEOUT - 0.1
    assert (
        dispatcher.sweep_disconnected_clients(
            lambda room_id, frame: broadcast.append((room_id, frame))
        )
        == 0
    )

    room = registry.get("lobby")
    assert room is not None
    assert room.entities[100].owner_client_no == client_no
