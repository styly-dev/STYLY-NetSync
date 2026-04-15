"""Unit tests for :class:`ReplicationDispatcher`.

The dispatcher is transport-agnostic; these tests use in-memory sinks
for sent and broadcast frames so they don't require real ZMQ sockets.
"""

from __future__ import annotations

from styly_netsync.replication.dispatcher import (
    BroadcastFn,
    ReplicationDispatcher,
    SendFn,
)
from styly_netsync.replication.message_codec import (
    MSG_REPL_JOIN_REJECT,
    MSG_REPL_OWNERSHIP_EVENT,
    MSG_REPL_ROOM_SNAPSHOT,
    MSG_REPL_STATE_BATCH,
    MessageCodec,
    TransformCodecV1,
)
from styly_netsync.replication.messages import (
    ChangedMask,
    JoinRejectReason,
    JoinRoomMessage,
    OwnershipRequestMessage,
    OwnershipResult,
    StateBatchMessage,
    StateFlags,
    StateUpdate,
    WireTransform,
)
from styly_netsync.replication.models import EntityKind, EntityRecord
from styly_netsync.replication.ownership_arbiter import OwnershipArbiter
from styly_netsync.replication.room_registry import RoomRegistry
from styly_netsync.replication.snapshot_service import SnapshotService
from styly_netsync.replication.state_relay import StateRelay


def _make_dispatcher(
    registry: RoomRegistry | None = None,
    clock: float = 1000.0,
    lease_sec: float = 2.0,
) -> tuple[
    ReplicationDispatcher,
    RoomRegistry,
    list[tuple[bytes, bytes]],
    list[tuple[str, bytes]],
    SendFn,
    BroadcastFn,
]:
    registry = registry if registry is not None else RoomRegistry()
    sent: list[tuple[bytes, bytes]] = []
    broadcast: list[tuple[str, bytes]] = []

    def send_fn(identity: bytes, frame: bytes) -> None:
        sent.append((identity, frame))

    def broadcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    relay = StateRelay(
        room_registry=registry,
        broadcast=broadcast_fn,
        transform_codec=TransformCodecV1(),
        lease_sec=lease_sec,
        clock=lambda: clock,
    )
    dispatcher = ReplicationDispatcher(
        room_registry=registry,
        snapshot_service=SnapshotService(time_source=lambda: 123_456_789),
        transform_codec=TransformCodecV1(),
        ownership_arbiter=OwnershipArbiter(lease_sec=lease_sec),
        state_relay=relay,
        clock=lambda: clock,
    )
    return dispatcher, registry, sent, broadcast, send_fn, broadcast_fn


# --- JOIN_ROOM -------------------------------------------------------------


def test_join_room_creates_room_and_replies_with_snapshot() -> None:
    dispatcher, registry, sent, _bc, send, broadcast = _make_dispatcher()

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-1", scene_hash="hash-1")
    )
    result = dispatcher.handle_frame(b"client-1", "lobby", frame, send, broadcast)

    assert result.handled is True
    assert result.accepted is True
    assert result.reject_reason is None
    assert registry.get("lobby") is not None
    assert len(sent) == 1

    identity, reply = sent[0]
    assert identity == b"client-1"
    assert reply[0] == MSG_REPL_ROOM_SNAPSHOT

    snapshot = MessageCodec.decode_room_snapshot(reply, TransformCodecV1())
    assert snapshot.room_id == "lobby"
    assert snapshot.base_room_seq == 0
    assert snapshot.server_time_us == 123_456_789
    assert snapshot.entities == []


def test_join_room_scene_hash_mismatch_sends_reject() -> None:
    dispatcher, registry, sent, _bc, send, broadcast = _make_dispatcher()
    registry.get_or_create("lobby", "hash-1")

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-2", scene_hash="hash-2")
    )
    result = dispatcher.handle_frame(b"client-2", "lobby", frame, send, broadcast)

    assert result.handled is True
    assert result.accepted is False
    assert result.reject_reason is JoinRejectReason.SCENE_HASH_MISMATCH
    assert len(sent) == 1

    identity, reply = sent[0]
    assert identity == b"client-2"
    assert reply[0] == MSG_REPL_JOIN_REJECT


def test_join_room_registers_client_state() -> None:
    dispatcher, registry, _sent, _bc, send, broadcast = _make_dispatcher()

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-3", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-3", "lobby", frame, send, broadcast)

    room = registry.get("lobby")
    assert room is not None
    assert len(room.connected_clients) == 1


def test_join_room_assigns_distinct_client_numbers() -> None:
    """Two different identities joining the same room receive distinct short ids."""
    dispatcher, registry, sent, _bc, send, broadcast = _make_dispatcher()

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-a", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-a", "lobby", frame, send, broadcast)
    dispatcher.handle_frame(b"client-b", "lobby", frame, send, broadcast)

    snap_a = MessageCodec.decode_room_snapshot(sent[0][1], TransformCodecV1())
    snap_b = MessageCodec.decode_room_snapshot(sent[1][1], TransformCodecV1())
    assert snap_a.your_client_no == 1
    assert snap_b.your_client_no == 2

    room = registry.get("lobby")
    assert room is not None
    assert {s.client_no for s in room.connected_clients.values()} == {1, 2}
    assert room.next_client_no == 3


def test_join_room_rejoin_reuses_client_number() -> None:
    """A rejoin from the same identity must receive the same short id.

    This is the contract the ownership plane relies on: client_no is
    stable for the lifetime of the ROUTER identity so OWNERSHIP_EVENT
    records can be matched against the client's LocalClientNo across
    reconnects. Disconnect reclaim is a Phase 5 concern.
    """
    dispatcher, registry, sent, _bc, send, broadcast = _make_dispatcher()

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-a", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-a", "lobby", frame, send, broadcast)
    dispatcher.handle_frame(b"client-a", "lobby", frame, send, broadcast)

    snap_first = MessageCodec.decode_room_snapshot(sent[0][1], TransformCodecV1())
    snap_second = MessageCodec.decode_room_snapshot(sent[1][1], TransformCodecV1())
    assert snap_first.your_client_no == 1
    assert snap_second.your_client_no == 1

    room = registry.get("lobby")
    assert room is not None
    assert len(room.connected_clients) == 1
    # Counter did not advance — no new allocation happened on rejoin.
    assert room.next_client_no == 2


def test_join_room_counters_are_per_room() -> None:
    """Client numbers are scoped to a single room; rooms don't share a counter."""
    dispatcher, registry, sent, _bc, send, broadcast = _make_dispatcher()

    frame_one = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="room-1", device_id="dev-a", scene_hash="h")
    )
    frame_two = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="room-2", device_id="dev-b", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-a", "room-1", frame_one, send, broadcast)
    dispatcher.handle_frame(b"client-b", "room-2", frame_two, send, broadcast)

    snap_a = MessageCodec.decode_room_snapshot(sent[0][1], TransformCodecV1())
    snap_b = MessageCodec.decode_room_snapshot(sent[1][1], TransformCodecV1())
    # Each room starts at 1; counters are independent.
    assert snap_a.your_client_no == 1
    assert snap_b.your_client_no == 1


def test_malformed_join_room_frame_is_handled_but_not_accepted() -> None:
    dispatcher, _registry, sent, _bc, send, broadcast = _make_dispatcher()

    result = dispatcher.handle_frame(
        b"client-x", "lobby", bytes([30, 1]), send, broadcast
    )
    assert result.handled is True
    assert result.accepted is False
    assert sent == []


# --- OWNERSHIP_REQUEST ----------------------------------------------------


def _seed_room_with_entity(registry: RoomRegistry, entity_id: int = 100) -> None:
    room = registry.get_or_create("lobby", "h")
    registry.upsert_entity(
        room, EntityRecord(entity_id=entity_id, entity_kind=EntityKind.SceneObject)
    )


def test_ownership_acquire_sends_reply_and_broadcasts_event() -> None:
    dispatcher, registry, sent, broadcast, send, bcast = _make_dispatcher()
    _seed_room_with_entity(registry)

    frame = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(entity_id=100, requester_short_id=7, expected_epoch=0)
    )
    result = dispatcher.handle_frame(b"client-7", "lobby", frame, send, bcast)

    assert result.handled is True
    assert result.accepted is True
    assert result.ownership_result is OwnershipResult.GRANTED

    # Reply to sender + broadcast to room.
    assert len(sent) == 1
    assert sent[0][0] == b"client-7"
    assert sent[0][1][0] == MSG_REPL_OWNERSHIP_EVENT

    assert len(broadcast) == 1
    assert broadcast[0][0] == "lobby"
    assert broadcast[0][1] == sent[0][1]

    event = MessageCodec.decode_ownership_event(broadcast[0][1])
    assert event.entity_id == 100
    assert event.new_owner_short_id == 7
    assert event.new_authority_epoch == 1
    assert event.result is OwnershipResult.GRANTED

    # Authoritative record reflects the grant.
    record = registry.get("lobby").entities[100]  # type: ignore[union-attr]
    assert record.owner_client_no == 7
    assert record.authority_epoch == 1
    assert record.lease_expire_at == 1002.0


def test_ownership_acquire_on_owned_returns_rejected_event() -> None:
    dispatcher, registry, sent, broadcast, send, bcast = _make_dispatcher()
    _seed_room_with_entity(registry)

    first = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(entity_id=100, requester_short_id=7, expected_epoch=0)
    )
    dispatcher.handle_frame(b"client-7", "lobby", first, send, bcast)

    second = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(entity_id=100, requester_short_id=9, expected_epoch=0)
    )
    result = dispatcher.handle_frame(b"client-9", "lobby", second, send, bcast)

    assert result.accepted is False
    assert result.ownership_result is OwnershipResult.DENIED

    # Latest broadcast carries the deny event with current owner/epoch.
    event = MessageCodec.decode_ownership_event(broadcast[-1][1])
    assert event.result is OwnershipResult.DENIED
    assert event.new_owner_short_id == 7  # unchanged
    assert event.new_authority_epoch == 1


def test_ownership_request_for_unknown_room_drops() -> None:
    dispatcher, _registry, sent, broadcast, send, bcast = _make_dispatcher()

    frame = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(entity_id=1, requester_short_id=1, expected_epoch=0)
    )
    result = dispatcher.handle_frame(b"client-1", "ghost", frame, send, bcast)

    assert result.handled is True
    assert result.accepted is False
    assert sent == []
    assert broadcast == []


def test_malformed_ownership_request_is_handled_but_not_accepted() -> None:
    dispatcher, registry, sent, _bc, send, broadcast = _make_dispatcher()
    _seed_room_with_entity(registry)

    # Header says OWNERSHIP_REQUEST but payload is truncated.
    result = dispatcher.handle_frame(
        b"client-x", "lobby", bytes([32, 1]), send, broadcast
    )
    assert result.handled is True
    assert result.accepted is False
    assert sent == []


# --- Lease sweep ---------------------------------------------------------


def test_sweep_expired_leases_broadcasts_expired_events() -> None:
    registry = RoomRegistry()
    _seed_room_with_entity(registry)
    broadcast: list[tuple[str, bytes]] = []

    # Acquire at t=100, lease expires at 102.
    arbiter = OwnershipArbiter(lease_sec=2.0)
    tick = {"t": 100.0}
    dispatcher = ReplicationDispatcher(
        room_registry=registry,
        snapshot_service=SnapshotService(time_source=lambda: 0),
        transform_codec=TransformCodecV1(),
        ownership_arbiter=arbiter,
        clock=lambda: tick["t"],
    )
    sent: list[tuple[bytes, bytes]] = []

    def send(identity: bytes, frame: bytes) -> None:
        sent.append((identity, frame))

    def bcast(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    frame = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(entity_id=100, requester_short_id=7, expected_epoch=0)
    )
    dispatcher.handle_frame(b"client-7", "lobby", frame, send, bcast)
    assert len(broadcast) == 1  # GRANTED

    tick["t"] = 101.5
    assert dispatcher.sweep_expired_leases(bcast) == 0
    assert len(broadcast) == 1

    tick["t"] = 103.0
    assert dispatcher.sweep_expired_leases(bcast) == 1
    assert len(broadcast) == 2

    event = MessageCodec.decode_ownership_event(broadcast[-1][1])
    assert event.entity_id == 100
    assert event.result is OwnershipResult.EXPIRED
    assert event.new_owner_short_id == 0
    # Epoch bumped once on acquire (0→1), once on expiry (1→2).
    assert event.new_authority_epoch == 2


# --- STATE_BATCH -----------------------------------------------------------


def test_state_batch_routed_and_broadcast_on_flush() -> None:
    """End-to-end: JOIN → OWNERSHIP acquire → STATE_BATCH → flush → broadcast."""
    dispatcher, registry, sent, broadcast, send, bcast = _make_dispatcher()
    _seed_room_with_entity(registry, entity_id=100)

    # Join so the dispatcher knows the identity → client_no mapping.
    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-o", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-o", "lobby", join_frame, send, bcast)
    snapshot = MessageCodec.decode_room_snapshot(sent[0][1], TransformCodecV1())
    client_no = snapshot.your_client_no

    # Acquire ownership so the state relay will accept updates.
    req_frame = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(
            entity_id=100, requester_short_id=client_no, expected_epoch=0
        )
    )
    dispatcher.handle_frame(b"client-o", "lobby", req_frame, send, bcast)

    broadcast_count_before = len(broadcast)

    # Send a STATE_BATCH; accepted update should go into dirty set.
    state_frame = MessageCodec.encode_state_batch(
        StateBatchMessage(
            room_seq=0,
            updates=[
                StateUpdate(
                    entity_id=100,
                    authority_epoch=1,
                    pose_seq=1,
                    flags=StateFlags.NONE,
                    changed_mask=ChangedMask.POSITION | ChangedMask.ROTATION,
                    state=WireTransform(position=(4.0, 5.0, 6.0)),
                )
            ],
        ),
        TransformCodecV1(),
    )
    result = dispatcher.handle_frame(b"client-o", "lobby", state_frame, send, bcast)
    assert result.handled is True
    assert result.accepted is True
    # No new broadcast yet — the relay only publishes on flush.
    assert len(broadcast) == broadcast_count_before

    # Flush drains the dirty set and emits exactly one STATE_BATCH frame.
    flushed = dispatcher.flush_pending_batches()
    assert flushed == 1
    assert len(broadcast) == broadcast_count_before + 1

    published_room, published_frame = broadcast[-1]
    assert published_room == "lobby"
    assert published_frame[0] == MSG_REPL_STATE_BATCH

    decoded = MessageCodec.decode_state_batch(published_frame, TransformCodecV1())
    assert decoded.room_seq == 1
    assert len(decoded.updates) == 1
    assert decoded.updates[0].entity_id == 100
    assert decoded.updates[0].pose_seq == 1
    assert decoded.updates[0].state.position == (4.0, 5.0, 6.0)


def test_state_batch_from_non_owner_is_dropped() -> None:
    dispatcher, registry, sent, broadcast, send, bcast = _make_dispatcher()
    _seed_room_with_entity(registry, entity_id=100)

    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-q", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-q", "lobby", join_frame, send, bcast)

    # Send a STATE_BATCH without ever claiming ownership.
    state_frame = MessageCodec.encode_state_batch(
        StateBatchMessage(
            room_seq=0,
            updates=[
                StateUpdate(
                    entity_id=100,
                    authority_epoch=0,
                    pose_seq=1,
                    flags=StateFlags.NONE,
                    changed_mask=ChangedMask.POSITION,
                    state=WireTransform(position=(1.0, 0.0, 0.0)),
                )
            ],
        ),
        TransformCodecV1(),
    )
    result = dispatcher.handle_frame(b"client-q", "lobby", state_frame, send, bcast)
    assert result.handled is True
    assert result.accepted is False

    # Nothing to flush.
    assert dispatcher.flush_pending_batches() == 0


def test_state_batch_from_unknown_identity_is_dropped() -> None:
    """Identity must be registered via JOIN_ROOM before STATE_BATCH is accepted."""
    dispatcher, registry, _sent, _bc, send, bcast = _make_dispatcher()
    _seed_room_with_entity(registry, entity_id=100)

    state_frame = MessageCodec.encode_state_batch(
        StateBatchMessage(
            room_seq=0,
            updates=[
                StateUpdate(
                    entity_id=100,
                    authority_epoch=0,
                    pose_seq=1,
                    flags=StateFlags.NONE,
                    changed_mask=ChangedMask.POSITION,
                )
            ],
        ),
        TransformCodecV1(),
    )
    result = dispatcher.handle_frame(b"stranger", "lobby", state_frame, send, bcast)
    assert result.handled is True
    assert result.accepted is False


def test_unhandled_message_type_reports_not_handled() -> None:
    dispatcher, _registry, sent, broadcast, send, bcast = _make_dispatcher()

    # JOIN_REJECT (id 37) is server→client only — the server never
    # routes an inbound frame of that type, so it is a reliable
    # "unhandled" sample for this test.
    frame = bytes([37, 1])
    result = dispatcher.handle_frame(b"client-y", "lobby", frame, send, bcast)
    assert result.handled is False
    assert sent == []
    assert broadcast == []
