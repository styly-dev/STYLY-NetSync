"""Tests for multi-NIC discovery and _build_connect_addr."""

import socket
from unittest.mock import MagicMock, patch

from styly_netsync.client import net_sync_manager


def _make_manager() -> net_sync_manager:
    """Create a manager without starting networking."""
    return net_sync_manager(server="tcp://192.168.1.100", room="test")


class TestBuildConnectAddr:
    """Tests for _build_connect_addr."""

    def test_localhost_returns_simple_format(self) -> None:
        mgr = net_sync_manager(server="tcp://localhost", room="test")
        addr = mgr._build_connect_addr(5555)
        assert addr == "tcp://localhost:5555"

    def test_127_0_0_1_returns_simple_format(self) -> None:
        mgr = net_sync_manager(server="tcp://127.0.0.1", room="test")
        addr = mgr._build_connect_addr(5555)
        assert addr == "tcp://127.0.0.1:5555"

    def test_remote_host_uses_extended_tcp_format(self) -> None:
        mgr = _make_manager()
        with patch.object(
            net_sync_manager,
            "_resolve_source_address",
            return_value="10.0.0.5",
        ):
            addr = mgr._build_connect_addr(5555)
        assert addr == "tcp://10.0.0.5:0;192.168.1.100:5555"

    def test_remote_host_falls_back_when_resolve_returns_none(self) -> None:
        mgr = _make_manager()
        with patch.object(
            net_sync_manager,
            "_resolve_source_address",
            return_value=None,
        ):
            addr = mgr._build_connect_addr(5555)
        assert addr == "tcp://192.168.1.100:5555"


class TestGetBroadcastAddress:
    """Tests for _get_broadcast_address."""

    def test_computes_broadcast_from_psutil(self) -> None:
        mgr = _make_manager()
        # Mock psutil.net_if_addrs() to return a known interface
        fake_addr = MagicMock()
        fake_addr.family = socket.AF_INET
        fake_addr.address = "192.168.1.10"
        fake_addr.netmask = "255.255.255.0"

        with patch("psutil.net_if_addrs", return_value={"eth0": [fake_addr]}):
            bcast = mgr._get_broadcast_address("192.168.1.10")
        assert bcast == "192.168.1.255"

    def test_falls_back_to_generic_broadcast(self) -> None:
        mgr = _make_manager()
        with patch("psutil.net_if_addrs", return_value={}):
            bcast = mgr._get_broadcast_address("10.99.99.99")
        assert bcast == "255.255.255.255"

    def test_falls_back_on_psutil_exception(self) -> None:
        mgr = _make_manager()
        with patch("psutil.net_if_addrs", side_effect=RuntimeError("boom")):
            bcast = mgr._get_broadcast_address("10.0.0.1")
        assert bcast == "255.255.255.255"


class TestDiscoverySocketsCleanup:
    """Tests that start_discovery cleans up sockets on failure."""

    def test_sockets_cleaned_up_on_error(self) -> None:
        mgr = _make_manager()
        mock_sock = MagicMock(spec=socket.socket)

        with (
            patch(
                "styly_netsync.client.net_sync_manager._get_broadcast_address",
                return_value="192.168.1.255",
            ),
            patch(
                "styly_netsync.network_utils.get_local_ip_addresses",
                return_value=["192.168.1.10"],
            ),
            patch(
                "socket.socket",
                return_value=mock_sock,
            ),
            patch.object(
                # Force an error after sockets are created but before thread starts
                mgr,
                "_discovery_running",
                new=False,
            ),
        ):
            # Make the Thread constructor raise to trigger the except path
            with patch("threading.Thread", side_effect=RuntimeError("thread error")):
                mgr.start_discovery(9999)

        # Socket should have been closed during cleanup
        mock_sock.close.assert_called()
        assert mgr._discovery_sockets == []
        assert mgr._discovery_socket is None
        assert mgr._discovery_running is False
