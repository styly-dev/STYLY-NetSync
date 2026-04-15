"""Replication core for the NetSync object redesign.

This package holds the server-side building blocks for the shared
ReplicationCore: domain models, room registry, ownership arbitration,
state relay, snapshot service, and the wire-level message codec.

Phase 1 only populates ``models`` and ``room_registry``; the remaining
modules are intentionally left as empty stubs for later phases.
"""

from .dispatcher import DispatchResult, ReplicationDispatcher, SendFn
from .message_codec import (
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
    TransformCodec,
    TransformCodecV1,
)
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
from .models import EntityKind, EntityRecord, TransformState
from .room_registry import ClientState, RoomRegistry, RoomState
from .snapshot_service import SnapshotService
from .state_relay import StateRelay

__all__ = [
    "ChangedMask",
    "ClientState",
    "DispatchResult",
    "EntityKind",
    "EntityRecord",
    "JoinRejectMessage",
    "JoinRejectReason",
    "JoinRoomMessage",
    "MSG_REPL_JOIN_REJECT",
    "MSG_REPL_JOIN_ROOM",
    "MSG_REPL_OWNERSHIP_EVENT",
    "MSG_REPL_OWNERSHIP_REQUEST",
    "MSG_REPL_RESYNC_REPLY",
    "MSG_REPL_RESYNC_REQUEST",
    "MSG_REPL_ROOM_SNAPSHOT",
    "MSG_REPL_STATE_BATCH",
    "MessageCodec",
    "OwnershipEventMessage",
    "OwnershipEventReasonCode",
    "OwnershipRequestMessage",
    "OwnershipResult",
    "REPL_PROTOCOL_VERSION",
    "ReplicationDispatcher",
    "ResyncReplyMessage",
    "ResyncRequestMessage",
    "RoomRegistry",
    "RoomSnapshotMessage",
    "RoomState",
    "SendFn",
    "SnapshotService",
    "StateBatchMessage",
    "StateRelay",
    "StateFlags",
    "StateUpdate",
    "TransformCodec",
    "TransformCodecV1",
    "TransformState",
    "WireEntityRecord",
    "WireTransform",
]
