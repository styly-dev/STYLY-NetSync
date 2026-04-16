"""Round-trip tests for replication protocol v1 message codec."""

from __future__ import annotations

import pytest

from styly_netsync.replication.message_codec import (
    MSG_REPL_JOIN_REJECT,
    MSG_REPL_JOIN_ROOM,
    MSG_REPL_OWNERSHIP_EVENT,
    MSG_REPL_OWNERSHIP_REQUEST,
    MSG_REPL_RESYNC_REPLY,
    MSG_REPL_RESYNC_REQUEST,
    MSG_REPL_ROOM_SNAPSHOT,
    MSG_REPL_STATE_BATCH,
    REPL_PROTOCOL_VERSION,
    MessageCodec,
    TransformCodecV1,
)
from styly_netsync.replication.messages import (
    ChangedMask,
    JoinRejectMessage,
    JoinRejectReason,
    JoinRoomMessage,
    OwnershipEventMessage,
    OwnershipEventReasonCode,
    OwnershipRequestMessage,
    OwnershipResult,
    ResyncReplyMessage,
    ResyncRequestMessage,
    RoomSnapshotMessage,
    StateBatchMessage,
    StateFlags,
    StateUpdate,
    WireEntityRecord,
    WireTransform,
)


@pytest.fixture
def codec() -> TransformCodecV1:
    return TransformCodecV1()


def _sample_state(seed: float) -> WireTransform:
    # Values are exactly representable in float32 so round-trip equality holds.
    return WireTransform(
        position=(seed, seed + 1.0, seed + 2.0),
        rotation=(0.0, 0.0, 0.0, 1.0),
        scale=(seed + 0.25, seed + 0.5, seed + 0.75),
    )


def test_peek_message_type_empty() -> None:
    assert MessageCodec.peek_message_type(b"") == 0


def test_join_room_round_trip() -> None:
    msg = JoinRoomMessage(room_id="lobby", device_id="device-abc", scene_hash="abc123")
    encoded = MessageCodec.encode_join_room(msg)
    assert encoded[0] == MSG_REPL_JOIN_ROOM
    assert encoded[1] == REPL_PROTOCOL_VERSION
    decoded = MessageCodec.decode_join_room(encoded)
    assert decoded == msg


def test_join_room_empty_strings() -> None:
    msg = JoinRoomMessage(room_id="", device_id="", scene_hash="")
    decoded = MessageCodec.decode_join_room(MessageCodec.encode_join_room(msg))
    assert decoded == msg


def test_join_reject_round_trip() -> None:
    for reason in JoinRejectReason:
        msg = JoinRejectMessage(
            room_id="room-x",
            reason=reason,
            reason_text=f"reason={reason.name}",
        )
        encoded = MessageCodec.encode_join_reject(msg)
        assert encoded[0] == MSG_REPL_JOIN_REJECT
        decoded = MessageCodec.decode_join_reject(encoded)
        assert decoded == msg


def test_join_reject_unknown_reason_coerced() -> None:
    # Synthesize a payload with an unknown reason code; decoder must coerce
    # to UNSPECIFIED rather than raise, so forward-compatible clients can
    # still surface the reason_text.
    encoded = bytearray(
        MessageCodec.encode_join_reject(
            JoinRejectMessage(
                room_id="room-x",
                reason=JoinRejectReason.UNSPECIFIED,
                reason_text="future reason",
            )
        )
    )
    # Layout: header(2) + roomId_len(1) + roomId(6) + reason(1) + ...
    reason_offset = 2 + 1 + len("room-x")
    encoded[reason_offset] = 200  # not defined in the enum
    decoded = MessageCodec.decode_join_reject(bytes(encoded))
    assert decoded.reason is JoinRejectReason.UNSPECIFIED
    assert decoded.reason_text == "future reason"


def test_room_snapshot_round_trip(codec: TransformCodecV1) -> None:
    msg = RoomSnapshotMessage(
        room_id="room-1",
        base_room_seq=123456,
        server_time_us=1_700_000_000_000_000,
        your_client_no=42,
        entities=[
            WireEntityRecord(
                entity_id=0xDEADBEEFCAFEBABE,
                authority_epoch=7,
                owner_short_id=42,
                pose_seq=9,
                changed_mask=ChangedMask.ALL,
                state=_sample_state(1.0),
            ),
            WireEntityRecord(
                entity_id=1,
                authority_epoch=0,
                owner_short_id=0,
                pose_seq=0,
                changed_mask=ChangedMask.POSITION,
                state=_sample_state(2.0),
            ),
        ],
    )
    encoded = MessageCodec.encode_room_snapshot(msg, codec)
    assert encoded[0] == MSG_REPL_ROOM_SNAPSHOT
    decoded = MessageCodec.decode_room_snapshot(encoded, codec)
    assert decoded.room_id == msg.room_id
    assert decoded.base_room_seq == msg.base_room_seq
    assert decoded.server_time_us == msg.server_time_us
    assert decoded.your_client_no == msg.your_client_no
    assert len(decoded.entities) == 2
    # Position-only second record should decode scale as default (1,1,1).
    assert decoded.entities[0].state.position == msg.entities[0].state.position
    assert decoded.entities[0].state.scale == msg.entities[0].state.scale
    assert decoded.entities[1].state.position == msg.entities[1].state.position
    assert decoded.entities[1].state.scale == (1.0, 1.0, 1.0)


def test_ownership_request_round_trip() -> None:
    msg = OwnershipRequestMessage(
        entity_id=0x1122334455667788,
        requester_short_id=12,
        expected_epoch=3,
        release=False,
    )
    encoded = MessageCodec.encode_ownership_request(msg)
    assert encoded[0] == MSG_REPL_OWNERSHIP_REQUEST
    decoded = MessageCodec.decode_ownership_request(encoded)
    assert decoded == msg
    assert decoded.release is False

    # Verify release flag round-trips correctly.
    release_msg = OwnershipRequestMessage(
        entity_id=42,
        requester_short_id=1,
        expected_epoch=5,
        release=True,
    )
    release_encoded = MessageCodec.encode_ownership_request(release_msg)
    release_decoded = MessageCodec.decode_ownership_request(release_encoded)
    assert release_decoded == release_msg
    assert release_decoded.release is True


def test_ownership_event_round_trip() -> None:
    cases = [
        (OwnershipResult.GRANTED, OwnershipEventReasonCode.NONE, 7),
        (OwnershipResult.RELEASED, OwnershipEventReasonCode.NONE, 0),
        (OwnershipResult.EXPIRED, OwnershipEventReasonCode.NONE, 0),
        (OwnershipResult.DENIED, OwnershipEventReasonCode.ALREADY_OWNED, 0),
        (OwnershipResult.DENIED, OwnershipEventReasonCode.NOT_OWNER, 0),
        (OwnershipResult.DENIED, OwnershipEventReasonCode.EPOCH_MISMATCH, 0),
        (OwnershipResult.DENIED, OwnershipEventReasonCode.LEASE_EXPIRED, 0),
    ]
    for result, reason_code, owner in cases:
        msg = OwnershipEventMessage(
            entity_id=99,
            new_owner_short_id=owner,
            new_authority_epoch=4,
            result=result,
            reason_code=reason_code,
        )
        encoded = MessageCodec.encode_ownership_event(msg)
        assert encoded[0] == MSG_REPL_OWNERSHIP_EVENT
        assert MessageCodec.decode_ownership_event(encoded) == msg


def test_ownership_event_unknown_reason_code_coerced() -> None:
    encoded = bytearray(
        MessageCodec.encode_ownership_event(
            OwnershipEventMessage(
                entity_id=1,
                new_owner_short_id=0,
                new_authority_epoch=0,
                result=OwnershipResult.DENIED,
                reason_code=OwnershipEventReasonCode.NONE,
            )
        )
    )
    # Layout: header(2) + entityId(8) + owner(4) + epoch(4) + result(1) + reasonCode(1)
    reason_code_offset = 2 + 8 + 4 + 4 + 1
    encoded[reason_code_offset] = 200
    decoded = MessageCodec.decode_ownership_event(bytes(encoded))
    assert decoded.reason_code is OwnershipEventReasonCode.NONE
    assert decoded.result is OwnershipResult.DENIED


def test_ownership_event_unknown_result_raises() -> None:
    encoded = bytearray(
        MessageCodec.encode_ownership_event(
            OwnershipEventMessage(
                entity_id=1,
                new_owner_short_id=0,
                new_authority_epoch=0,
                result=OwnershipResult.GRANTED,
                reason_code=OwnershipEventReasonCode.NONE,
            )
        )
    )
    result_offset = 2 + 8 + 4 + 4
    encoded[result_offset] = 200
    with pytest.raises(ValueError, match="Unknown OwnershipResult"):
        MessageCodec.decode_ownership_event(bytes(encoded))


def test_resync_request_round_trip() -> None:
    msg = ResyncRequestMessage(last_applied_room_seq=0xDEADBEEF)
    encoded = MessageCodec.encode_resync_request(msg)
    assert encoded[0] == MSG_REPL_RESYNC_REQUEST
    assert MessageCodec.decode_resync_request(encoded) == msg


def test_resync_request_zero() -> None:
    # A fresh client with no applied batches uses 0 to ask for everything.
    msg = ResyncRequestMessage()
    decoded = MessageCodec.decode_resync_request(
        MessageCodec.encode_resync_request(msg)
    )
    assert decoded.last_applied_room_seq == 0


def test_resync_reply_round_trip(codec: TransformCodecV1) -> None:
    msg = ResyncReplyMessage(
        room_id="room-resync",
        base_room_seq=98765,
        server_time_us=1_700_000_000_000_500,
        entities=[
            WireEntityRecord(
                entity_id=10,
                authority_epoch=1,
                owner_short_id=2,
                pose_seq=3,
                changed_mask=ChangedMask.ROTATION,
                state=_sample_state(3.0),
            )
        ],
    )
    encoded = MessageCodec.encode_resync_reply(msg, codec)
    assert encoded[0] == MSG_REPL_RESYNC_REPLY
    decoded = MessageCodec.decode_resync_reply(encoded, codec)
    assert decoded.room_id == msg.room_id
    assert decoded.base_room_seq == msg.base_room_seq
    assert decoded.server_time_us == msg.server_time_us
    assert len(decoded.entities) == 1
    assert decoded.entities[0].changed_mask == ChangedMask.ROTATION
    # Position and scale default when not masked.
    assert decoded.entities[0].state.position == (0.0, 0.0, 0.0)
    assert decoded.entities[0].state.scale == (1.0, 1.0, 1.0)
    assert decoded.entities[0].state.rotation == msg.entities[0].state.rotation


def test_resync_reply_empty(codec: TransformCodecV1) -> None:
    msg = ResyncReplyMessage(room_id="", base_room_seq=0, server_time_us=0, entities=[])
    decoded = MessageCodec.decode_resync_reply(
        MessageCodec.encode_resync_reply(msg, codec), codec
    )
    assert decoded == msg


def test_state_batch_round_trip(codec: TransformCodecV1) -> None:
    msg = StateBatchMessage(
        room_seq=42,
        server_time_us=1_700_000_000_000_000,
        updates=[
            StateUpdate(
                entity_id=1,
                authority_epoch=5,
                pose_seq=10,
                flags=StateFlags.KEYFRAME | StateFlags.TELEPORT,
                changed_mask=ChangedMask.ALL,
                state=_sample_state(4.0),
            ),
            StateUpdate(
                entity_id=2,
                authority_epoch=5,
                pose_seq=11,
                flags=StateFlags.HEARTBEAT,
                changed_mask=ChangedMask.NONE,
                state=WireTransform(),
            ),
        ],
    )
    encoded = MessageCodec.encode_state_batch(msg, codec)
    assert encoded[0] == MSG_REPL_STATE_BATCH
    decoded = MessageCodec.decode_state_batch(encoded, codec)
    assert decoded.room_seq == 42
    assert decoded.server_time_us == 1_700_000_000_000_000
    assert len(decoded.updates) == 2
    assert decoded.updates[0].flags == StateFlags.KEYFRAME | StateFlags.TELEPORT
    assert decoded.updates[0].state.position == msg.updates[0].state.position
    assert decoded.updates[1].flags == StateFlags.HEARTBEAT
    assert decoded.updates[1].changed_mask == ChangedMask.NONE


def test_state_batch_empty(codec: TransformCodecV1) -> None:
    msg = StateBatchMessage(room_seq=0, server_time_us=0, updates=[])
    decoded = MessageCodec.decode_state_batch(
        MessageCodec.encode_state_batch(msg, codec), codec
    )
    assert decoded == msg


def test_reject_unknown_version(codec: TransformCodecV1) -> None:
    encoded = bytearray(
        MessageCodec.encode_join_room(
            JoinRoomMessage(room_id="a", device_id="b", scene_hash="c")
        )
    )
    encoded[1] = 99
    with pytest.raises(ValueError, match="Unsupported"):
        MessageCodec.decode_join_room(bytes(encoded))


def test_reject_wrong_message_type() -> None:
    encoded = MessageCodec.encode_join_room(JoinRoomMessage(room_id="a", device_id="b"))
    with pytest.raises(ValueError, match="Unexpected message type"):
        MessageCodec.decode_ownership_request(encoded)


def test_short_string_overflow_rejected() -> None:
    huge = "x" * 300
    with pytest.raises(ValueError, match="exceeds 255"):
        MessageCodec.encode_join_room(
            JoinRoomMessage(room_id=huge, device_id="", scene_hash="")
        )
