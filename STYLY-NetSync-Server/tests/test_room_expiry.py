"""Unit tests for empty room expiry functionality."""

from __future__ import annotations

import pytest

from styly_netsync.config import load_default_config
from styly_netsync.server import NetSyncServer


@pytest.fixture
def server() -> NetSyncServer:
    """Create a NetSyncServer instance for testing."""
    config = load_default_config()
    server = NetSyncServer(config=config)
    return server


class TestEmptyRoomExpiry:
    """Tests for empty room expiry tracking and delayed removal."""

    def test_empty_room_is_tracked_when_all_clients_disconnect(
        self, server: NetSyncServer
    ) -> None:
        """Test that empty rooms are tracked when all clients disconnect."""
        room_id = "test_room"
        device_id = "device_001"
        current_time = 1000.0

        # Setup: Add a room with one client
        server.rooms[room_id] = {
            device_id: {
                "last_update": current_time,
                "client_no": 1,
            }
        }
        server.room_dirty_flags[room_id] = False

        # Simulate client timeout (last_update is old)
        server.rooms[room_id][device_id]["last_update"] = (
            current_time - server.CLIENT_TIMEOUT - 1
        )

        # Run cleanup
        server._cleanup_clients(current_time)

        # Verify: Room should be tracked as empty
        assert room_id in server.room_empty_since
        assert server.room_empty_since[room_id] == current_time

        # Verify: Room should NOT be removed yet (just became empty)
        assert room_id in server.rooms

    def test_empty_room_not_removed_before_expiry_time(
        self, server: NetSyncServer
    ) -> None:
        """Test that empty rooms are NOT removed before EMPTY_ROOM_EXPIRY_TIME."""
        room_id = "test_room"
        device_id = "device_001"
        initial_time = 1000.0

        # Setup: Add a room with one client
        server.rooms[room_id] = {
            device_id: {
                "last_update": initial_time,
                "client_no": 1,
            }
        }
        server.room_dirty_flags[room_id] = False

        # Simulate client timeout
        server.rooms[room_id][device_id]["last_update"] = (
            initial_time - server.CLIENT_TIMEOUT - 1
        )

        # Run cleanup - room becomes empty
        server._cleanup_clients(initial_time)
        assert room_id in server.room_empty_since

        # Run cleanup again just before expiry time
        almost_expired_time = initial_time + server.EMPTY_ROOM_EXPIRY_TIME - 1
        server._cleanup_clients(almost_expired_time)

        # Verify: Room should still exist
        assert room_id in server.rooms
        assert room_id in server.room_empty_since

    def test_empty_room_removed_after_expiry_time(self, server: NetSyncServer) -> None:
        """Test that empty rooms are removed after EMPTY_ROOM_EXPIRY_TIME has elapsed."""
        room_id = "test_room"
        device_id = "device_001"
        initial_time = 1000.0

        # Setup: Add a room with one client
        server.rooms[room_id] = {
            device_id: {
                "last_update": initial_time,
                "client_no": 1,
            }
        }
        server.room_dirty_flags[room_id] = False

        # Simulate client timeout
        server.rooms[room_id][device_id]["last_update"] = (
            initial_time - server.CLIENT_TIMEOUT - 1
        )

        # Run cleanup - room becomes empty
        server._cleanup_clients(initial_time)
        assert room_id in server.room_empty_since

        # Run cleanup after expiry time has passed
        expired_time = initial_time + server.EMPTY_ROOM_EXPIRY_TIME + 1
        server._cleanup_clients(expired_time)

        # Verify: Room should be removed
        assert room_id not in server.rooms
        assert room_id not in server.room_empty_since
        assert room_id not in server.room_dirty_flags

    def test_room_tracking_cleared_when_client_rejoins(
        self, server: NetSyncServer
    ) -> None:
        """Test that room tracking is cleared when a client rejoins an empty room."""
        room_id = "test_room"
        device_id = "device_001"
        initial_time = 1000.0

        # Setup: Add a room with one client
        server.rooms[room_id] = {
            device_id: {
                "last_update": initial_time,
                "client_no": 1,
            }
        }
        server.room_dirty_flags[room_id] = False

        # Simulate client timeout
        server.rooms[room_id][device_id]["last_update"] = (
            initial_time - server.CLIENT_TIMEOUT - 1
        )

        # Run cleanup - room becomes empty
        server._cleanup_clients(initial_time)
        assert room_id in server.room_empty_since

        # Simulate new client joining (add client back to room)
        new_device_id = "device_002"
        rejoined_time = initial_time + 100.0
        server.rooms[room_id][new_device_id] = {
            "last_update": rejoined_time,
            "client_no": 2,
        }

        # Run cleanup again
        server._cleanup_clients(rejoined_time)

        # Verify: Empty tracking should be cleared
        assert room_id not in server.room_empty_since

        # Verify: Room should still exist
        assert room_id in server.rooms
        assert new_device_id in server.rooms[room_id]

    def test_multiple_rooms_tracked_independently(self, server: NetSyncServer) -> None:
        """Test that multiple rooms are tracked independently."""
        room_id_1 = "test_room_1"
        room_id_2 = "test_room_2"
        device_id = "device_001"
        initial_time = 1000.0

        # Setup: Add two rooms with clients
        for room_id in [room_id_1, room_id_2]:
            server.rooms[room_id] = {
                device_id: {
                    "last_update": initial_time,
                    "client_no": 1,
                }
            }
            server.room_dirty_flags[room_id] = False

        # Simulate client timeout for room_1 only
        server.rooms[room_id_1][device_id]["last_update"] = (
            initial_time - server.CLIENT_TIMEOUT - 1
        )

        # Run cleanup
        server._cleanup_clients(initial_time)

        # Verify: Only room_1 should be tracked as empty
        assert room_id_1 in server.room_empty_since
        assert room_id_2 not in server.room_empty_since

        # Now timeout room_2
        later_time = initial_time + 100.0
        server.rooms[room_id_2][device_id]["last_update"] = (
            later_time - server.CLIENT_TIMEOUT - 1
        )
        server._cleanup_clients(later_time)

        # Verify: Both rooms should now be tracked
        assert room_id_1 in server.room_empty_since
        assert room_id_2 in server.room_empty_since

        # Verify: Different empty_since timestamps
        assert server.room_empty_since[room_id_1] == initial_time
        assert server.room_empty_since[room_id_2] == later_time

    def test_empty_room_expiry_time_configurable(self) -> None:
        """Test that EMPTY_ROOM_EXPIRY_TIME is set from config."""
        config = load_default_config()
        server = NetSyncServer(config=config)

        # Verify: EMPTY_ROOM_EXPIRY_TIME should match config
        assert server.EMPTY_ROOM_EXPIRY_TIME == config.empty_room_expiry_time
        assert server.EMPTY_ROOM_EXPIRY_TIME == 86400.0  # 24 hours
