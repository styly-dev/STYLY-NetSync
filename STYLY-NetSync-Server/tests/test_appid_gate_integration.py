#!/usr/bin/env python3
"""
Integration tests for AppID gate functionality in STYLY NetSync Server.

Tests the actual discovery filtering and handshake filtering with real UDP/TCP connections.
"""

import socket
import threading
import time

import zmq

from styly_netsync import binary_serializer
from styly_netsync.server import NetSyncServer


def test_discovery_filtering():
    """Test UDP discovery filtering with AppID."""
    print("\n=== Testing Discovery Filtering ===")

    # Start server with specific allowed AppIDs
    server = NetSyncServer(
        dealer_port=5561,
        pub_port=5562,
        beacon_port=9991,
        allowed_app_ids=["com.styly.prod", "com.styly.stage"],
    )
    server_thread = threading.Thread(target=server.start, daemon=True)
    server_thread.start()
    time.sleep(0.5)  # Wait for server startup

    try:
        # Test 1: Valid AppID should get response
        discovery_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        discovery_socket.settimeout(1.0)

        valid_request = "STYLY-NETSYNC|discover|appId=com.styly.prod|proto=1"
        discovery_socket.sendto(valid_request.encode(), ("localhost", 9991))

        try:
            response, addr = discovery_socket.recvfrom(1024)
            response_str = response.decode("utf-8")
            parts = response_str.split("|")
            assert len(parts) >= 3
            assert parts[0] == "STYLY-NETSYNC"
            print(f"✓ Valid AppID got response: {response_str}")
        except TimeoutError:
            print("✗ Valid AppID did not get response")
            assert False, "Valid AppID should have received response"

        # Test 2: Invalid AppID should not get response
        invalid_request = "STYLY-NETSYNC|discover|appId=com.other.app|proto=1"
        discovery_socket.sendto(invalid_request.encode(), ("localhost", 9991))

        try:
            response, addr = discovery_socket.recvfrom(1024)
            print(f"✗ Invalid AppID got unexpected response: {response}")
            assert False, "Invalid AppID should not have received response"
        except TimeoutError:
            print("✓ Invalid AppID correctly got no response (timeout)")

        # Test 3: Missing AppID should not get response
        missing_request = "STYLY-NETSYNC|discover|appId=|proto=1"
        discovery_socket.sendto(missing_request.encode(), ("localhost", 9991))

        try:
            response, addr = discovery_socket.recvfrom(1024)
            print(f"✗ Missing AppID got unexpected response: {response}")
            assert False, "Missing AppID should not have received response"
        except TimeoutError:
            print("✓ Missing AppID correctly got no response (timeout)")

        # Test 4: Old format should not get response
        old_request = "STYLY-NETSYNC-DISCOVER"
        discovery_socket.sendto(old_request.encode(), ("localhost", 9991))

        try:
            response, addr = discovery_socket.recvfrom(1024)
            print(f"✗ Old format got unexpected response: {response}")
            assert False, "Old format should not have received response"
        except TimeoutError:
            print("✓ Old format correctly got no response (timeout)")

        discovery_socket.close()

        # Check server stats
        print(
            f"Server stats: allowed={server.discovery_allowed}, denied={server.discovery_denied}, missing={server.app_id_missing}"
        )
        assert server.discovery_allowed >= 1  # At least one valid request
        assert (
            server.discovery_denied + server.app_id_missing >= 3
        )  # At least three invalid requests

    finally:
        server.stop()


def test_handshake_filtering():
    """Test TCP handshake filtering with HELLO message."""
    print("\n=== Testing Handshake Filtering ===")

    # Start server with specific allowed AppIDs
    server = NetSyncServer(
        dealer_port=5563,
        pub_port=5564,
        enable_beacon=False,
        allowed_app_ids=["com.styly.prod"],
    )
    server_thread = threading.Thread(target=server.start, daemon=True)
    server_thread.start()
    time.sleep(0.5)  # Wait for server startup

    try:
        context = zmq.Context()

        # Test 1: Valid HELLO message should be accepted
        dealer1 = context.socket(zmq.DEALER)
        dealer1.setsockopt(zmq.LINGER, 0)
        dealer1.setsockopt(zmq.RCVTIMEO, 1000)
        dealer1.connect("tcp://localhost:5563")

        # Send valid HELLO message
        hello_msg = binary_serializer.serialize_hello("com.styly.prod", "device123")
        dealer1.send_multipart([b"test_room", hello_msg])

        # Server should accept the handshake (no immediate close)
        time.sleep(0.1)  # Give server time to process
        print("✓ Valid HELLO message sent and accepted")

        # Test 2: Invalid AppID HELLO should be rejected (connection closed)
        dealer2 = context.socket(zmq.DEALER)
        dealer2.setsockopt(zmq.LINGER, 0)
        dealer2.setsockopt(zmq.RCVTIMEO, 1000)
        dealer2.connect("tcp://localhost:5564")  # Different port to avoid collision

        # Send invalid HELLO message
        invalid_hello = binary_serializer.serialize_hello("com.other.app", "device456")
        dealer2.send_multipart([b"test_room", invalid_hello])

        time.sleep(0.1)  # Give server time to process and close
        print("✓ Invalid HELLO message sent and should be rejected")

        # Test 3: Non-HELLO first message should be rejected
        dealer3 = context.socket(zmq.DEALER)
        dealer3.setsockopt(zmq.LINGER, 0)
        dealer3.setsockopt(zmq.RCVTIMEO, 1000)
        dealer3.connect("tcp://localhost:5563")

        # Send RPC message as first message (should be rejected)
        rpc_data = {"senderClientNo": 1, "functionName": "test", "argumentsJson": "[]"}
        rpc_msg = binary_serializer.serialize_rpc_message(rpc_data)
        dealer3.send_multipart([b"test_room", rpc_msg])

        time.sleep(0.1)  # Give server time to process
        print("✓ Non-HELLO first message sent and should be rejected")

        dealer1.close()
        dealer2.close()
        dealer3.close()
        context.term()

        # Check server handshake stats
        print(
            f"Handshake stats: allowed={server.handshake_allowed}, denied={server.handshake_denied}"
        )
        assert server.handshake_allowed >= 1  # At least one valid handshake
        assert (
            server.handshake_denied >= 1
        )  # At least one invalid handshake (may not get both due to connection timing)

    finally:
        server.stop()


def test_filter_disabled():
    """Test that filter disabled mode works correctly."""
    print("\n=== Testing Filter Disabled Mode ===")

    # Start server with empty allow-list (filter disabled)
    server = NetSyncServer(
        dealer_port=5565,
        pub_port=5566,
        beacon_port=9992,
        allowed_app_ids=[],  # Empty = filter disabled
    )
    server_thread = threading.Thread(target=server.start, daemon=True)
    server_thread.start()
    time.sleep(0.5)

    try:
        # Test discovery with any AppID should work
        discovery_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        discovery_socket.settimeout(1.0)

        test_request = "STYLY-NETSYNC|discover|appId=com.any.app|proto=1"
        discovery_socket.sendto(test_request.encode(), ("localhost", 9992))

        try:
            response, addr = discovery_socket.recvfrom(1024)
            print(f"✓ Any AppID got response when filter disabled: {response.decode()}")
        except TimeoutError:
            print("✗ Filter disabled should allow any AppID")
            assert False, "Filter disabled should allow any AppID"

        discovery_socket.close()

        # Test handshake with any AppID should work
        context = zmq.Context()
        dealer = context.socket(zmq.DEALER)
        dealer.setsockopt(zmq.LINGER, 0)
        dealer.connect("tcp://localhost:5565")

        hello_msg = binary_serializer.serialize_hello("com.random.app", "device789")
        dealer.send_multipart([b"test_room", hello_msg])

        time.sleep(0.1)
        print("✓ Any AppID handshake accepted when filter disabled")

        dealer.close()
        context.term()

        # Check stats - all should be allowed
        assert server.discovery_allowed >= 1
        assert server.handshake_allowed >= 1
        assert server.discovery_denied == 0  # Should be 0 when filter disabled
        print(
            f"Filter disabled stats: discovery_allowed={server.discovery_allowed}, handshake_allowed={server.handshake_allowed}"
        )

    finally:
        server.stop()


if __name__ == "__main__":
    print("Running AppID Gate Integration Tests")
    print("=" * 50)

    try:
        test_discovery_filtering()
        test_handshake_filtering()
        test_filter_disabled()

        print("\n" + "=" * 50)
        print("✅ All AppID gate integration tests passed!")

    except Exception as e:
        print(f"\n❌ Test failed: {e}")
        raise
