"""Tests for the RESYNC_REQUEST → RESYNC_REPLY dispatcher path."""

from __future__ import annotations

import logging

from styly_netsync.replication.dispatcher import ReplicationDispatcher
from styly_netsync.replication.message_codec import (
    MSG_REPL_RESYNC_REPLY,
    MessageCodec,
    TransformCodecV1,
)
from styly_netsync.replication.messages import (
    JoinRoomMessage,
    OwnershipRequestMessage,
    ResyncRequestMessage,
)
from styly_netsync.replication.models import EntityKind, EntityRecord
from styly_netsync.replication.ownership_arbiter import OwnershipArbiter
from styly_netsync.replication.room_registry import RoomRegistry
from styly_netsync.replication.snapshot_service import SnapshotService
from styly_netsync.replication.state_relay import StateRelay

SNAPSHOT_TIME_US = 1_700_000_000_000_000


def _make_dispatcher() -> tuple[
    ReplicationDispatcher,
    RoomRegistry,
    list[tuple[bytes, bytes]],
    list[tuple[str, bytes]],
]:
    registry = RoomRegistry()
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
        clock=lambda: 1000.0,
    )
    dispatcher = ReplicationDispatcher(
        room_registry=registry,
        snapshot_service=SnapshotService(time_source=lambda: SNAPSHOT_TIME_US),
        transform_codec=TransformCodecV1(),
        ownership_arbiter=OwnershipArbiter(),
        state_relay=relay,
        clock=lambda: 1000.0,
    )
    return dispatcher, registry, sent, broadcast


def _seed_and_claim_entity(
    dispatcher: ReplicationDispatcher,
    registry: RoomRegistry,
    sent: list[tuple[bytes, bytes]],
    broadcast: list[tuple[str, bytes]],
    entity_id: int,
    identity: bytes,
) -> int:
    """Pre-populate the room so the snapshot has a non-empty sparse set."""
    room = registry.get_or_create("lobby", "h")
    registry.upsert_entity(
        room,
        EntityRecord(entity_id=entity_id, entity_kind=EntityKind.SceneObject),
    )

    def send_fn(ident: bytes, frame: bytes) -> None:
        sent.append((ident, frame))

    def bcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="dev", scene_hash="h")
    )
    dispatcher.handle_frame(identity, "lobby", join_frame, send_fn, bcast_fn)
    snap = MessageCodec.decode_room_snapshot(sent[-1][1], TransformCodecV1())
    client_no = snap.your_client_no

    req = MessageCodec.encode_ownership_request(
        OwnershipRequestMessage(
            entity_id=entity_id, requester_short_id=client_no, expected_epoch=0
        )
    )
    dispatcher.handle_frame(identity, "lobby", req, send_fn, bcast_fn)
    return client_no


def test_resync_reply_mirrors_current_sparse_snapshot() -> None:
    dispatcher, registry, sent, broadcast = _make_dispatcher()
    _seed_and_claim_entity(
        dispatcher, registry, sent, broadcast, entity_id=100, identity=b"owner"
    )

    # Third client joins and immediately resyncs.
    def send_fn(ident: bytes, frame: bytes) -> None:
        sent.append((ident, frame))

    def bcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="late", scene_hash="h")
    )
    dispatcher.handle_frame(b"late", "lobby", join_frame, send_fn, bcast_fn)

    sent_before = len(sent)
    req_frame = MessageCodec.encode_resync_request(
        ResyncRequestMessage(last_applied_room_seq=0)
    )
    result = dispatcher.handle_frame(b"late", "lobby", req_frame, send_fn, bcast_fn)
    assert result.handled is True
    assert result.accepted is True

    assert len(sent) == sent_before + 1
    identity, reply_frame = sent[-1]
    assert identity == b"late"
    assert reply_frame[0] == MSG_REPL_RESYNC_REPLY

    reply = MessageCodec.decode_resync_reply(reply_frame, TransformCodecV1())
    assert reply.room_id == "lobby"
    # Current sequence = next_room_seq - 1. No STATE_BATCH has been
    # flushed, so next_room_seq is still 1 → base_room_seq == 0.
    assert reply.base_room_seq == 0
    assert reply.server_time_us == SNAPSHOT_TIME_US
    # The owned entity shows up in the sparse snapshot.
    assert len(reply.entities) == 1
    assert reply.entities[0].entity_id == 100
    assert reply.entities[0].owner_short_id != 0


def test_resync_for_unknown_room_drops() -> None:
    dispatcher, _registry, sent, broadcast = _make_dispatcher()

    def send_fn(ident: bytes, frame: bytes) -> None:
        sent.append((ident, frame))

    def bcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    req = MessageCodec.encode_resync_request(
        ResyncRequestMessage(last_applied_room_seq=0)
    )
    result = dispatcher.handle_frame(b"ghost", "nowhere", req, send_fn, bcast_fn)
    assert result.handled is True
    assert result.accepted is False
    assert sent == []


def test_resync_with_stale_seq_still_replies_but_warns(
    caplog: logging.LogCaptureFixture,
) -> None:
    dispatcher, registry, sent, broadcast = _make_dispatcher()
    _seed_and_claim_entity(
        dispatcher, registry, sent, broadcast, entity_id=100, identity=b"owner"
    )

    room = registry.get("lobby")
    assert room is not None
    # Fast-forward the room's published sequence so the gap check fires.
    room.next_room_seq = 5000

    def send_fn(ident: bytes, frame: bytes) -> None:
        sent.append((ident, frame))

    def bcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="late", scene_hash="h")
    )
    dispatcher.handle_frame(b"late", "lobby", join_frame, send_fn, bcast_fn)

    sent_before = len(sent)
    req = MessageCodec.encode_resync_request(
        ResyncRequestMessage(last_applied_room_seq=1)
    )
    with caplog.at_level(
        logging.WARNING, logger="styly_netsync.replication.dispatcher"
    ):
        result = dispatcher.handle_frame(b"late", "lobby", req, send_fn, bcast_fn)

    assert result.accepted is True
    assert len(sent) == sent_before + 1
    # Warning about the gap is present but the reply is still sent.
    assert any("batches behind current" in rec.message for rec in caplog.records)


def test_resync_with_fresh_seq_does_not_warn(
    caplog: logging.LogCaptureFixture,
) -> None:
    dispatcher, registry, sent, broadcast = _make_dispatcher()
    _seed_and_claim_entity(
        dispatcher, registry, sent, broadcast, entity_id=100, identity=b"owner"
    )

    def send_fn(ident: bytes, frame: bytes) -> None:
        sent.append((ident, frame))

    def bcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    join_frame = MessageCodec.encode_join_room(
        JoinRoomMessage(room_id="lobby", device_id="late", scene_hash="h")
    )
    dispatcher.handle_frame(b"late", "lobby", join_frame, send_fn, bcast_fn)

    req = MessageCodec.encode_resync_request(
        ResyncRequestMessage(last_applied_room_seq=0)
    )
    with caplog.at_level(
        logging.WARNING, logger="styly_netsync.replication.dispatcher"
    ):
        dispatcher.handle_frame(b"late", "lobby", req, send_fn, bcast_fn)

    assert not any("batches behind current" in rec.message for rec in caplog.records)


def test_malformed_resync_request_is_handled_but_not_accepted() -> None:
    dispatcher, registry, sent, broadcast = _make_dispatcher()
    registry.get_or_create("lobby", "h")

    def send_fn(ident: bytes, frame: bytes) -> None:
        sent.append((ident, frame))

    def bcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    # Header says RESYNC_REQUEST but payload is truncated (missing u32).
    result = dispatcher.handle_frame(
        b"client", "lobby", bytes([34, 1]), send_fn, bcast_fn
    )
    assert result.handled is True
    assert result.accepted is False
    assert sent == []


def test_resync_refreshes_last_seen() -> None:
    """A RESYNC_REQUEST counts as "client is alive" like any other frame."""
    dispatcher, registry, sent, broadcast = _make_dispatcher()
    _seed_and_claim_entity(
        dispatcher, registry, sent, broadcast, entity_id=100, identity=b"owner"
    )
    room = registry.get("lobby")
    assert room is not None
    owner_key = next(iter(room.connected_clients))
    # Clobber last_seen to the past so we can observe the refresh.
    room.connected_clients[owner_key].last_seen = 0.0

    def send_fn(ident: bytes, frame: bytes) -> None:
        sent.append((ident, frame))

    def bcast_fn(room_id: str, frame: bytes) -> None:
        broadcast.append((room_id, frame))

    req = MessageCodec.encode_resync_request(
        ResyncRequestMessage(last_applied_room_seq=0)
    )
    dispatcher.handle_frame(b"owner", "lobby", req, send_fn, bcast_fn)
    assert room.connected_clients[owner_key].last_seen == 1000.0
