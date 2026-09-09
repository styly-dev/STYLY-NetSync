"""Tests for the discovery port conflict probe."""

from __future__ import annotations

import socket
import threading
from dataclasses import replace
from unittest.mock import patch

from styly_netsync.config import load_default_config
from styly_netsync.server import NetSyncServer


def _find_free_port() -> int:
    """Return a free UDP port."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.bind(("", 0))
        return int(s.getsockname()[1])


class TestDiscoveryProbe:
    """Tests for _probe_existing_discovery_server."""

    def test_builds_current_discovery_response_with_rest_port(self) -> None:
        """Discovery response should include the REST bridge port."""
        config = replace(load_default_config(), rest_api_port=9900)
        server = NetSyncServer(
            dealer_port=5555,
            transform_port=5557,
            pub_port=5556,
            server_name="TestServer",
            config=config,
        )

        expected = "STYLY-NETSYNC3|5555|5557|5556|9900|TestServer"
        assert server._build_discovery_response() == expected
        assert server._build_discovery_response(newline=True) == f"{expected}\n"

    def test_no_conflict_when_no_other_server(self) -> None:
        """Probe should return None when nobody responds."""
        port = _find_free_port()
        server = NetSyncServer(
            dealer_port=_find_free_port(),
            pub_port=_find_free_port(),
            server_discovery_port=port,
            enable_server_discovery=False,
        )
        assert server._probe_existing_discovery_server() is None

    def test_detects_conflict_when_another_server_responds(self) -> None:
        """Probe should return a conflict description when a server responds."""
        port = _find_free_port()

        # Simulate an existing server that responds to DISCOVER probes
        stop_event = threading.Event()
        ready_event = threading.Event()

        def fake_server() -> None:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            sock.bind(("", port))
            sock.settimeout(0.5)
            ready_event.set()
            while not stop_event.is_set():
                try:
                    data, addr = sock.recvfrom(1024)
                    if data == b"STYLY-NETSYNC-DISCOVER":
                        response = b"STYLY-NETSYNC3|5555|5557|5556|8800|FakeServer"
                        sock.sendto(response, addr)
                except TimeoutError:
                    continue
            sock.close()

        t = threading.Thread(target=fake_server, daemon=True)
        t.start()
        assert ready_event.wait(
            timeout=2
        ), "Fake discovery server did not become ready in time"

        try:
            server = NetSyncServer(
                dealer_port=_find_free_port(),
                pub_port=_find_free_port(),
                server_discovery_port=port,
                enable_server_discovery=False,
            )
            conflict = server._probe_existing_discovery_server()

            assert conflict is not None
            assert "Another STYLY-NetSync server" in conflict
            assert "FakeServer" in conflict
            assert str(port) in conflict
        finally:
            stop_event.set()
            t.join(timeout=2)

    def test_probe_does_not_block_on_exception(self) -> None:
        """Probe should return None (not raise) if socket operations fail."""
        server = NetSyncServer(
            dealer_port=_find_free_port(),
            pub_port=_find_free_port(),
            server_discovery_port=_find_free_port(),
            enable_server_discovery=False,
        )
        with patch("socket.socket", side_effect=OSError("mock error")):
            assert server._probe_existing_discovery_server() is None


class TestParseDiscoveryServerName:
    """Tests for _parse_discovery_server_name against the current format."""

    def test_parses_current_v3_response(self) -> None:
        name = NetSyncServer._parse_discovery_server_name(
            "STYLY-NETSYNC3|5555|5557|5556|8800|MyServer"
        )
        assert name == "MyServer"

    def test_v3_name_may_contain_pipe(self) -> None:
        # maxsplit keeps everything after the 5th '|' as the name.
        name = NetSyncServer._parse_discovery_server_name(
            "STYLY-NETSYNC3|5555|5557|5556|8800|Room|A"
        )
        assert name == "Room|A"

    def test_rejects_older_response_formats(self) -> None:
        # Only the current format counts as a conflict; older shapes are simply
        # not a compatible server (see the repo backward-compatibility policy).
        assert (
            NetSyncServer._parse_discovery_server_name(
                "STYLY-NETSYNC2|5555|5557|5556|LegacyServer"
            )
            is None
        )
        assert (
            NetSyncServer._parse_discovery_server_name(
                "STYLY-NETSYNC|5555|5557|OldServer"
            )
            is None
        )

    def test_rejects_unrelated_payload(self) -> None:
        assert NetSyncServer._parse_discovery_server_name("HELLO-WORLD") is None

    def test_rejects_non_integer_ports(self) -> None:
        assert (
            NetSyncServer._parse_discovery_server_name(
                "STYLY-NETSYNC3|5555|abc|5556|8800|MyServer"
            )
            is None
        )

    def test_rejects_truncated_response(self) -> None:
        assert (
            NetSyncServer._parse_discovery_server_name("STYLY-NETSYNC3|5555|5557")
            is None
        )
