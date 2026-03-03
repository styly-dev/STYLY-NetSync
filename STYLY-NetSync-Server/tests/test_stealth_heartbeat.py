"""
Tests for the stealth client heartbeat mechanism.

Verifies that net_sync_manager automatically sends keepalive heartbeats
at 1 Hz once send_stealth_handshake() is called, matching Unity's
TransformSyncManager.HEARTBEAT_INTERVAL_SECONDS behaviour.
"""

from __future__ import annotations

import threading
import time
import unittest
from unittest.mock import MagicMock, patch

from styly_netsync.client import (
    STEALTH_HEARTBEAT_INTERVAL,
    net_sync_manager,
)


class TestStealthHeartbeatUnit(unittest.TestCase):
    """Unit-level tests that exercise heartbeat logic without real sockets."""

    def _make_running_manager(self) -> net_sync_manager:
        """Return a manager whose internal state mimics a connected client."""
        mgr = net_sync_manager()
        mgr._running = True
        mgr._dealer_socket = MagicMock()  # fake socket so guards pass
        return mgr

    # ------------------------------------------------------------------
    # State flag tests
    # ------------------------------------------------------------------

    def test_is_stealth_mode_starts_false(self) -> None:
        mgr = net_sync_manager()
        self.assertFalse(mgr._is_stealth_mode)

    def test_send_stealth_handshake_sets_stealth_mode(self) -> None:
        mgr = self._make_running_manager()
        with patch.object(mgr, "_enqueue_control", return_value=True):
            mgr.send_stealth_handshake()
        self.assertTrue(mgr._is_stealth_mode)

    def test_send_stealth_handshake_returns_false_when_not_running(self) -> None:
        mgr = net_sync_manager()  # _running is False by default
        result = mgr.send_stealth_handshake()
        self.assertFalse(result)
        self.assertFalse(mgr._is_stealth_mode)

    # ------------------------------------------------------------------
    # Heartbeat timer tests
    # ------------------------------------------------------------------

    def test_send_stealth_handshake_updates_heartbeat_time(self) -> None:
        mgr = self._make_running_manager()
        before = time.monotonic()
        with patch.object(mgr, "_enqueue_control", return_value=True):
            mgr.send_stealth_handshake()
        self.assertGreaterEqual(mgr._last_stealth_heartbeat_time, before)

    def test_maybe_send_stealth_heartbeat_noop_when_not_stealth(self) -> None:
        mgr = self._make_running_manager()
        enqueue_calls: list[None] = []
        with patch.object(
            mgr,
            "_enqueue_control",
            side_effect=lambda *a, **kw: enqueue_calls.append(None) or True,
        ):
            mgr._maybe_send_stealth_heartbeat()
        self.assertEqual(len(enqueue_calls), 0)

    def test_maybe_send_stealth_heartbeat_noop_before_interval(self) -> None:
        mgr = self._make_running_manager()
        mgr._is_stealth_mode = True
        mgr._last_stealth_heartbeat_time = time.monotonic()  # just sent

        enqueue_calls: list[None] = []
        with patch.object(
            mgr,
            "_enqueue_control",
            side_effect=lambda *a, **kw: enqueue_calls.append(None) or True,
        ):
            mgr._maybe_send_stealth_heartbeat()
        self.assertEqual(len(enqueue_calls), 0)

    def test_maybe_send_stealth_heartbeat_fires_after_interval(self) -> None:
        mgr = self._make_running_manager()
        mgr._is_stealth_mode = True
        # Simulate that the last heartbeat was sent long ago
        mgr._last_stealth_heartbeat_time = time.monotonic() - (
            STEALTH_HEARTBEAT_INTERVAL + 0.1
        )

        enqueue_calls: list[None] = []
        with patch.object(
            mgr,
            "_enqueue_control",
            side_effect=lambda *a, **kw: enqueue_calls.append(None) or True,
        ):
            mgr._maybe_send_stealth_heartbeat()
        self.assertEqual(len(enqueue_calls), 1)

    def test_heartbeat_resets_timer(self) -> None:
        """After _send_stealth_heartbeat succeeds, the timer should be updated."""
        mgr = self._make_running_manager()
        old_time = time.monotonic() - 10.0
        mgr._last_stealth_heartbeat_time = old_time

        before = time.monotonic()
        with patch.object(mgr, "_enqueue_control", return_value=True):
            mgr._send_stealth_heartbeat()
        self.assertGreaterEqual(mgr._last_stealth_heartbeat_time, before)

    def test_heartbeat_timer_not_updated_on_enqueue_failure(self) -> None:
        """If _enqueue_control returns False (queue full), timer must not advance."""
        mgr = self._make_running_manager()
        old_time = time.monotonic() - 10.0
        mgr._last_stealth_heartbeat_time = old_time

        with patch.object(mgr, "_enqueue_control", return_value=False):
            mgr._send_stealth_heartbeat()
        self.assertAlmostEqual(mgr._last_stealth_heartbeat_time, old_time, places=3)

    # ------------------------------------------------------------------
    # Constant value test
    # ------------------------------------------------------------------

    def test_heartbeat_interval_is_one_second(self) -> None:
        """STEALTH_HEARTBEAT_INTERVAL must match Unity's HEARTBEAT_INTERVAL_SECONDS = 1f."""
        self.assertEqual(STEALTH_HEARTBEAT_INTERVAL, 1.0)


class TestStealthHeartbeatIntegration(unittest.TestCase):
    """Integration tests with a real server to verify keepalive works end-to-end."""

    def test_stealth_client_stays_connected_beyond_server_timeout(self) -> None:
        """Stealth client should remain registered after CLIENT_TIMEOUT expires."""
        import dataclasses

        from styly_netsync import NetSyncServer, net_sync_manager
        from styly_netsync.config import load_default_config

        # Use a very short client timeout so the test finishes quickly
        SHORT_TIMEOUT = 1.0
        config = dataclasses.replace(
            load_default_config(), client_timeout=SHORT_TIMEOUT
        )

        server = NetSyncServer(
            dealer_port=15600,
            pub_port=15601,
            enable_server_discovery=False,
            config=config,
        )
        server_thread = threading.Thread(target=server.start, daemon=True)
        server_thread.start()
        time.sleep(0.3)  # wait for server to bind

        client = None
        try:
            client = net_sync_manager(
                server="tcp://localhost",
                dealer_port=15600,
                sub_port=15601,
                room="heartbeat_test",
            )
            client.start()
            time.sleep(0.3)  # wait for client number assignment

            # Enter stealth mode
            result = client.send_stealth_handshake()
            self.assertTrue(result, "send_stealth_handshake should succeed")
            self.assertTrue(client._is_stealth_mode)

            # Wait for a duration longer than the short server timeout.
            # The 1 Hz heartbeat should keep the client registered.
            wait = SHORT_TIMEOUT * 2.5
            time.sleep(wait)

            # Client should still be registered (client_no assigned and still in mapping)
            client_no = client.client_no
            self.assertIsNotNone(
                client_no, "Client should still have an assigned number"
            )

            # Also verify via server: device should still be in a room
            with server._rooms_lock:
                room_exists = "heartbeat_test" in server.rooms
            self.assertTrue(
                room_exists, "Room should still exist with the stealth client"
            )

        finally:
            if client is not None:
                client.stop()
            server.stop()


if __name__ == "__main__":
    unittest.main()
