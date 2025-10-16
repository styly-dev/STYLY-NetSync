"""
NetSync client implementation with pull-based transform consumption.

Provides a Python client API that mirrors Unity NetSync capabilities while using
snake_case naming and pull-based transform consumption by default.
"""

import json
import logging
import os
import socket
import threading
import time
import uuid
from queue import Empty, Full, Queue
from typing import Any

# Dynamic ZMQ import based on environment variable
env_is_green = os.environ.get("STYLY_USE_ZMQ_GREEN", "")
if env_is_green.lower() == "true":
    print("## net_sync_manager client imports green ZeroMQ.")
    import zmq.green as zmq
else:
    import zmq

from . import binary_serializer
from .adapters import (
    client_transform_from_wire,
    client_transform_to_wire,
    create_stealth_transform,
    is_stealth_transform,
)
from .events import EventHandler
from .types import client_transform_data, room_transform_data

logger = logging.getLogger(__name__)


class net_sync_manager:
    """
    NetSync client manager providing pull-based transform consumption and event-based RPC/NV.

    Design: Pull-based transforms (get_room_transform_data()) with optional event callbacks for RPC and Network Variables.
    """

    def __init__(
        self,
        server: str = "tcp://localhost",
        dealer_port: int = 5555,
        sub_port: int = 5556,
        room: str = "default_room",
        auto_dispatch: bool = True,
        queue_max: int = 10000,
    ):
        """
        Initialize NetSync client manager.

        Args:
            server: ZeroMQ base address (e.g., "tcp://localhost")
            dealer_port: Server ROUTER port for uplink
            sub_port: Server PUB port for downlink
            room: Room topic to subscribe to
            auto_dispatch: If True, callbacks fire on receive thread
            queue_max: Max queued events for RPC/NV
        """
        self._server = server
        self._dealer_port = dealer_port
        self._sub_port = sub_port
        self._room = room
        self._auto_dispatch = auto_dispatch
        self._queue_max = queue_max

        # ZeroMQ context and sockets
        self._context: zmq.Context | None = None
        self._dealer_socket: zmq.Socket | None = None
        self._sub_socket: zmq.Socket | None = None

        # Threading
        self._running = False
        self._receive_thread: threading.Thread | None = None
        self._lock = threading.RLock()

        # Device/client identification
        self._device_id = str(uuid.uuid4())
        self._client_no: int | None = None
        self._is_ready = False

        # Pull-based transform state (single latest snapshot)
        self._latest_room_snapshot: room_transform_data | None = None
        self._snapshot_lock = threading.Lock()

        # Device ID mapping
        self._device_to_client: dict[str, int] = {}
        self._client_to_device: dict[int, str] = {}
        self._client_stealth_flags: dict[int, bool] = {}

        # Network Variables (local cache)
        self._global_variables: dict[str, str] = {}
        self._client_variables: dict[int, dict[str, str]] = {}

        # Event queues and handlers
        self._rpc_queue: Queue = Queue(maxsize=queue_max)
        self._nv_queue: Queue = Queue(maxsize=queue_max)

        # Event handlers
        self.on_rpc_received = EventHandler()
        self.on_global_variable_changed = EventHandler()
        self.on_client_variable_changed = EventHandler()
        self.on_avatar_connected = EventHandler()
        self.on_client_disconnected = EventHandler()
        self.on_server_discovered = EventHandler()

        # Discovery
        self._discovery_socket: socket.socket | None = None
        self._discovery_thread: threading.Thread | None = None
        self._discovery_running = False

        # Statistics
        self._stats = {
            "messages_received": 0,
            "transforms_received": 0,
            "rpc_received": 0,
            "nv_updates": 0,
            "last_snapshot_time": 0.0,
        }

    # Properties
    @property
    def is_running(self) -> bool:
        """Whether the client is currently running."""
        return self._running

    @property
    def room_id(self) -> str:
        """Current room ID."""
        return self._room

    @property
    def server_address(self) -> str:
        """Server address."""
        return self._server

    @property
    def client_no(self) -> int | None:
        """Assigned client number (None until mapped)."""
        return self._client_no

    @property
    def device_id(self) -> str:
        """This client's device UUID."""
        return self._device_id

    @property
    def is_ready(self) -> bool:
        """True once client number has been assigned."""
        return self._is_ready

    @property
    def dealer_port(self) -> int:
        """Dealer port."""
        return self._dealer_port

    @property
    def sub_port(self) -> int:
        """Subscriber port."""
        return self._sub_port

    def start(self) -> "net_sync_manager":
        """Start the client and connect to server."""
        with self._lock:
            if self._running:
                return self

            try:
                # Initialize ZeroMQ
                self._context = zmq.Context()

                # DEALER socket for uplink
                self._dealer_socket = self._context.socket(zmq.DEALER)
                dealer_addr = f"{self._server}:{self._dealer_port}"
                self._dealer_socket.connect(dealer_addr)

                # SUB socket for downlink
                self._sub_socket = self._context.socket(zmq.SUB)
                sub_addr = f"{self._server}:{self._sub_port}"
                self._sub_socket.connect(sub_addr)
                self._sub_socket.setsockopt(zmq.SUBSCRIBE, self._room.encode("utf-8"))

                # Start receive thread
                self._running = True
                self._receive_thread = threading.Thread(
                    target=self._receive_loop, daemon=True
                )
                self._receive_thread.start()

                logger.info(
                    f"NetSync client started: {dealer_addr}, {sub_addr}, room={self._room}"
                )

            except Exception as e:
                self._cleanup()
                raise Exception(f"Failed to start NetSync client: {e}") from e

            return self

    def stop(self) -> None:
        """Stop the client and cleanup resources."""
        with self._lock:
            if not self._running:
                return

            self._running = False

            # Stop discovery if running
            self.stop_discovery()

            # Wait for receive thread
            if self._receive_thread and self._receive_thread.is_alive():
                self._receive_thread.join(timeout=1.0)

            self._cleanup()
            logger.info("NetSync client stopped")

    def close(self) -> None:
        """Alias for stop()."""
        self.stop()

    def _cleanup(self) -> None:
        """Clean up ZeroMQ resources."""
        if self._dealer_socket:
            self._dealer_socket.close()
            self._dealer_socket = None
        if self._sub_socket:
            self._sub_socket.close()
            self._sub_socket = None
        if self._context:
            self._context.term()
            self._context = None

    def _receive_loop(self) -> None:
        """Main receive loop for processing server messages."""
        poller = zmq.Poller()
        if self._sub_socket:
            poller.register(self._sub_socket, zmq.POLLIN)

        while self._running:
            try:
                socks = dict(poller.poll(100))  # 100ms timeout

                if self._sub_socket in socks:
                    message_parts = self._sub_socket.recv_multipart()
                    if len(message_parts) >= 2:
                        # room_id = message_parts[0].decode("utf-8")  # Not used currently
                        data = message_parts[1]

                        self._process_message(data)

            except zmq.Again:
                continue
            except Exception as e:
                if self._running:
                    logger.error(f"Error in receive loop: {e}")

    def _process_message(self, data: bytes) -> None:
        """Process a received message."""
        try:
            msg_type, msg_data, _ = binary_serializer.deserialize(data)
            if msg_data is None:
                return

            self._stats["messages_received"] += 1

            if msg_type == binary_serializer.MSG_ROOM_TRANSFORM:
                self._process_room_transform(msg_data)
            elif msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING:
                self._process_device_mapping(msg_data)
            elif msg_type == binary_serializer.MSG_RPC:
                self._process_rpc(msg_data)
            elif msg_type == binary_serializer.MSG_GLOBAL_VAR_SYNC:
                self._process_global_var_sync(msg_data)
            elif msg_type == binary_serializer.MSG_CLIENT_VAR_SYNC:
                self._process_client_var_sync(msg_data)

        except Exception as e:
            logger.error(f"Error processing message: {e}")

    def _process_room_transform(self, msg_data: dict[str, Any]) -> None:
        """Process room transform update (pull-based - single latest snapshot)."""
        try:
            room_id = msg_data.get("roomId", "")
            clients_data = msg_data.get("clients", [])

            # Convert wire format to snake_case and filter out stealth clients
            clients = {}
            for client_data in clients_data:
                ct = client_transform_from_wire(client_data)
                if ct.client_no is not None and not is_stealth_transform(ct):
                    clients[ct.client_no] = ct

            # Update single latest snapshot (O(1) access)
            with self._snapshot_lock:
                prev_snapshot = self._latest_room_snapshot
                self._latest_room_snapshot = room_transform_data(
                    room_id=room_id, clients=clients, timestamp=time.monotonic()
                )

            prev_clients = set(prev_snapshot.clients.keys()) if prev_snapshot else set()
            new_clients = set(clients.keys())
            added = new_clients - prev_clients
            removed = prev_clients - new_clients

            for client_no in added:
                self.on_avatar_connected.invoke(client_no)
            for client_no in removed:
                self.on_client_disconnected.invoke(client_no)

            self._stats["transforms_received"] += 1
            self._stats["last_snapshot_time"] = time.monotonic()

        except Exception as e:
            logger.error(f"Error processing room transform: {e}")

    def _process_device_mapping(self, msg_data: dict[str, Any]) -> None:
        """Process device ID mapping update."""
        try:
            mappings = msg_data.get("mappings", [])

            # Clear and rebuild mapping tables
            self._device_to_client.clear()
            self._client_to_device.clear()
            self._client_stealth_flags.clear()

            for mapping in mappings:
                device_id = mapping.get("deviceId")
                client_no = mapping.get("clientNo")
                is_stealth = mapping.get("isStealthMode", False)

                if device_id and client_no is not None:
                    self._device_to_client[device_id] = client_no
                    self._client_to_device[client_no] = device_id
                    self._client_stealth_flags[client_no] = is_stealth

                    # Check if this is our mapping
                    if device_id == self._device_id:
                        self._client_no = client_no
                        self._is_ready = True
                        logger.info(f"Assigned client number: {client_no}")

        except Exception as e:
            logger.error(f"Error processing device mapping: {e}")

    def _process_rpc(self, msg_data: dict[str, Any]) -> None:
        """Process RPC message."""
        try:
            sender_client_no = msg_data.get("senderClientNo")
            function_name = msg_data.get("functionName", "")
            args_json = msg_data.get("argumentsJson", "[]")

            try:
                args = json.loads(args_json)
            except json.JSONDecodeError:
                args = []

            self._stats["rpc_received"] += 1

            # Queue for pull or auto-dispatch
            rpc_event = (sender_client_no, function_name, args)

            if self._auto_dispatch:
                self.on_rpc_received.invoke(sender_client_no, function_name, args)
            else:
                try:
                    self._rpc_queue.put_nowait(rpc_event)
                except Full:
                    # Drop oldest and add new
                    try:
                        self._rpc_queue.get_nowait()
                        self._rpc_queue.put_nowait(rpc_event)
                    except Empty:
                        pass

        except Exception as e:
            logger.error(f"Error processing RPC: {e}")

    def _process_global_var_sync(self, msg_data: dict[str, Any]) -> None:
        """Process global variable sync."""
        try:
            variables = msg_data.get("variables", [])

            for var in variables:
                name = var.get("name", "")
                value = var.get("value", "")
                old_value = self._global_variables.get(name)

                self._global_variables[name] = value

                if old_value != value:
                    self._stats["nv_updates"] += 1

                    event = ("global", name, old_value, value)
                    if self._auto_dispatch:
                        self.on_global_variable_changed.invoke(name, old_value, value)
                    else:
                        try:
                            self._nv_queue.put_nowait(event)
                        except Full:
                            try:
                                self._nv_queue.get_nowait()
                                self._nv_queue.put_nowait(event)
                            except Empty:
                                pass

        except Exception as e:
            logger.error(f"Error processing global var sync: {e}")

    def _process_client_var_sync(self, msg_data: dict[str, Any]) -> None:
        """Process client variable sync."""
        try:
            client_variables = msg_data.get("clientVariables", {})

            for client_no_str, variables in client_variables.items():
                try:
                    client_no = int(client_no_str)
                except ValueError:
                    continue

                if client_no not in self._client_variables:
                    self._client_variables[client_no] = {}

                for var in variables:
                    name = var.get("name", "")
                    value = var.get("value", "")
                    old_value = self._client_variables[client_no].get(name)

                    self._client_variables[client_no][name] = value

                    if old_value != value:
                        self._stats["nv_updates"] += 1

                        event = ("client", client_no, name, old_value, value)
                        if self._auto_dispatch:
                            self.on_client_variable_changed.invoke(
                                client_no, name, old_value, value
                            )
                        else:
                            try:
                                self._nv_queue.put_nowait(event)
                            except Full:
                                try:
                                    self._nv_queue.get_nowait()
                                    self._nv_queue.put_nowait(event)
                                except Empty:
                                    pass

        except Exception as e:
            logger.error(f"Error processing client var sync: {e}")

    # Transform API (pull-based)
    def get_room_transform_data(self) -> room_transform_data | None:
        """Get the latest room snapshot (pull-based consumption)."""
        with self._snapshot_lock:
            return self._latest_room_snapshot

    def get_client_transform_data(self, client_no: int) -> client_transform_data | None:
        """Get latest transform for a specific client."""
        snapshot = self.get_room_transform_data()
        if snapshot and client_no in snapshot.clients:
            return snapshot.clients[client_no]
        return None

    # Sending API
    def send_transform(self, tx: client_transform_data) -> bool:
        """Send client transform to server."""
        if not self._running or not self._dealer_socket:
            return False

        try:
            # Convert to wire format and serialize
            wire_data = client_transform_to_wire(tx)
            message = binary_serializer.serialize_client_transform(wire_data)

            # Send with room topic
            self._dealer_socket.send_multipart([self._room.encode("utf-8"), message])
            return True

        except Exception as e:
            logger.error(f"Error sending transform: {e}")
            return False

    def send_stealth_handshake(self) -> bool:
        """Send stealth handshake to become invisible client."""
        stealth_tx = create_stealth_transform()
        stealth_tx.device_id = self._device_id
        return self.send_transform(stealth_tx)

    def rpc(self, function_name: str, args: list[str]) -> bool:
        """Send RPC to all clients in room."""
        if not self._running or not self._dealer_socket or self._client_no is None:
            return False

        try:
            rpc_data = {
                "senderClientNo": self._client_no,
                "functionName": function_name,
                "argumentsJson": json.dumps(args),
            }
            message = binary_serializer.serialize_rpc_message(rpc_data)
            self._dealer_socket.send_multipart([self._room.encode("utf-8"), message])
            return True

        except Exception as e:
            logger.error(f"Error sending RPC: {e}")
            return False

    # Network Variables API
    def set_global_variable(self, name: str, value: str) -> bool:
        """Set global variable."""
        if not self._running or not self._dealer_socket or self._client_no is None:
            return False

        try:
            var_data = {
                "senderClientNo": self._client_no,
                "variableName": name,
                "variableValue": value,
                "timestamp": time.time(),
            }
            message = binary_serializer.serialize_global_var_set(var_data)
            self._dealer_socket.send_multipart([self._room.encode("utf-8"), message])
            return True

        except Exception as e:
            logger.error(f"Error setting global variable: {e}")
            return False

    def set_client_variable(self, target_client_no: int, name: str, value: str) -> bool:
        """Set client variable."""
        if not self._running or not self._dealer_socket or self._client_no is None:
            return False

        try:
            var_data = {
                "senderClientNo": self._client_no,
                "targetClientNo": target_client_no,
                "variableName": name,
                "variableValue": value,
                "timestamp": time.time(),
            }
            message = binary_serializer.serialize_client_var_set(var_data)
            self._dealer_socket.send_multipart([self._room.encode("utf-8"), message])
            return True

        except Exception as e:
            logger.error(f"Error setting client variable: {e}")
            return False

    def get_global_variable(self, name: str, default: str | None = None) -> str | None:
        """Get global variable from local cache."""
        return self._global_variables.get(name, default)

    def get_client_variable(
        self, client_no: int, name: str, default: str | None = None
    ) -> str | None:
        """Get client variable from local cache."""
        if client_no in self._client_variables:
            return self._client_variables[client_no].get(name, default)
        return default

    # Device mapping API
    def get_client_no(self, device_id: str) -> int | None:
        """Get client number for device ID."""
        return self._device_to_client.get(device_id)

    def get_device_id_from_client_no(self, client_no: int) -> str | None:
        """Get device ID for client number."""
        return self._client_to_device.get(client_no)

    def get_all_global_variables(self) -> dict[str, str]:
        """Return a copy of all global variables."""
        return self._global_variables.copy()

    def get_all_client_variables(self, client_no: int) -> dict[str, str]:
        """Return a copy of all variables for the given client."""
        return self._client_variables.get(client_no, {}).copy()

    def is_client_stealth_mode(self, client_no: int) -> bool:
        """Check if the client is in stealth mode."""
        return self._client_stealth_flags.get(client_no, False)

    # Event dispatch control
    def dispatch_pending_events(self, max_items: int = 100) -> int:
        """Process queued callbacks on caller's thread."""
        if self._auto_dispatch:
            return 0

        dispatched = 0

        # Process RPC events
        while dispatched < max_items:
            try:
                event = self._rpc_queue.get_nowait()
                sender_client_no, function_name, args = event
                self.on_rpc_received.invoke(sender_client_no, function_name, args)
                dispatched += 1
            except Empty:
                break

        # Process Network Variable events
        while dispatched < max_items:
            try:
                event = self._nv_queue.get_nowait()
                if event[0] == "global":
                    _, name, old_value, new_value = event
                    self.on_global_variable_changed.invoke(name, old_value, new_value)
                elif event[0] == "client":
                    _, client_no, name, old_value, new_value = event
                    self.on_client_variable_changed.invoke(
                        client_no, name, old_value, new_value
                    )
                dispatched += 1
            except Empty:
                break

        return dispatched

    # Discovery API
    def start_discovery(self, server_discovery_port: int = 9999) -> None:
        """Start UDP discovery for servers."""
        if self._discovery_running:
            return

        try:
            self._discovery_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            self._discovery_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            self._discovery_socket.settimeout(1.0)

            self._discovery_running = True
            self._discovery_thread = threading.Thread(
                target=self._discovery_loop, args=(server_discovery_port,), daemon=True
            )
            self._discovery_thread.start()

            logger.info(f"Started UDP discovery on port {server_discovery_port}")

        except Exception as e:
            logger.error(f"Error starting discovery: {e}")

    def stop_discovery(self) -> None:
        """Stop UDP discovery."""
        self._discovery_running = False

        if self._discovery_socket:
            self._discovery_socket.close()
            self._discovery_socket = None

        if self._discovery_thread and self._discovery_thread.is_alive():
            self._discovery_thread.join(timeout=1.0)

        logger.info("Stopped UDP discovery")

    def _discovery_loop(self, server_discovery_port: int) -> None:
        """UDP discovery loop."""
        while self._discovery_running:
            try:
                # Send discovery request
                message = "STYLY-NETSYNC-DISCOVER"
                self._discovery_socket.sendto(
                    message.encode(), ("<broadcast>", server_discovery_port)
                )

                # Listen for response
                try:
                    data, addr = self._discovery_socket.recvfrom(1024)
                    response = data.decode("utf-8")

                    if response.startswith("STYLY-NETSYNC|"):
                        parts = response.split("|")
                        if len(parts) >= 4:
                            dealer_port = int(parts[1])
                            sub_port = int(parts[2])
                            server_name = parts[3]

                            logger.info(
                                f"Discovered server: {server_name} at {addr[0]}:{dealer_port}/{sub_port}"
                            )
                            server_address = f"tcp://{addr[0]}"
                            self.on_server_discovered.invoke(
                                server_address, dealer_port, sub_port
                            )
                            # Could auto-reconnect here if desired

                except TimeoutError:
                    pass

                time.sleep(5.0)  # Discovery interval

            except PermissionError:
                # Broadcast not permitted in sandboxed environment - this is expected
                logger.debug(
                    "Discovery broadcast not permitted (sandboxed environment)"
                )
                time.sleep(1.0)
            except Exception as e:
                if self._discovery_running:
                    logger.error(f"Discovery error: {e}")
                    time.sleep(1.0)

    # Diagnostics
    def get_stats(self) -> dict[str, Any]:
        """Get diagnostic statistics."""
        return self._stats.copy()
