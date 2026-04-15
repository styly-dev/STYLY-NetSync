"""Unit tests for :class:`ReplicationDispatcher`.

The dispatcher is transport-agnostic; these tests use an in-memory sink
for sent frames so they don't require real ZMQ sockets.
"""

from __future__ import annotations

from styly_netsync.replication.dispatcher import ReplicationDispatcher, SendFn
from styly_netsync.replication.message_codec import (
    MSG_REPL_JOIN_REJECT,
    MSG_REPL_ROOM_SNAPSHOT,
    MessageCodec,
    TransformCodecV1,
)
from styly_netsync.replication.messages import (
    JoinRejectReason,
    JoinRoomMessage,
    OwnershipRequestMessage,
)
from styly_netsync.replication.room_registry import RoomRegistry
from styly_netsync.replication.snapshot_service import SnapshotService


def _make_dispatcher(
    registry: RoomRegistry | None = None,
) -> tuple[
    ReplicationDispatcher,
    RoomRegistry,
    list[tuple[bytes, bytes]],
    SendFn,
]:
    registry = registry if registry is not None else RoomRegistry()
    dispatcher = ReplicationDispatcher(
        room_registry=registry,
        snapshot_service=SnapshotService(time_source=lambda: 123_456_789),
        transform_codec=TransformCodecV1(),
        clock=lambda: 1000.0,
    )
    sent: list[tuple[bytes, bytes]] = []

    # ``send`` appends (identity, frame) for later inspection.
    def send(identity: bytes, frame: bytes) -> None:
        sent.append((identity, frame))

    return dispatcher, registry, sent, send


def test_join_room_creates_room_and_replies_with_snapshot() -> None:
    dispatcher, registry, sent, send = _make_dispatcher()

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-1", scene_hash="hash-1")
    )
    result = dispatcher.handle_frame(b"client-1", frame, send)

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
    dispatcher, registry, sent, send = _make_dispatcher()
    registry.get_or_create("lobby", "hash-1")

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-2", scene_hash="hash-2")
    )
    result = dispatcher.handle_frame(b"client-2", frame, send)

    assert result.handled is True
    assert result.accepted is False
    assert result.reject_reason is JoinRejectReason.SCENE_HASH_MISMATCH
    assert len(sent) == 1

    identity, reply = sent[0]
    assert identity == b"client-2"
    assert reply[0] == MSG_REPL_JOIN_REJECT

    reject = MessageCodec.decode_join_reject(reply)
    assert reject.room_id == "lobby"
    assert reject.reason is JoinRejectReason.SCENE_HASH_MISMATCH
    assert "hash-2" in reject.reason_text or "hash-1" in reject.reason_text


def test_join_room_registers_client_state() -> None:
    dispatcher, registry, _sent, send = _make_dispatcher()

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-3", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-3", frame, send)

    room = registry.get("lobby")
    assert room is not None
    assert len(room.connected_clients) == 1
    state = next(iter(room.connected_clients.values()))
    assert state.connected_at == 1000.0
    assert state.last_seen == 1000.0


def test_join_room_reuses_existing_client_state_and_bumps_last_seen() -> None:
    ticks = iter([1000.0, 1050.0])
    registry = RoomRegistry()
    dispatcher = ReplicationDispatcher(
        room_registry=registry,
        snapshot_service=SnapshotService(time_source=lambda: 0),
        transform_codec=TransformCodecV1(),
        clock=lambda: next(ticks),
    )
    sent: list[tuple[bytes, bytes]] = []

    def send(identity: bytes, frame: bytes) -> None:
        sent.append((identity, frame))

    frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev-4", scene_hash="h")
    )
    dispatcher.handle_frame(b"client-4", frame, send)
    dispatcher.handle_frame(b"client-4", frame, send)

    room = registry.get("lobby")
    assert room is not None
    assert len(room.connected_clients) == 1
    state = next(iter(room.connected_clients.values()))
    assert state.connected_at == 1000.0
    assert state.last_seen == 1050.0


def test_malformed_join_room_frame_is_handled_but_not_accepted() -> None:
    dispatcher, _registry, sent, send = _make_dispatcher()

    # Header says JOIN_ROOM but payload is truncated.
    result = dispatcher.handle_frame(b"client-x", bytes([30, 1]), send)
    assert result.handled is True
    assert result.accepted is False
    assert sent == []


def test_unhandled_message_type_reports_not_handled() -> None:
    dispatcher, _registry, sent, send = _make_dispatcher()

    frame = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(entity_id=1, requester_short_id=0, expected_epoch=0)
    )
    result = dispatcher.handle_frame(b"client-y", frame, send)
    assert result.handled is False
    assert sent == []
