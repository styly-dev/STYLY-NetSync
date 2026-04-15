"""Replication core for the NetSync object redesign.

This package holds the server-side building blocks for the shared
ReplicationCore: domain models, room registry, ownership arbitration,
state relay, snapshot service, and the wire-level message codec.

Phase 1 only populates ``models`` and ``room_registry``; the remaining
modules are intentionally left as empty stubs for later phases.
"""

from .message_codec import (
    MSG_REPL_JOIN_ROOM,
    MSG_REPL_OWNERSHIP_EVENT,
    MSG_REPL_OWNERSHIP_REQUEST,
    MSG_REPL_RESYNC_REPLY,
    MSG_REPL_RESYNC_REQUEST,
    MSG_REPL_ROOM_SNAPSHOT,
    MSG_REPL_STATE_BATCH,
    REPL_PROTOCOL_VERSION,
    MessageCodec,
    TransformCodec,
    TransformCodecV1,
)
from .messages import (
    ChangedMask,
    JoinRoomMessage,
    OwnershipEventMessage,
    OwnershipReason,
    OwnershipRequestMessage,
    ResyncReplyMessage,
    ResyncRequestMessage,
    RoomSnapshotMessage,
    StateBatchMessage,
    StateFlags,
    StateUpdate,
    WireEntityRecord,
    WireTransform,
)
from .models import EntityKind, EntityRecord, TransformState
from .room_registry import ClientState, RoomRegistry, RoomState

__all__ = [
    "ChangedMask",
    "ClientState",
    "EntityKind",
    "EntityRecord",
    "JoinRoomMessage",
    "MSG_REPL_JOIN_ROOM",
    "MSG_REPL_OWNERSHIP_EVENT",
    "MSG_REPL_OWNERSHIP_REQUEST",
    "MSG_REPL_RESYNC_REPLY",
    "MSG_REPL_RESYNC_REQUEST",
    "MSG_REPL_ROOM_SNAPSHOT",
    "MSG_REPL_STATE_BATCH",
    "MessageCodec",
    "OwnershipEventMessage",
    "OwnershipReason",
    "OwnershipRequestMessage",
    "REPL_PROTOCOL_VERSION",
    "ResyncReplyMessage",
    "ResyncRequestMessage",
    "RoomRegistry",
    "RoomSnapshotMessage",
    "RoomState",
    "StateBatchMessage",
    "StateFlags",
    "StateUpdate",
    "TransformCodec",
    "TransformCodecV1",
    "TransformState",
    "WireEntityRecord",
    "WireTransform",
]
