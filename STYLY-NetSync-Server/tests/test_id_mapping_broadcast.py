"""Unit tests for ID mapping broadcasts."""

from __future__ import annotations

from styly_netsync import binary_serializer
from styly_netsync.config import load_default_config
from styly_netsync.server import NetSyncServer


def test_broadcast_id_mappings_sends_empty_mapping_to_clear_stale_clients() -> None:
    """Even with no connected clients, an empty mapping message is still sent."""
    config = load_default_config()
    server = NetSyncServer(config=config)

    room_id = "room_with_only_stealth_observers"
    server.room_device_id_to_client_no[room_id] = {"disconnected_device": 7}
    server.rooms[room_id] = {}

    sent_messages: list[tuple[str, bytes]] = []

    def _record_send(target_room_id: str, payload: bytes) -> None:
        sent_messages.append((target_room_id, payload))

    server._send_ctrl_to_room_via_router = _record_send  # type: ignore[method-assign]

    server._broadcast_id_mappings(room_id)

    assert len(sent_messages) == 1
    target_room_id, payload = sent_messages[0]
    assert target_room_id == room_id

    msg_type, msg_data, _ = binary_serializer.deserialize(payload)
    assert msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING
    assert msg_data == {"mappings": []}

