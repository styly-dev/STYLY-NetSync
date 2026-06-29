"""Tests for discovery startup after the required REST bridge."""

from __future__ import annotations

import socket
from dataclasses import replace
from unittest.mock import MagicMock, patch

import pytest

from styly_netsync.config import load_default_config
from styly_netsync.server import NetSyncServer


def _find_free_tcp_port() -> int:
    """Return a free TCP port."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


def _make_server() -> NetSyncServer:
    config = replace(load_default_config(), rest_api_port=_find_free_tcp_port())
    return NetSyncServer(
        control_port=_find_free_tcp_port(),
        transform_port=_find_free_tcp_port(),
        pub_port=_find_free_tcp_port(),
        server_discovery_port=_find_free_tcp_port(),
        enable_server_discovery=True,
        config=config,
    )


def _fake_rest_lifecycle() -> tuple[MagicMock, MagicMock]:
    thread = MagicMock()
    thread.is_alive.return_value = False
    server = MagicMock()
    return thread, server


def test_discovery_starts_after_rest_bridge_success() -> None:
    """Server discovery should start only after the REST bridge is available."""
    server = _make_server()
    rest_thread, rest_server = _fake_rest_lifecycle()

    try:
        with (
            patch(
                "styly_netsync.rest_bridge.run_uvicorn_in_thread",
                return_value=(rest_thread, rest_server),
            ) as run_rest,
            patch("styly_netsync.server.display_logo"),
            patch.object(server, "_start_server_discovery") as start_discovery,
        ):
            server.start()

        run_rest.assert_called_once()
        start_discovery.assert_called_once()
    finally:
        server.stop()


def test_server_start_fails_when_rest_bridge_fails() -> None:
    """Server startup must fail before discovery can advertise a dead REST port."""
    server = _make_server()

    with (
        patch(
            "styly_netsync.rest_bridge.run_uvicorn_in_thread",
            side_effect=RuntimeError("rest bind failed"),
        ) as run_rest,
        patch.object(server, "_start_server_discovery") as start_discovery,
        pytest.raises(SystemExit),
    ):
        server.start()

    run_rest.assert_called_once()
    start_discovery.assert_not_called()
    assert server.running is False
    assert server.server_discovery_running is False
    assert server.tcp_server_discovery_running is False


def test_server_start_fails_without_discovery_when_rest_bridge_fails() -> None:
    """REST bridge startup is required even when server discovery is disabled."""
    server = _make_server()
    server.enable_server_discovery = False

    with (
        patch(
            "styly_netsync.rest_bridge.run_uvicorn_in_thread",
            side_effect=RuntimeError("rest bind failed"),
        ) as run_rest,
        patch.object(server, "_start_server_discovery") as start_discovery,
        pytest.raises(SystemExit),
    ):
        server.start()

    run_rest.assert_called_once()
    start_discovery.assert_not_called()
    assert server.running is False
