"""Tests for the discovery port conflict probe."""

from __future__ import annotations

import io
import socket
import threading
from dataclasses import replace
from unittest.mock import patch

from loguru import logger

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
        """Probe should produce no warning when nobody responds."""
        port = _find_free_port()
        server = NetSyncServer(
            dealer_port=_find_free_port(),
            pub_port=_find_free_port(),
            server_discovery_port=port,
            enable_server_discovery=False,
        )
        sink = io.StringIO()
        handler_id = logger.add(sink, level="WARNING", format="{message}")
        try:
            server._probe_existing_discovery_server()
        finally:
            logger.remove(handler_id)
        assert "Another STYLY-NetSync server" not in sink.getvalue()

    def test_warns_when_another_server_responds(self) -> None:
        """Probe should log a warning when an existing server responds."""
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
            sink = io.StringIO()
            handler_id = logger.add(sink, level="WARNING", format="{message}")
            try:
                server._probe_existing_discovery_server()
            finally:
                logger.remove(handler_id)

            output = sink.getvalue()
            assert "Another STYLY-NetSync server" in output
            assert str(port) in output
        finally:
            stop_event.set()
            t.join(timeout=2)

    def test_probe_does_not_block_on_exception(self) -> None:
        """Probe should not raise even if socket operations fail."""
        server = NetSyncServer(
            dealer_port=_find_free_port(),
            pub_port=_find_free_port(),
            server_discovery_port=_find_free_port(),
            enable_server_discovery=False,
        )
        with patch("socket.socket", side_effect=OSError("mock error")):
            # Should not raise
            server._probe_existing_discovery_server()
