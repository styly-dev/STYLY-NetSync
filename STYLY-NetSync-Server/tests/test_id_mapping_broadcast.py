"""Unit tests for ID mapping broadcasts."""

from __future__ import annotations

from styly_netsync import binary_serializer
from styly_netsync.config import load_default_config
from styly_netsync.server import NetSyncServer


def test_broadcast_id_mapping_sends_empty_payload_for_stealth_only_room() -> None:
    """Server should broadcast empty mappings so clients can clear stale state."""
    config = load_default_config()
    server = NetSyncServer(config=config)

    room_id = "room_stealth_only"
    stealth_device_id = "stealth-device"

    # Keep room mapping table alive, but ensure no connected clients currently exist.
    server.room_device_id_to_client_no[room_id] = {stealth_device_id: 7}
    server.rooms[room_id] = {}

    sent_messages: list[bytes] = []

    def capture(_room_id: str, message: bytes, _exclude_identity: bytes | None = None) -> None:
        sent_messages.append(message)

    server._send_ctrl_to_room_via_router = capture  # type: ignore[method-assign]

    server._broadcast_id_mappings(room_id)

    assert len(sent_messages) == 1
    msg_type, payload, _ = binary_serializer.deserialize(sent_messages[0])
    assert msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING
    assert payload["mappings"] == []

