"""Encode/decode for replication-protocol-v1 messages.

All primitives are little-endian; layout mirrors Unity's
``MessageCodec.cs``. See ``docs/replication-protocol-v1.md`` for the
wire format definition.
"""

from __future__ import annotations

import logging
import struct
from typing import Protocol

from .messages import (
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

logger = logging.getLogger(__name__)

REPL_PROTOCOL_VERSION: int = 1

MSG_REPL_JOIN_ROOM: int = 30
MSG_REPL_ROOM_SNAPSHOT: int = 31
MSG_REPL_OWNERSHIP_REQUEST: int = 32
MSG_REPL_OWNERSHIP_EVENT: int = 33
MSG_REPL_RESYNC_REQUEST: int = 34
MSG_REPL_RESYNC_REPLY: int = 35
MSG_REPL_STATE_BATCH: int = 36
MSG_REPL_JOIN_REJECT: int = 37


class TransformCodec(Protocol):
    """Swappable transform codec. v1 uses float32; a later version may
    introduce quantization without touching the surrounding framing."""

    version: int

    def write(
        self, buf: bytearray, mask: ChangedMask, state: WireTransform
    ) -> None: ...

    def read(
        self, data: bytes, offset: int, mask: ChangedMask
    ) -> tuple[WireTransform, int]: ...


class TransformCodecV1:
    """Float32 transform codec."""

    version: int = 1

    def write(self, buf: bytearray, mask: ChangedMask, state: WireTransform) -> None:
        if mask & ChangedMask.POSITION:
            buf.extend(struct.pack("<fff", *state.position))
        if mask & ChangedMask.ROTATION:
            buf.extend(struct.pack("<ffff", *state.rotation))
        if mask & ChangedMask.SCALE:
            buf.extend(struct.pack("<fff", *state.scale))

    def read(
        self, data: bytes, offset: int, mask: ChangedMask
    ) -> tuple[WireTransform, int]:
        position: tuple[float, float, float] = (0.0, 0.0, 0.0)
        rotation: tuple[float, float, float, float] = (0.0, 0.0, 0.0, 1.0)
        scale: tuple[float, float, float] = (1.0, 1.0, 1.0)
        if mask & ChangedMask.POSITION:
            position = struct.unpack_from("<fff", data, offset)
            offset += 12
        if mask & ChangedMask.ROTATION:
            rotation = struct.unpack_from("<ffff", data, offset)
            offset += 16
        if mask & ChangedMask.SCALE:
            scale = struct.unpack_from("<fff", data, offset)
            offset += 12
        return (
            WireTransform(position=position, rotation=rotation, scale=scale),
            offset,
        )


# --- Internal helpers ---


def _write_header(buf: bytearray, msg_type: int) -> None:
    buf.append(msg_type)
    buf.append(REPL_PROTOCOL_VERSION)


def _read_header(data: bytes, expected: int) -> int:
    if len(data) < 2:
        raise ValueError("Buffer too short for header")
    if data[0] != expected:
        raise ValueError(f"Unexpected message type {data[0]} (expected {expected})")
    if data[1] != REPL_PROTOCOL_VERSION:
        raise ValueError(f"Unsupported replication protocol version {data[1]}")
    return 2


def _write_short_string(buf: bytearray, value: str) -> None:
    encoded = value.encode("utf-8") if value else b""
    if len(encoded) > 255:
        raise ValueError(f"Short string length {len(encoded)} exceeds 255 bytes")
    buf.append(len(encoded))
    buf.extend(encoded)


def _read_short_string(data: bytes, offset: int) -> tuple[str, int]:
    length = data[offset]
    offset += 1
    end = offset + length
    return data[offset:end].decode("utf-8"), end


_RECORD_HEADER_FMT = "<QIIHB"
_RECORD_HEADER_SIZE = struct.calcsize(_RECORD_HEADER_FMT)
_UPDATE_HEADER_FMT = "<QIHBB"
_UPDATE_HEADER_SIZE = struct.calcsize(_UPDATE_HEADER_FMT)


def _write_entity_record(
    buf: bytearray, record: WireEntityRecord, codec: TransformCodec
) -> None:
    buf.extend(
        struct.pack(
            _RECORD_HEADER_FMT,
            record.entity_id,
            record.authority_epoch,
            record.owner_short_id,
            record.pose_seq,
            int(record.changed_mask),
        )
    )
    codec.write(buf, record.changed_mask, record.state)


def _read_entity_record(
    data: bytes, offset: int, codec: TransformCodec
) -> tuple[WireEntityRecord, int]:
    entity_id, authority_epoch, owner_short_id, pose_seq, mask_raw = struct.unpack_from(
        _RECORD_HEADER_FMT, data, offset
    )
    offset += _RECORD_HEADER_SIZE
    mask = ChangedMask(mask_raw)
    state, offset = codec.read(data, offset, mask)
    return (
        WireEntityRecord(
            entity_id=entity_id,
            authority_epoch=authority_epoch,
            owner_short_id=owner_short_id,
            pose_seq=pose_seq,
            changed_mask=mask,
            state=state,
        ),
        offset,
    )


def _write_state_update(
    buf: bytearray, update: StateUpdate, codec: TransformCodec
) -> None:
    buf.extend(
        struct.pack(
            _UPDATE_HEADER_FMT,
            update.entity_id,
            update.authority_epoch,
            update.pose_seq,
            int(update.flags),
            int(update.changed_mask),
        )
    )
    codec.write(buf, update.changed_mask, update.state)


def _read_state_update(
    data: bytes, offset: int, codec: TransformCodec
) -> tuple[StateUpdate, int]:
    entity_id, authority_epoch, pose_seq, flags_raw, mask_raw = struct.unpack_from(
        _UPDATE_HEADER_FMT, data, offset
    )
    offset += _UPDATE_HEADER_SIZE
    mask = ChangedMask(mask_raw)
    state, offset = codec.read(data, offset, mask)
    return (
        StateUpdate(
            entity_id=entity_id,
            authority_epoch=authority_epoch,
            pose_seq=pose_seq,
            flags=StateFlags(flags_raw),
            changed_mask=mask,
            state=state,
        ),
        offset,
    )


class MessageCodec:
    """Static encoder/decoder for replication protocol v1 messages."""

    @staticmethod
    def peek_message_type(data: bytes) -> int:
        if not data:
            return 0
        return data[0]

    # --- JOIN_ROOM ---

    @staticmethod
    def encode_join_room(message: JoinRoomMessage) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_JOIN_ROOM)
        _write_short_string(buf, message.room_id)
        _write_short_string(buf, message.device_id)
        _write_short_string(buf, message.scene_hash)
        return bytes(buf)

    @staticmethod
    def decode_join_room(data: bytes) -> JoinRoomMessage:
        offset = _read_header(data, MSG_REPL_JOIN_ROOM)
        room_id, offset = _read_short_string(data, offset)
        device_id, offset = _read_short_string(data, offset)
        scene_hash, _ = _read_short_string(data, offset)
        return JoinRoomMessage(
            room_id=room_id, device_id=device_id, scene_hash=scene_hash
        )

    # --- ROOM_SNAPSHOT ---

    @staticmethod
    def encode_room_snapshot(
        message: RoomSnapshotMessage, codec: TransformCodec
    ) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_ROOM_SNAPSHOT)
        _write_short_string(buf, message.room_id)
        # baseRoomSeq (u32) + serverTimeUs (u64) + yourClientNo (u32) + entity count (u32)
        buf.extend(
            struct.pack(
                "<IQII",
                message.base_room_seq,
                message.server_time_us,
                message.your_client_no,
                len(message.entities),
            )
        )
        for record in message.entities:
            _write_entity_record(buf, record, codec)
        return bytes(buf)

    @staticmethod
    def decode_room_snapshot(data: bytes, codec: TransformCodec) -> RoomSnapshotMessage:
        offset = _read_header(data, MSG_REPL_ROOM_SNAPSHOT)
        room_id, offset = _read_short_string(data, offset)
        base_room_seq, server_time_us, your_client_no, count = struct.unpack_from(
            "<IQII", data, offset
        )
        offset += 20
        entities: list[WireEntityRecord] = []
        for _ in range(count):
            record, offset = _read_entity_record(data, offset, codec)
            entities.append(record)
        return RoomSnapshotMessage(
            room_id=room_id,
            base_room_seq=base_room_seq,
            server_time_us=server_time_us,
            your_client_no=your_client_no,
            entities=entities,
        )

    # --- JOIN_REJECT ---

    @staticmethod
    def encode_join_reject(message: JoinRejectMessage) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_JOIN_REJECT)
        _write_short_string(buf, message.room_id)
        buf.append(int(message.reason))
        _write_short_string(buf, message.reason_text)
        return bytes(buf)

    @staticmethod
    def decode_join_reject(data: bytes) -> JoinRejectMessage:
        offset = _read_header(data, MSG_REPL_JOIN_REJECT)
        room_id, offset = _read_short_string(data, offset)
        raw_reason = data[offset]
        offset += 1
        try:
            reason = JoinRejectReason(raw_reason)
        except ValueError:
            logger.warning(
                "Unknown JoinRejectReason %d; coercing to UNSPECIFIED", raw_reason
            )
            reason = JoinRejectReason.UNSPECIFIED
        reason_text, _ = _read_short_string(data, offset)
        return JoinRejectMessage(
            room_id=room_id, reason=reason, reason_text=reason_text
        )

    # --- OWNERSHIP_REQUEST ---

    @staticmethod
    def encode_ownership_request(message: OwnershipRequestMessage) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_OWNERSHIP_REQUEST)
        buf.extend(
            struct.pack(
                "<QII",
                message.entity_id,
                message.requester_short_id,
                message.expected_epoch,
            )
        )
        return bytes(buf)

    @staticmethod
    def decode_ownership_request(data: bytes) -> OwnershipRequestMessage:
        offset = _read_header(data, MSG_REPL_OWNERSHIP_REQUEST)
        entity_id, requester, expected_epoch = struct.unpack_from("<QII", data, offset)
        return OwnershipRequestMessage(
            entity_id=entity_id,
            requester_short_id=requester,
            expected_epoch=expected_epoch,
        )

    # --- OWNERSHIP_EVENT ---

    @staticmethod
    def encode_ownership_event(message: OwnershipEventMessage) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_OWNERSHIP_EVENT)
        buf.extend(
            struct.pack(
                "<QIIBB",
                message.entity_id,
                message.new_owner_short_id,
                message.new_authority_epoch,
                int(message.result),
                int(message.reason_code),
            )
        )
        return bytes(buf)

    @staticmethod
    def decode_ownership_event(data: bytes) -> OwnershipEventMessage:
        offset = _read_header(data, MSG_REPL_OWNERSHIP_EVENT)
        entity_id, owner, epoch, raw_result, raw_reason = struct.unpack_from(
            "<QIIBB", data, offset
        )
        try:
            result = OwnershipResult(raw_result)
        except ValueError as exc:
            raise ValueError(f"Unknown OwnershipResult {raw_result}") from exc
        try:
            reason_code = OwnershipEventReasonCode(raw_reason)
        except ValueError:
            logger.warning(
                "Unknown OwnershipEventReasonCode %d; coercing to NONE", raw_reason
            )
            reason_code = OwnershipEventReasonCode.NONE
        return OwnershipEventMessage(
            entity_id=entity_id,
            new_owner_short_id=owner,
            new_authority_epoch=epoch,
            result=result,
            reason_code=reason_code,
        )

    # --- RESYNC_REQUEST ---

    @staticmethod
    def encode_resync_request(message: ResyncRequestMessage) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_RESYNC_REQUEST)
        buf.extend(struct.pack("<I", message.last_applied_room_seq))
        return bytes(buf)

    @staticmethod
    def decode_resync_request(data: bytes) -> ResyncRequestMessage:
        offset = _read_header(data, MSG_REPL_RESYNC_REQUEST)
        (last_applied_room_seq,) = struct.unpack_from("<I", data, offset)
        return ResyncRequestMessage(last_applied_room_seq=last_applied_room_seq)

    # --- RESYNC_REPLY ---

    @staticmethod
    def encode_resync_reply(
        message: ResyncReplyMessage, codec: TransformCodec
    ) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_RESYNC_REPLY)
        _write_short_string(buf, message.room_id)
        # baseRoomSeq (u32) + serverTimeUs (u64) + entity count (u32)
        buf.extend(
            struct.pack(
                "<IQI",
                message.base_room_seq,
                message.server_time_us,
                len(message.entities),
            )
        )
        for record in message.entities:
            _write_entity_record(buf, record, codec)
        return bytes(buf)

    @staticmethod
    def decode_resync_reply(data: bytes, codec: TransformCodec) -> ResyncReplyMessage:
        offset = _read_header(data, MSG_REPL_RESYNC_REPLY)
        room_id, offset = _read_short_string(data, offset)
        base_room_seq, server_time_us, count = struct.unpack_from("<IQI", data, offset)
        offset += 16
        entities: list[WireEntityRecord] = []
        for _ in range(count):
            record, offset = _read_entity_record(data, offset, codec)
            entities.append(record)
        return ResyncReplyMessage(
            room_id=room_id,
            base_room_seq=base_room_seq,
            server_time_us=server_time_us,
            entities=entities,
        )

    # --- STATE_BATCH ---

    @staticmethod
    def encode_state_batch(message: StateBatchMessage, codec: TransformCodec) -> bytes:
        buf = bytearray()
        _write_header(buf, MSG_REPL_STATE_BATCH)
        # roomSeq (u32) + serverTimeUs (u64) + updateCount (u32)
        buf.extend(
            struct.pack(
                "<IQI",
                message.room_seq,
                message.server_time_us,
                len(message.updates),
            )
        )
        for update in message.updates:
            _write_state_update(buf, update, codec)
        return bytes(buf)

    @staticmethod
    def decode_state_batch(data: bytes, codec: TransformCodec) -> StateBatchMessage:
        offset = _read_header(data, MSG_REPL_STATE_BATCH)
        room_seq, server_time_us, count = struct.unpack_from("<IQI", data, offset)
        offset += 16
        updates: list[StateUpdate] = []
        for _ in range(count):
            update, offset = _read_state_update(data, offset, codec)
            updates.append(update)
        return StateBatchMessage(
            room_seq=room_seq,
            server_time_us=server_time_us,
            updates=updates,
        )


__all__ = [
    "MSG_REPL_JOIN_REJECT",
    "MSG_REPL_JOIN_ROOM",
    "MSG_REPL_OWNERSHIP_EVENT",
    "MSG_REPL_OWNERSHIP_REQUEST",
    "MSG_REPL_RESYNC_REPLY",
    "MSG_REPL_RESYNC_REQUEST",
    "MSG_REPL_ROOM_SNAPSHOT",
    "MSG_REPL_STATE_BATCH",
    "MessageCodec",
    "REPL_PROTOCOL_VERSION",
    "TransformCodec",
    "TransformCodecV1",
]
