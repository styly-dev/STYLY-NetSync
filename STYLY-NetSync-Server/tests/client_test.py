import logging
import threading
import time
import unittest

from styly_netsync import (
    NetSyncServer,
    client_transform,
    create_manager,
    net_sync_manager,
    transform,
)

# Configure logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

class TestNetSyncClient(unittest.TestCase):
    SERVER_DEALER_PORT = 5557
    SERVER_PUB_PORT = 5558
    TEST_ROOM = "test_room"

    server_thread = None
    server: NetSyncServer = None

    @classmethod
    def setUpClass(cls):
        """Set up a server instance to run in the background."""
        cls.server = NetSyncServer(
            dealer_port=cls.SERVER_DEALER_PORT,
            pub_port=cls.SERVER_PUB_PORT,
            enable_beacon=False,
        )
        cls.server_thread = threading.Thread(target=cls.server.start, daemon=True)
        cls.server_thread.start()
        # Give the server a moment to start up
        time.sleep(1)
        logger.info("Test server started.")

    @classmethod
    def tearDownClass(cls):
        """Stop the server."""
        if cls.server:
            cls.server.stop()
        logger.info("Test server stopped.")

    def setUp(self):
        """Create a new client for each test."""
        self.client: net_sync_manager = create_manager(
            server="tcp://localhost",
            dealer_port=self.SERVER_DEALER_PORT,
            sub_port=self.SERVER_PUB_PORT,
            room=self.TEST_ROOM,
            auto_dispatch=False
        )
        self.received_events = {}

    def tearDown(self):
        """Stop the client."""
        if self.client and self.client.is_running:
            self.client.stop()

    def _dispatch_and_wait(self, expected_event_key: str, timeout: float = 2.0):
        """Helper to dispatch events and wait for a specific one."""
        start_time = time.monotonic()
        while time.monotonic() - start_time < timeout:
            self.client.dispatch_pending()
            if expected_event_key in self.received_events:
                return
            time.sleep(0.05)
        self.fail(f"Event '{expected_event_key}' not received within {timeout}s")

    def test_01_connectivity_and_mapping(self):
        """Test if the client connects and receives device ID mapping."""
        self.client.start()
        self.assertTrue(self.client.is_running)

        # Send a transform to announce presence
        self.client.send_transform(client_transform(head=transform()))

        # Wait a moment for the mapping to be broadcast
        time.sleep(1)

        client_no = self.client.get_client_no(self.client._device_id)
        self.assertIsNotNone(client_no, "Client should get a client number.")

        device_id = self.client.get_device_id(client_no)
        self.assertEqual(device_id, self.client._device_id, "Device ID should match.")

    def test_02_transform_pull(self):
        """Test sending and receiving transforms via the pull model."""
        self.client.start()

        # Send a specific transform
        test_transform = client_transform(head=transform(pos_y=1.23))
        self.client.send_transform(test_transform)

        # Wait for the room snapshot to update
        time.sleep(1)

        snapshot = self.client.latest_room()
        self.assertIsNotNone(snapshot, "Should receive a room snapshot.")
        self.assertEqual(snapshot.room_id, self.TEST_ROOM)
        self.assertGreater(len(snapshot.clients), 0, "Snapshot should contain clients.")

        client_no = self.client.get_client_no(self.client._device_id)
        self.assertIn(client_no, snapshot.clients, "Our client should be in the snapshot.")

        received_transform = snapshot.clients[client_no]
        self.assertAlmostEqual(received_transform.head.pos_y, 1.23, places=5)

    def test_03_stealth_mode(self):
        """Test that stealth clients are not included in room snapshots."""
        stealth_client = create_manager(
            server="tcp://localhost",
            dealer_port=self.SERVER_DEALER_PORT,
            sub_port=self.SERVER_PUB_PORT,
            room=self.TEST_ROOM,
        )
        stealth_client.start()
        stealth_client.send_stealth_handshake()
        time.sleep(1) # Wait for handshake to be processed

        # A normal client to observe the room
        self.client.start()
        self.client.send_transform(client_transform(head=transform()))
        time.sleep(1)

        snapshot = self.client.latest_room()
        self.assertIsNotNone(snapshot)

        stealth_client_no = stealth_client.get_client_no(stealth_client._device_id)
        self.assertIsNotNone(stealth_client_no)
        self.assertNotIn(stealth_client_no, snapshot.clients, "Stealth client should not be in snapshot.")

        # Check that the stealth client is in the mapping
        observer_device_id = self.client.get_device_id(stealth_client_no)
        self.assertEqual(observer_device_id, stealth_client._device_id)

        stealth_client.stop()

    def test_04_rpc(self):
        """Test sending and receiving RPCs."""
        def on_rpc(sender_client_no, function_name, args):
            logger.info(f"RPC received: {function_name}({args}) from {sender_client_no}")
            self.received_events['rpc'] = (sender_client_no, function_name, args)

        self.client.on_rpc_received.add(on_rpc)
        self.client.start()

        # Send a transform to get a client_no
        self.client.send_transform(client_transform())
        time.sleep(0.5)

        # Send RPC
        self.client.rpc("TestRPC", ["hello", "world"])

        self._dispatch_and_wait('rpc')

        sender, name, rpc_args = self.received_events['rpc']
        self.assertEqual(name, "TestRPC")
        self.assertEqual(rpc_args, ["hello", "world"])
        # The server assigns the client_no, so we can't easily predict it,
        # but we can check it's a valid number.
        self.assertGreater(sender, 0)

    def test_05_network_variables(self):
        """Test global and client network variables."""
        def on_global_var(name, old, new, meta):
            self.received_events['global_var'] = (name, new)

        def on_client_var(client_no, name, old, new, meta):
            self.received_events['client_var'] = (client_no, name, new)

        self.client.on_global_variable_changed.add(on_global_var)
        self.client.on_client_variable_changed.add(on_client_var)
        self.client.start()

        # Send a transform to get a client_no
        self.client.send_transform(client_transform())
        time.sleep(0.5)

        client_no = self.client.get_client_no(self.client._device_id)
        self.assertIsNotNone(client_no)

        # Test Global NV
        self.client.set_global_variable("game_state", "playing")
        self._dispatch_and_wait('global_var')

        name, value = self.received_events['global_var']
        self.assertEqual(name, "game_state")
        self.assertEqual(value, "playing")
        self.assertEqual(self.client.get_global_variable("game_state"), "playing")

        # Test Client NV
        self.client.set_client_variable(client_no, "player_score", "100")
        self._dispatch_and_wait('client_var')

        c_no, name, value = self.received_events['client_var']
        self.assertEqual(c_no, client_no)
        self.assertEqual(name, "player_score")
        self.assertEqual(value, "100")
        self.assertEqual(self.client.get_client_variable(client_no, "player_score"), "100")

if __name__ == '__main__':
    unittest.main()
