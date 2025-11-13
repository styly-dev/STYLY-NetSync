#!/usr/bin/env python3
"""
Test platform-specific error messages when port is already in use.
"""

import unittest.mock as mock

import pytest

from styly_netsync.server import NetSyncServer


class TestPortErrorMessage:
    """Test platform-specific error messages for port conflicts."""

    def test_port_error_message_linux(self):
        """Test that Linux shows lsof and kill commands when port is in use."""
        with mock.patch("platform.system", return_value="Linux"):
            with mock.patch("styly_netsync.server.logger") as mock_logger:
                # Create a server to bind to a port
                server1 = NetSyncServer(dealer_port=15555, pub_port=15556)
                try:
                    server1.start()

                    # Try to create a second server on the same port
                    server2 = NetSyncServer(dealer_port=15555, pub_port=15557)
                    with pytest.raises(SystemExit):
                        server2.start()

                    # Verify Linux-specific error messages were logged
                    error_calls = [
                        str(call) for call in mock_logger.error.call_args_list
                    ]
                    assert any(
                        "lsof -i :15555" in str(call) for call in error_calls
                    ), f"Expected 'lsof -i :15555' in error messages. Got: {error_calls}"
                    assert any(
                        "kill <PID>" in str(call) for call in error_calls
                    ), f"Expected 'kill <PID>' in error messages. Got: {error_calls}"
                    assert not any(
                        "netstat" in str(call) for call in error_calls
                    ), f"Did not expect Windows 'netstat' command. Got: {error_calls}"
                    assert not any(
                        "taskkill" in str(call) for call in error_calls
                    ), f"Did not expect Windows 'taskkill' command. Got: {error_calls}"
                finally:
                    server1.stop()

    def test_port_error_message_windows(self):
        """Test that Windows shows netstat and taskkill commands when port is in use."""
        with mock.patch("platform.system", return_value="Windows"):
            with mock.patch("styly_netsync.server.logger") as mock_logger:
                # Create a server to bind to a port
                server1 = NetSyncServer(dealer_port=16555, pub_port=16556)
                try:
                    server1.start()

                    # Try to create a second server on the same port
                    server2 = NetSyncServer(dealer_port=16555, pub_port=16557)
                    with pytest.raises(SystemExit):
                        server2.start()

                    # Verify Windows-specific error messages were logged
                    error_calls = [
                        str(call) for call in mock_logger.error.call_args_list
                    ]
                    assert any(
                        "netstat -ano | findstr :16555" in str(call)
                        for call in error_calls
                    ), f"Expected 'netstat -ano | findstr :16555' in error messages. Got: {error_calls}"
                    assert any(
                        "taskkill /PID <PID> /F" in str(call) for call in error_calls
                    ), f"Expected 'taskkill /PID <PID> /F' in error messages. Got: {error_calls}"
                    assert not any(
                        "lsof" in str(call) for call in error_calls
                    ), f"Did not expect Unix 'lsof' command. Got: {error_calls}"
                    assert not any(
                        "kill <PID>" in str(call) for call in error_calls
                    ), f"Did not expect Unix 'kill <PID>' command. Got: {error_calls}"
                finally:
                    server1.stop()

    def test_port_error_message_darwin(self):
        """Test that macOS (Darwin) shows lsof and kill commands when port is in use."""
        with mock.patch("platform.system", return_value="Darwin"):
            with mock.patch("styly_netsync.server.logger") as mock_logger:
                # Create a server to bind to a port
                server1 = NetSyncServer(dealer_port=17555, pub_port=17556)
                try:
                    server1.start()

                    # Try to create a second server on the same port
                    server2 = NetSyncServer(dealer_port=17555, pub_port=17557)
                    with pytest.raises(SystemExit):
                        server2.start()

                    # Verify macOS-specific error messages were logged (same as Linux)
                    error_calls = [
                        str(call) for call in mock_logger.error.call_args_list
                    ]
                    assert any(
                        "lsof -i :17555" in str(call) for call in error_calls
                    ), f"Expected 'lsof -i :17555' in error messages. Got: {error_calls}"
                    assert any(
                        "kill <PID>" in str(call) for call in error_calls
                    ), f"Expected 'kill <PID>' in error messages. Got: {error_calls}"
                finally:
                    server1.stop()
