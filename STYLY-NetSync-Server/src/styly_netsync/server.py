# server.py
import sys

# ruff: noqa: E402, I001

# Python version check - must be at the very beginning
MIN_PY = (3, 11)
if sys.version_info < MIN_PY:
    sys.stderr.write(
        f"ERROR: STYLY NetSync Server requires Python {MIN_PY[0]}.{MIN_PY[1]}+ "
        f"(current: {sys.version.split()[0]}).\n"
    )
    sys.exit(1)

import argparse
import base64
import json
import logging
import os
import socket
import threading
import time
import traceback
from functools import lru_cache
from pathlib import Path
from queue import Empty, Full, Queue
from typing import Any
import zmq
from . import binary_serializer

# Log configuration (with optional ANSI colors for console)


class ColorFormatter(logging.Formatter):
    """Simple ANSI colorizing formatter.

    - INFO: default
    - WARNING: yellow
    - ERROR: bright red
    - CRITICAL: white on red background
    """

    RESET = "\x1b[0m"
    COLORS = {
        "WARNING": "\x1b[33m",  # yellow
        "ERROR": "\x1b[91m\x1b[1m",  # bright red + bold
        "CRITICAL": "\x1b[97m\x1b[41m\x1b[1m",  # white on red bg + bold
    }

    def __init__(self, fmt: str, datefmt: str | None = None, enable_color: bool = True):
        super().__init__(fmt=fmt, datefmt=datefmt)
        self.enable_color = enable_color

    def format(self, record: logging.LogRecord) -> str:
        base = super().format(record)
        if not self.enable_color:
            return base
        color = self.COLORS.get(record.levelname)
        if not color:
            return base
        return f"{color}{base}{self.RESET}"


# Use colors only when outputting to a TTY and NO_COLOR is not set
_use_color = sys.stderr.isatty() and os.getenv("NO_COLOR") is None
_handler = logging.StreamHandler()
_handler.setFormatter(
    ColorFormatter(
        "%(asctime)s - %(levelname)s - %(message)s",
        datefmt="%H:%M:%S",
        enable_color=_use_color,
    )
)
logging.basicConfig(level=logging.INFO, handlers=[_handler])
logger = logging.getLogger(__name__)


@lru_cache(maxsize=1)
def get_version() -> str:
    """
    Return the server version.
    Priority:
      1) importlib.metadata for 'styly-netsync-server' (when installed)
      2) parse nearest pyproject.toml (when running from source)
      3) 'unknown'
    """
    # Python 3.11+ guaranteed, so we can use these imports directly
    import importlib.metadata as im
    import tomllib

    # 1) Try installed package metadata first
    try:
        return im.version("styly-netsync-server")
    except im.PackageNotFoundError:
        # Fallback: reverse lookup from package name to distribution
        for dist in im.packages_distributions().get("styly_netsync", []):
            try:
                return im.version(dist)
            except im.PackageNotFoundError:
                pass

    # 2) Source execution fallback - parse pyproject.toml properly
    for parent in Path(__file__).resolve().parents:
        toml_path = parent / "pyproject.toml"
        if toml_path.exists():
            data = tomllib.loads(toml_path.read_text(encoding="utf-8"))
            v = (data.get("project") or {}).get("version")
            if v:
                return v
            break

    return "unknown"


class NetSyncServer:
    # Performance and timing constants
    BASE_BROADCAST_INTERVAL = 0.1  # 10Hz base rate
    IDLE_BROADCAST_INTERVAL = 0.5  # 2Hz when idle
    DIRTY_THRESHOLD = 0.05  # 20Hz max rate when very active
    BROADCAST_CHECK_INTERVAL = 0.05  # Check broadcasts every 50ms
    CLEANUP_INTERVAL = 1.0  # Cleanup every 1 second
    STATUS_LOG_INTERVAL = 10.0  # Log status every 10 seconds
    MAIN_LOOP_SLEEP = 0.02  # 50Hz main loop sleep
    CLIENT_TIMEOUT = 1.0  # 1 second timeout for client disconnect
    DEVICE_ID_EXPIRY_TIME = (
        300.0  # 5 minutes - remove device ID mappings after this time
    )
    POLL_TIMEOUT = 100  # ZMQ poll timeout in ms

    def __init__(
        self,
        dealer_port=5555,
        pub_port=5556,
        enable_beacon=True,
        beacon_port=9999,
        server_name="STYLY-LBE-Server",
        nv_flush_policy: str = "drain",
    ):
        self.dealer_port = dealer_port
        self.pub_port = pub_port
        self.context = zmq.Context()

        # Beacon discovery settings
        self.enable_beacon = enable_beacon
        self.beacon_port = beacon_port
        self.server_name = server_name
        self.beacon_socket = None
        self.beacon_thread = None
        self.beacon_running = False

        # Sockets
        self.router = None
        self.pub = None  # Will be created/owned by Publisher thread only

        # Publisher thread infrastructure
        self._pub_queue = Queue(maxsize=10000)  # tuneable
        self._publisher_thread = None
        self._publisher_running = False
        self._pub_ready = threading.Event()  # signaled after successful bind
        self._publisher_exception = None  # bind/run errors are stored here

        # Thread-safe room management with locks
        self.rooms: dict[str, dict[str, Any]] = (
            {}
        )  # room_id -> {device_id: {client_data}}
        self.room_dirty_flags: dict[str, bool] = (
            {}
        )  # Track which rooms have changed data
        self.room_last_broadcast: dict[str, float] = (
            {}
        )  # Track last broadcast time per room
        self.client_binary_cache: dict[str, bytes] = (
            {}
        )  # Cache client_no -> binary data

        # Client number management per room
        self.room_client_no_counters: dict[str, int] = {}  # room_id -> next_client_no
        self.room_device_id_to_client_no: dict[str, dict[str, int]] = (
            {}
        )  # room_id -> {device_id: client_no}
        self.room_client_no_to_device_id: dict[str, dict[int, str]] = (
            {}
        )  # room_id -> {client_no: device_id}
        self.device_id_last_seen: dict[str, float] = (
            {}
        )  # device_id -> last_seen_timestamp

        # Network Variables storage
        self.global_variables: dict[str, dict[str, Any]] = (
            {}
        )  # room_id -> {var_name: {value, timestamp, lastWriterClientNo}}
        self.client_variables: dict[str, dict[int, dict[str, Any]]] = (
            {}
        )  # room_id -> {client_no -> {var_name: {value, timestamp, lastWriterClientNo}}}

        # NV Pending buffers for coalescing (latest-wins per key)
        self.pending_global_nv: dict[str, dict[str, tuple]] = (
            {}
        )  # room_id -> {var_name: (sender_client_no, value, timestamp)}
        self.pending_client_nv: dict[str, dict[tuple, tuple]] = (
            {}
        )  # room_id -> {(target_client_no, var_name): (sender_client_no, value, timestamp)}

        # NV flush behaviour
        self.nv_flush_policy = nv_flush_policy  # "drain" or "rate_limited"

        # NV fairness and rate control
        self.nv_flush_interval = 0.05  # 50ms flush cadence
        self.nv_per_client_rate = 5.0  # 5 updates/second per client
        self.nv_per_room_cap = 50.0  # 50 updates/second per room
        self.room_last_nv_flush: dict[str, float] = {}  # room_id -> last_flush_time
        self.room_client_nv_allowance: dict[str, dict[int, float]] = (
            {}
        )  # room_id -> {client_no: fractional_allowance}
        self.room_nv_allowance: dict[str, float] = {}  # room_id -> fractional_allowance

        # NV monitoring window (1s sliding window for logging only)
        self.nv_monitor_window: dict[str, list] = {}  # room_id -> [timestamps]
        self.nv_monitor_window_size = 1.0  # 1 second window
        self.nv_monitor_threshold = 200  # Log warning if > 200 NV req/s

        # Network Variables limits
        self.MAX_GLOBAL_VARS = 20
        self.MAX_CLIENT_VARS = 20
        self.MAX_VAR_NAME_LENGTH = 64
        self.MAX_VAR_VALUE_LENGTH = 1024

        # Thread synchronization
        self._rooms_lock = threading.RLock()  # Reentrant lock for nested access
        self._stats_lock = threading.Lock()  # Lock for statistics

        # Threading
        self.running = False
        self.receive_thread = None
        self.periodic_thread = None  # Combined broadcast + cleanup

        # Performance configuration (now using constants)
        self.base_broadcast_interval = self.BASE_BROADCAST_INTERVAL
        self.idle_broadcast_interval = self.IDLE_BROADCAST_INTERVAL
        self.dirty_threshold = self.DIRTY_THRESHOLD

        # Statistics
        self.message_count = 0
        self.broadcast_count = 0
        self.skipped_broadcasts = 0

    def _increment_stat(self, stat_name: str, amount: int = 1):
        """Thread-safe increment of statistics"""
        with self._stats_lock:
            setattr(self, stat_name, getattr(self, stat_name) + amount)

    def _publisher_loop(self):
        """The only place that owns/uses self.pub and performs ZMQ sends."""
        try:
            # Create/bind PUB in this thread to avoid cross-thread use
            self.pub = self.context.socket(zmq.PUB)
            self.pub.bind(f"tcp://*:{self.pub_port}")
            logger.info(f"PUB socket bound to port {self.pub_port} (PublisherThread)")
            self._pub_ready.set()

            while self._publisher_running:
                try:
                    item = self._pub_queue.get(timeout=0.05)
                except Empty:
                    continue

                # Sentinel for shutdown
                if not item or item[0] is None:
                    break

                topic_bytes, message_bytes = item

                try:
                    self.pub.send_multipart([topic_bytes, message_bytes])
                    # Count only *actual* sends
                    self._increment_stat("broadcast_count")
                except Exception as e:
                    logger.error(f"Publisher failed to send: {e}")

        except Exception as e:
            # On bind or loop failure, publish the exception and wake starters
            self._publisher_exception = e
            self._pub_ready.set()
            logger.error(f"Publisher error during startup/run: {e}")
        finally:
            if self.pub is not None:
                try:
                    self.pub.close()
                except Exception:
                    pass
                self.pub = None
            logger.info("Publisher loop ended")

    def _enqueue_pub(self, topic_bytes: bytes, message_bytes: bytes):
        """Thread-safe enqueue of a broadcast. Never touches self.pub directly."""
        try:
            self._pub_queue.put_nowait((topic_bytes, message_bytes))
        except Full:
            # Backpressure policy: drop oldest-style or simply count the drop
            self._increment_stat("skipped_broadcasts")
            logger.debug("PUB queue full: dropping a message")

    def _get_or_assign_client_no(self, room_id: str, device_id: str) -> int:
        """Get existing client number or assign a new one for the given device ID in the room"""
        with self._rooms_lock:
            # Initialize room structures if needed
            self._initialize_room(room_id)

            # Update last seen time
            self.device_id_last_seen[device_id] = time.monotonic()

            # Check if device ID already has a client number in this room
            if device_id in self.room_device_id_to_client_no[room_id]:
                return self.room_device_id_to_client_no[room_id][device_id]

            # Assign new client number
            client_no = self.room_client_no_counters[room_id]
            if client_no > 65535:  # Max value for 2 bytes
                # Find and reuse expired client numbers
                client_no = self._find_reusable_client_no(room_id)
                if client_no == -1:
                    raise ValueError(
                        f"Room {room_id} has exhausted all available client numbers"
                    )
            else:
                self.room_client_no_counters[room_id] += 1

            # Store mappings
            self.room_device_id_to_client_no[room_id][device_id] = client_no
            self.room_client_no_to_device_id[room_id][client_no] = device_id

            logger.info(
                f"Assigned client number {client_no} to device ID {device_id[:8]}... in room {room_id}"
            )
            return client_no

    def _initialize_room(self, room_id: str):
        """Initialize all room-related data structures"""
        if room_id not in self.rooms:
            self.rooms[room_id] = {}
            self.room_dirty_flags[room_id] = True
            self.room_last_broadcast[room_id] = 0
            self.room_client_no_counters[room_id] = 1
            self.room_device_id_to_client_no[room_id] = {}
            self.room_client_no_to_device_id[room_id] = {}

            # Initialize Network Variables for the room
            self.global_variables[room_id] = {}
            self.client_variables[room_id] = {}

            # Initialize NV pending buffers and rate control
            self.pending_global_nv[room_id] = {}
            self.pending_client_nv[room_id] = {}
            self.room_last_nv_flush[room_id] = 0
            self.room_client_nv_allowance[room_id] = {}
            self.room_nv_allowance[room_id] = 0.0

            # Initialize monitoring window
            self.nv_monitor_window[room_id] = []

            logger.info(f"Created new room: {room_id}")

    def _get_device_id_from_identity(self, client_identity: bytes, room_id: str) -> str:
        """Get device ID from client identity"""
        with self._rooms_lock:
            if room_id in self.rooms:
                for device_id, client_data in self.rooms[room_id].items():
                    if client_data.get("identity") == client_identity:
                        return device_id
        return None

    def _get_client_no_for_device_id(self, room_id: str, device_id: str) -> int:
        """Get client number for a given device ID in a room"""
        with self._rooms_lock:
            if room_id in self.room_device_id_to_client_no:
                return self.room_device_id_to_client_no[room_id].get(device_id, 0)
        return 0

    def _find_reusable_client_no(self, room_id: str) -> int:
        """Find a client number that can be reused (from expired device IDs)"""
        current_time = time.monotonic()

        # Check all client numbers in the room
        for client_no, device_id in list(
            self.room_client_no_to_device_id[room_id].items()
        ):
            if device_id not in self.device_id_last_seen:
                # Device ID has no last seen time, can reuse
                del self.room_client_no_to_device_id[room_id][client_no]
                del self.room_device_id_to_client_no[room_id][device_id]
                return client_no

            if (
                current_time - self.device_id_last_seen[device_id]
                > self.DEVICE_ID_EXPIRY_TIME
            ):
                # Device ID has expired, can reuse
                del self.room_client_no_to_device_id[room_id][client_no]
                del self.room_device_id_to_client_no[room_id][device_id]
                del self.device_id_last_seen[device_id]
                return client_no

        return -1  # No reusable client number found

    def _cleanup_expired_device_id_mappings(self, current_time):
        """Clean up expired device ID to client number mappings"""
        with self._rooms_lock:
            expired_device_ids = []

            # Find expired device IDs
            for device_id, last_seen in list(self.device_id_last_seen.items()):
                if current_time - last_seen > self.DEVICE_ID_EXPIRY_TIME:
                    expired_device_ids.append(device_id)

            # Remove expired mappings
            for device_id in expired_device_ids:
                del self.device_id_last_seen[device_id]

                # Remove from all room mappings
                for room_id in list(self.room_device_id_to_client_no.keys()):
                    if device_id in self.room_device_id_to_client_no[room_id]:
                        client_no = self.room_device_id_to_client_no[room_id][device_id]
                        del self.room_device_id_to_client_no[room_id][device_id]
                        if client_no in self.room_client_no_to_device_id[room_id]:
                            del self.room_client_no_to_device_id[room_id][client_no]
                        logger.info(
                            f"Cleaned up expired device ID {device_id[:8]}... (client number: {client_no}) from room {room_id}"
                        )

            if expired_device_ids:
                logger.info(
                    f"Cleaned up {len(expired_device_ids)} expired device ID mappings"
                )

    def start(self):
        """Start the server"""
        logger.info(
            f"Starting server on ports {self.dealer_port} (ROUTER) and {self.pub_port} (PUB)"
        )

        try:
            # Setup ROUTER socket
            self.router = self.context.socket(zmq.ROUTER)
            self.router.bind(f"tcp://*:{self.dealer_port}")
            logger.info(f"ROUTER socket bound to port {self.dealer_port}")

            # Start Publisher thread (it creates/binds PUB)
            self._publisher_running = True
            self._publisher_thread = threading.Thread(
                target=self._publisher_loop, name="PublisherThread", daemon=True
            )
            self._publisher_thread.start()

            # Wait for PUB bind or failure
            if not self._pub_ready.wait(timeout=5.0):
                raise RuntimeError(
                    "Timed out waiting for Publisher thread to bind PUB socket"
                )
            if self._publisher_exception:
                # Clean up and re-raise the failure so callers get a proper error
                if self.router:
                    self.router.close()
                self.context.term()
                raise self._publisher_exception

            self.running = True

            # Start other threads
            self.receive_thread = threading.Thread(
                target=self._receive_loop, name="ReceiveThread"
            )
            self.periodic_thread = threading.Thread(
                target=self._periodic_loop, name="PeriodicThread"
            )

            self.receive_thread.start()
            self.periodic_thread.start()

            # Start beacon if enabled
            if self.enable_beacon:
                self._start_beacon()

            logger.info("All threads started successfully")
            logger.info("Server is ready and waiting for connections...")

        except zmq.error.ZMQError as e:
            if "Address already in use" in str(e):
                logger.error(
                    f"Error: Another server instance is already running on port {self.dealer_port}"
                )
                logger.error(
                    "Please stop the existing server before starting a new one."
                )
                logger.error("You can find the process using: lsof -i :5555")
                logger.error("And stop it using: kill <PID>")
                # Clean up sockets if partially created
                if self.router:
                    self.router.close()
                self.context.term()
                raise SystemExit(1) from e
            else:
                logger.error(f"ZMQ Error: {e}")
                raise
        except Exception as e:
            logger.error(f"Failed to start server: {e}")
            logger.error(traceback.format_exc())
            raise

    def stop(self):
        """Stop the server"""
        logger.info("Stopping server...")
        self.running = False

        # Stop beacon
        if self.beacon_running:
            self._stop_beacon()

        if self.receive_thread:
            self.receive_thread.join()
            logger.info("Receive thread stopped")
        if self.periodic_thread:
            self.periodic_thread.join()
            logger.info("Periodic thread stopped")

        # Stop Publisher thread
        self._publisher_running = False
        try:
            self._pub_queue.put_nowait((None, None))  # sentinel
        except Full:
            # Best-effort to make room, then send sentinel
            try:
                _ = self._pub_queue.get_nowait()
            except Empty:
                pass
            try:
                self._pub_queue.put_nowait((None, None))
            except Full:
                pass
        if self._publisher_thread:
            self._publisher_thread.join()
            logger.info("Publisher thread stopped")

        if self.router:
            self.router.close()
        if self.context:
            self.context.term()

        logger.info(
            f"Server stopped. Total messages processed: {self.message_count}, "
            f"Broadcasts sent: {self.broadcast_count}, Skipped broadcasts: {self.skipped_broadcasts}"
        )

    def _receive_loop(self):
        """Receive messages from clients"""
        logger.info("Receive loop started")

        while self.running:
            try:
                # Check for incoming messages
                if self.router.poll(self.POLL_TIMEOUT, zmq.POLLIN):
                    # Receive multipart message [identity, room_id, message]
                    parts = self.router.recv_multipart()
                    self._increment_stat("message_count")

                    if len(parts) >= 3:
                        client_identity = parts[0]
                        room_id_bytes = parts[1]
                        message_bytes = parts[2]
                        try:
                            room_id = room_id_bytes.decode("utf-8")
                        except UnicodeDecodeError as e:
                            logger.error(f"Failed to decode room ID: {e}")
                            continue
                        # Try binary first, fallback to JSON for compatibility
                        try:
                            msg_type, data, raw_payload = binary_serializer.deserialize(
                                message_bytes
                            )
                            if msg_type == binary_serializer.MSG_CLIENT_TRANSFORM:
                                self._handle_client_transform(
                                    client_identity, room_id, data, raw_payload
                                )
                            elif msg_type == binary_serializer.MSG_RPC:
                                # Get sender's client number from client identity
                                sender_device_id = self._get_device_id_from_identity(
                                    client_identity, room_id
                                )
                                if sender_device_id:
                                    sender_client_no = (
                                        self._get_client_no_for_device_id(
                                            room_id, sender_device_id
                                        )
                                    )
                                    data["senderClientNo"] = sender_client_no
                                # Send RPC to room excluding sender
                                self._send_rpc_to_room(room_id, data)
                            # MSG_RPC_SERVER and MSG_RPC_CLIENT are reserved for future use
                            elif msg_type == binary_serializer.MSG_GLOBAL_VAR_SET:
                                # Buffer global variable set request (no rate limit drops)
                                self._buffer_global_var_set(room_id, data)
                                self._monitor_nv_sliding_window(room_id)
                            elif msg_type == binary_serializer.MSG_CLIENT_VAR_SET:
                                # Buffer client variable set request (no rate limit drops)
                                self._buffer_client_var_set(room_id, data)
                                self._monitor_nv_sliding_window(room_id)
                            else:
                                logger.warning(f"Unknown binary msg_type: {msg_type}")
                        except Exception:
                            # Fallback to JSON for backward compatibility
                            try:
                                message = message_bytes.decode("utf-8")
                                self._process_message(client_identity, room_id, message)
                            except UnicodeDecodeError as e:
                                logger.error(f"Failed to decode message: {e}")

                    else:
                        logger.warning(
                            f"Received incomplete message with only {len(parts)} parts"
                        )

            except Exception as e:
                logger.error(f"Error in receive loop: {e}")
                logger.error(traceback.format_exc())

        logger.info("Receive loop ended")

    def _process_message(self, client_identity: bytes, room_id: str, message: str):
        """Process incoming client message"""
        try:
            msg_data = json.loads(message)
            msg_type = msg_data.get("type")
            data_str = msg_data.get("data", "{}")
            data = json.loads(data_str) if data_str else {}

            # Process client transform messages (support both enum and string formats)
            if msg_type in [0, "ClientTransform", "client_transform"]:
                self._handle_client_transform(client_identity, room_id, data)
            # JSON-based RPC broadcast
            elif msg_type in ["Rpc", "rpc"]:
                logger.info(f"JSON RPC received in room {room_id}: {data}")
                self._send_rpc_to_room(room_id, data)
            else:
                logger.warning(f"Unknown message type: {msg_type}")

        except json.JSONDecodeError as e:
            logger.error(f"Failed to parse JSON: {e}")
            logger.error(f"Raw message: {message}")
        except Exception as e:
            logger.error(f"Error processing message: {e}")
            logger.error(traceback.format_exc())

    def _handle_client_transform(
        self,
        client_identity: bytes,
        room_id: str,
        data: dict[str, Any],
        raw_payload: bytes = b"",
    ):
        """Handle client transform update"""
        device_id = data.get("deviceId")  # Receiving device ID from client

        # Detect stealth mode using NaN handshake
        is_stealth = binary_serializer._is_stealth_client(data)

        # Get or assign client number for this device ID
        client_no = self._get_or_assign_client_no(room_id, device_id)

        # Create modified data with client number for internal use
        data_with_client_no = data.copy()
        data_with_client_no["clientNo"] = client_no
        data_with_client_no["deviceId"] = device_id  # Keep device ID for reference

        with self._rooms_lock:
            # Create room if it doesn't exist
            self._initialize_room(room_id)

            # Update or create client (using device ID as key for backward compatibility)
            is_new_client = device_id not in self.rooms[room_id]
            if is_new_client:
                self.rooms[room_id][device_id] = {
                    "identity": client_identity,
                    "last_update": time.monotonic(),
                    "transform_data": data_with_client_no,
                    "client_no": client_no,
                    "is_stealth": is_stealth,
                }
                self.room_dirty_flags[room_id] = True  # Mark room as dirty
                # Cache the binary data if available
                if raw_payload:
                    # Store by client number for efficient broadcast
                    self.client_binary_cache[client_no] = raw_payload
                stealth_text = " (stealth mode)" if is_stealth else ""
                logger.info(
                    f"New client {device_id[:8]}... (client number: {client_no}){stealth_text} joined room {room_id}"
                )
            else:
                # Update existing client and mark room as dirty
                self.rooms[room_id][device_id]["transform_data"] = data_with_client_no
                self.rooms[room_id][device_id]["last_update"] = time.monotonic()
                self.rooms[room_id][device_id]["client_no"] = client_no
                self.rooms[room_id][device_id]["is_stealth"] = is_stealth

                # Update cached binary data if available
                if raw_payload:
                    self.client_binary_cache[client_no] = raw_payload

                # Mark room as dirty since transform data has arrived
                self.room_dirty_flags[room_id] = True

        # Broadcast ID mappings and Network Variables when a new client joins (outside of lock)
        if is_new_client:
            self._broadcast_id_mappings(room_id)
            self._sync_network_variables_to_new_client(room_id)

    def _send_rpc_to_room(self, room_id: str, rpc_data: dict[str, Any]):
        """Send RPC to all clients in room except sender"""
        # Log RPC
        sender_client_no = rpc_data.get("senderClientNo", 0)
        function_name = rpc_data.get("functionName", "unknown")
        args = rpc_data.get("args", [])
        logger.info(
            f"RPC: sender={sender_client_no}, function={function_name}, args={args}, room={room_id}"
        )

        # Prepare topic and payload
        topic_bytes = room_id.encode("utf-8")
        message_bytes = binary_serializer.serialize_rpc_message(rpc_data)

        # Send multipart [roomId, payload]
        self._enqueue_pub(topic_bytes, message_bytes)

    def _monitor_nv_sliding_window(self, room_id: str):
        """Monitor NV request rate for logging only (no gating)"""
        current_time = time.monotonic()
        with self._rooms_lock:
            if room_id not in self.nv_monitor_window:
                self.nv_monitor_window[room_id] = []

            window = self.nv_monitor_window[room_id]

            # Add current timestamp
            window.append(current_time)

            # Remove old timestamps outside window
            cutoff_time = current_time - self.nv_monitor_window_size
            self.nv_monitor_window[room_id] = [t for t in window if t > cutoff_time]

            # Check if over threshold and log warning
            if len(self.nv_monitor_window[room_id]) > self.nv_monitor_threshold:
                logger.warning(
                    f"High NV request rate in room {room_id}: {len(self.nv_monitor_window[room_id])} req/s"
                )

    def _buffer_global_var_set(self, room_id: str, data: dict[str, Any]):
        """Buffer global variable set request for later processing"""
        sender_client_no = data.get("senderClientNo", 0)
        var_name = data.get("variableName", "")[: self.MAX_VAR_NAME_LENGTH]
        var_value = data.get("variableValue", "")[: self.MAX_VAR_VALUE_LENGTH]
        timestamp = data.get("timestamp", time.monotonic())

        if not var_name:
            return

        with self._rooms_lock:
            self._initialize_room(room_id)

            # Buffer the update (latest-wins per key)
            self.pending_global_nv[room_id][var_name] = (
                sender_client_no,
                var_value,
                timestamp,
            )

    def _apply_global_var_set(
        self,
        room_id: str,
        sender_client_no: int,
        var_name: str,
        var_value: str,
        timestamp: float,
    ) -> bool:
        """Apply global variable update (used by flush, returns True if applied)"""
        with self._rooms_lock:
            # Check limits
            global_vars = self.global_variables[room_id]
            if var_name not in global_vars and len(global_vars) >= self.MAX_GLOBAL_VARS:
                logger.warning(f"Global variable limit reached in room {room_id}")
                return False

            # Conflict resolution: last-writer-wins with timestamp comparison
            if var_name in global_vars:
                existing = global_vars[var_name]
                # Skip if value unchanged (no-op)
                if existing.get("value") == var_value:
                    return False
                if timestamp < existing["timestamp"] or (
                    timestamp == existing["timestamp"]
                    and sender_client_no < existing["lastWriterClientNo"]
                ):
                    return False  # Ignore older or lower priority update

            # Store old value for logging
            old_value = global_vars.get(var_name, {}).get("value", None)

            # Update variable
            global_vars[var_name] = {
                "value": var_value,
                "timestamp": timestamp,
                "lastWriterClientNo": sender_client_no,
            }

            logger.info(
                f"Global Variable Changed: room={room_id}, client={sender_client_no}, name='{var_name}', old='{old_value}', new='{var_value}'"
            )
            return True

    def _handle_global_var_set(self, room_id: str, data: dict[str, Any]):
        """Handle global variable set request (for backward compat - immediate apply+broadcast)"""
        sender_client_no = data.get("senderClientNo", 0)
        var_name = data.get("variableName", "")[: self.MAX_VAR_NAME_LENGTH]
        var_value = data.get("variableValue", "")[: self.MAX_VAR_VALUE_LENGTH]
        timestamp = data.get("timestamp", time.monotonic())

        if not var_name:
            return

        if self._apply_global_var_set(
            room_id, sender_client_no, var_name, var_value, timestamp
        ):
            # Broadcast sync to all clients
            self._broadcast_global_var_sync(room_id)

    def _buffer_client_var_set(self, room_id: str, data: dict[str, Any]):
        """Buffer client variable set request for later processing"""
        sender_client_no = data.get("senderClientNo", 0)
        target_client_no = data.get("targetClientNo", 0)
        var_name = data.get("variableName", "")[: self.MAX_VAR_NAME_LENGTH]
        var_value = data.get("variableValue", "")[: self.MAX_VAR_VALUE_LENGTH]
        timestamp = data.get("timestamp", time.monotonic())

        if not var_name:
            return

        with self._rooms_lock:
            self._initialize_room(room_id)

            # Buffer the update (latest-wins per key)
            key = (target_client_no, var_name)
            self.pending_client_nv[room_id][key] = (
                sender_client_no,
                var_value,
                timestamp,
            )

    def _apply_client_var_set(
        self,
        room_id: str,
        sender_client_no: int,
        target_client_no: int,
        var_name: str,
        var_value: str,
        timestamp: float,
    ) -> bool:
        """Apply client variable update (used by flush, returns True if applied)"""
        with self._rooms_lock:
            # Initialize client variables for target if needed
            if target_client_no not in self.client_variables[room_id]:
                self.client_variables[room_id][target_client_no] = {}

            client_vars = self.client_variables[room_id][target_client_no]

            # Check limits
            if var_name not in client_vars and len(client_vars) >= self.MAX_CLIENT_VARS:
                logger.warning(
                    f"Client variable limit reached for client {target_client_no} in room {room_id}"
                )
                return False

            # Conflict resolution: last-writer-wins with timestamp comparison
            if var_name in client_vars:
                existing = client_vars[var_name]
                # Skip if value unchanged (no-op)
                if existing.get("value") == var_value:
                    return False
                if timestamp < existing["timestamp"] or (
                    timestamp == existing["timestamp"]
                    and sender_client_no < existing["lastWriterClientNo"]
                ):
                    return False  # Ignore older or lower priority update

            # Store old value for logging
            old_value = client_vars.get(var_name, {}).get("value", None)

            # Update variable
            client_vars[var_name] = {
                "value": var_value,
                "timestamp": timestamp,
                "lastWriterClientNo": sender_client_no,
            }

            logger.info(
                f"Client Variable Changed: room={room_id}, target={target_client_no}, sender={sender_client_no}, name='{var_name}', old='{old_value}', new='{var_value}'"
            )
            return True

    def _handle_client_var_set(self, room_id: str, data: dict[str, Any]):
        """Handle client variable set request (for backward compat - immediate apply+broadcast)"""
        sender_client_no = data.get("senderClientNo", 0)
        target_client_no = data.get("targetClientNo", 0)
        var_name = data.get("variableName", "")[: self.MAX_VAR_NAME_LENGTH]
        var_value = data.get("variableValue", "")[: self.MAX_VAR_VALUE_LENGTH]
        timestamp = data.get("timestamp", time.monotonic())

        if not var_name:
            return

        if self._apply_client_var_set(
            room_id, sender_client_no, target_client_no, var_name, var_value, timestamp
        ):
            # Broadcast sync to all clients
            self._broadcast_client_var_sync(room_id)

    def _broadcast_global_var_sync(self, room_id: str):
        """Broadcast global variables sync to all clients in room"""
        with self._rooms_lock:
            if room_id not in self.global_variables:
                return

            variables = []
            for var_name, var_data in self.global_variables[room_id].items():
                variables.append(
                    {
                        "name": var_name,
                        "value": var_data["value"],
                        "timestamp": var_data["timestamp"],
                        "lastWriterClientNo": var_data["lastWriterClientNo"],
                    }
                )

            if variables:
                topic_bytes = room_id.encode("utf-8")
                message_bytes = binary_serializer.serialize_global_var_sync(
                    {"variables": variables}
                )
                self._enqueue_pub(topic_bytes, message_bytes)
                logger.debug(
                    f"Broadcasted {len(variables)} global variables to room {room_id}"
                )

    def _broadcast_client_var_sync(self, room_id: str):
        """Broadcast client variables sync to all clients in room"""
        with self._rooms_lock:
            if room_id not in self.client_variables:
                return

            client_variables = {}
            for client_no, variables in self.client_variables[room_id].items():
                client_vars = []
                for var_name, var_data in variables.items():
                    client_vars.append(
                        {
                            "name": var_name,
                            "value": var_data["value"],
                            "timestamp": var_data["timestamp"],
                            "lastWriterClientNo": var_data["lastWriterClientNo"],
                        }
                    )
                if client_vars:
                    client_variables[str(client_no)] = client_vars

            if client_variables:
                topic_bytes = room_id.encode("utf-8")
                message_bytes = binary_serializer.serialize_client_var_sync(
                    {"clientVariables": client_variables}
                )
                self._enqueue_pub(topic_bytes, message_bytes)
                logger.debug(
                    f"Broadcasted client variables for {len(client_variables)} clients to room {room_id}"
                )

    def _sync_network_variables_to_new_client(self, room_id: str):
        """Send current Network Variables state to a newly connected client"""
        self._broadcast_global_var_sync(room_id)
        self._broadcast_client_var_sync(room_id)

    def _flush_nv_drain(self, room_id: str) -> None:
        """Drain all pending NV updates for a room in one go."""
        start = time.perf_counter()

        with self._rooms_lock:
            pending_globals = self.pending_global_nv.get(room_id, {})
            pending_clients = self.pending_client_nv.get(room_id, {})
            globals_to_apply = list(pending_globals.items())
            clients_to_apply = list(pending_clients.items())
            self.pending_global_nv[room_id] = {}
            self.pending_client_nv[room_id] = {}

        applied_globals: list[str] = []
        for var_name, (sender, value, ts) in globals_to_apply:
            if self._apply_global_var_set(room_id, sender, var_name, value, ts):
                applied_globals.append(var_name)

        applied_clients: dict[int, list[dict[str, Any]]] = {}
        for (target_client_no, var_name), (sender, value, ts) in clients_to_apply:
            if self._apply_client_var_set(
                room_id, sender, target_client_no, var_name, value, ts
            ):
                applied_clients.setdefault(target_client_no, []).append(
                    {
                        "name": var_name,
                        "value": value,
                        "timestamp": ts,
                        "lastWriterClientNo": sender,
                    }
                )

        topic = room_id.encode("utf-8")
        if applied_globals:
            vars_payload = []
            with self._rooms_lock:
                for name in applied_globals:
                    d = self.global_variables[room_id][name]
                    vars_payload.append(
                        {
                            "name": name,
                            "value": d["value"],
                            "timestamp": d["timestamp"],
                            "lastWriterClientNo": d["lastWriterClientNo"],
                        }
                    )
            msg = binary_serializer.serialize_global_var_sync(
                {"variables": vars_payload}
            )
            self._enqueue_pub(topic, msg)

        if applied_clients:
            client_vars = {str(cno): lst for cno, lst in applied_clients.items()}
            msg = binary_serializer.serialize_client_var_sync(
                {"clientVariables": client_vars}
            )
            self._enqueue_pub(topic, msg)

        elapsed_ms = (time.perf_counter() - start) * 1000.0
        logger.info(f"NV flush took {elapsed_ms:.2f} ms (room={room_id})")

    def _flush_nv_rate_limited(self, room_id: str) -> None:
        """Flush NV updates using existing rate-limited fairness logic."""
        now = time.monotonic()
        with self._rooms_lock:
            last_flush = self.room_last_nv_flush.get(room_id, 0.0)
            pending_global = self.pending_global_nv.get(room_id, {})
            pending_client = self.pending_client_nv.get(room_id, {})
            if now - last_flush < self.nv_flush_interval:
                return
            if not pending_global and not pending_client:
                return

            interval = now - last_flush if last_flush > 0 else self.nv_flush_interval

            room_allowance = self.room_nv_allowance.get(room_id, 0.0)
            room_allowance = min(1.0, room_allowance + self.nv_per_room_cap * interval)
            self.room_nv_allowance[room_id] = room_allowance

            client_updates: dict[int, list[tuple[bool, Any, tuple]]] = {}
            for var_name, (sender_no, value, ts) in pending_global.items():
                client_updates.setdefault(sender_no, []).append(
                    (True, var_name, (sender_no, value, ts))
                )
            for (target_no, var_name), (sender_no, value, ts) in pending_client.items():
                client_updates.setdefault(sender_no, []).append(
                    (False, (target_no, var_name), (sender_no, value, ts))
                )

            client_allowances = self.room_client_nv_allowance.get(room_id, {})
            for client_no in client_updates.keys():
                client_allowances[client_no] = min(
                    1.0,
                    client_allowances.get(client_no, 0.0)
                    + self.nv_per_client_rate * interval,
                )
            self.room_client_nv_allowance[room_id] = client_allowances

        global_dirty = False
        client_dirty = False
        processed_global: list[str] = []
        processed_client: list[tuple[int, str]] = []

        client_list = list(client_updates.keys())
        max_safety_iterations = 1000
        iteration_count = 0

        while client_list and iteration_count < max_safety_iterations:
            progress_made = False
            clients_to_process = len(client_list)

            for _ in range(clients_to_process):
                if not client_list:
                    break
                iteration_count += 1
                client_no = client_list.pop(0)

                if client_no not in client_updates or not client_updates[client_no]:
                    continue

                if client_allowances.get(client_no, 0.0) < 1.0:
                    client_list.append(client_no)
                    continue

                if room_allowance < 1.0:
                    client_list.insert(0, client_no)
                    break

                is_global, key, data = client_updates[client_no].pop(0)
                if is_global:
                    var_name = key
                    sender_no, value, ts = data
                    if self._apply_global_var_set(
                        room_id, sender_no, var_name, value, ts
                    ):
                        global_dirty = True
                        processed_global.append(var_name)
                        client_allowances[client_no] -= 1.0
                        room_allowance -= 1.0
                        progress_made = True
                else:
                    target_no, var_name = key
                    sender_no, value, ts = data
                    if self._apply_client_var_set(
                        room_id, sender_no, target_no, var_name, value, ts
                    ):
                        client_dirty = True
                        processed_client.append((target_no, var_name))
                        client_allowances[client_no] -= 1.0
                        room_allowance -= 1.0
                        progress_made = True

                if client_updates[client_no]:
                    client_list.append(client_no)

            if not progress_made or room_allowance < 1.0:
                break

        if iteration_count >= max_safety_iterations:
            logger.warning(
                f"Network variable processing hit safety limit for room {room_id}"
            )

        with self._rooms_lock:
            self.room_client_nv_allowance[room_id] = client_allowances
            self.room_nv_allowance[room_id] = room_allowance
            for var_name in processed_global:
                del self.pending_global_nv[room_id][var_name]
            for key in processed_client:
                del self.pending_client_nv[room_id][key]

        if global_dirty:
            self._broadcast_global_var_sync(room_id)
        if client_dirty:
            self._broadcast_client_var_sync(room_id)

    def _broadcast_id_mappings(self, room_id: str):
        """Broadcast all device ID mappings for a room (including stealth clients with flag)"""
        with self._rooms_lock:
            if room_id not in self.room_device_id_to_client_no:
                return

            # Collect all mappings for the room (including stealth clients with their flag)
            mappings = []
            for device_id, client_no in self.room_device_id_to_client_no[
                room_id
            ].items():
                # Get stealth status from client data
                client_data = self.rooms.get(room_id, {}).get(device_id, {})
                is_stealth = client_data.get("is_stealth", False)
                # Include all clients with their stealth flag
                mappings.append((client_no, device_id, is_stealth))

            if mappings:
                # Serialize and broadcast the mappings
                topic_bytes = room_id.encode("utf-8")
                message_bytes = binary_serializer.serialize_device_id_mapping(mappings)
                self._enqueue_pub(topic_bytes, message_bytes)
                logger.info(
                    f"Broadcasted {len(mappings)} ID mappings to room {room_id}: {[(cno, did[:8], 'stealth' if stealth else 'normal') for cno, did, stealth in mappings]}"
                )

    def _periodic_loop(self):
        """Combined broadcast and cleanup loop with adaptive rates"""
        logger.info("Periodic loop started")
        last_broadcast_check = 0
        last_cleanup = 0
        last_device_id_cleanup = 0
        last_log = time.monotonic()
        DEVICE_ID_CLEANUP_INTERVAL = 60.0  # Clean up expired device IDs every minute

        while self.running:
            try:
                current_time = time.monotonic()

                # Check for broadcasts at higher frequency but only broadcast when needed
                if current_time - last_broadcast_check >= self.BROADCAST_CHECK_INTERVAL:
                    self._adaptive_broadcast_all_rooms(current_time)

                    # Flush Network Variables after other categories
                    with self._rooms_lock:
                        rooms = list(self.rooms.keys())
                    for room_id in rooms:
                        last_flush = self.room_last_nv_flush.get(room_id, 0.0)
                        if current_time - last_flush >= self.nv_flush_interval:
                            if self.nv_flush_policy == "drain":
                                self._flush_nv_drain(room_id)
                            else:
                                self._flush_nv_rate_limited(room_id)
                            self.room_last_nv_flush[room_id] = current_time

                    last_broadcast_check = current_time

                # Cleanup at regular intervals
                if current_time - last_cleanup >= self.CLEANUP_INTERVAL:
                    self._cleanup_clients(current_time)
                    last_cleanup = current_time

                # Clean up expired device ID mappings periodically
                if current_time - last_device_id_cleanup >= DEVICE_ID_CLEANUP_INTERVAL:
                    self._cleanup_expired_device_id_mappings(current_time)
                    last_device_id_cleanup = current_time

                # Log status periodically
                if current_time - last_log >= self.STATUS_LOG_INTERVAL:
                    # Count normal and stealth clients separately
                    normal_clients = 0
                    stealth_clients = 0
                    for room in self.rooms.values():
                        for client in room.values():
                            if client.get("is_stealth", False):
                                stealth_clients += 1
                            else:
                                normal_clients += 1

                    dirty_rooms = sum(
                        1 for flag in self.room_dirty_flags.values() if flag
                    )
                    total_device_ids = len(self.device_id_last_seen)
                    logger.info(
                        f"Status: {len(self.rooms)} rooms, {normal_clients} normal clients, "
                        f"{stealth_clients} stealth clients, "
                        f"{dirty_rooms} dirty rooms, {total_device_ids} tracked device IDs"
                    )
                    last_log = current_time

                time.sleep(self.MAIN_LOOP_SLEEP)  # 50Hz loop for better responsiveness

            except Exception as e:
                logger.error(f"Error in periodic loop: {e}")

        logger.info("Periodic loop ended")

    def _adaptive_broadcast_all_rooms(self, current_time):
        """Broadcast room state with adaptive rates based on activity"""
        with self._rooms_lock:
            rooms_copy = list(
                self.rooms.items()
            )  # Create copy to avoid holding lock too long

        for room_id, clients in rooms_copy:
            if not clients:  # Skip empty rooms
                continue

            with self._rooms_lock:
                # Check if room needs broadcasting
                last_broadcast = self.room_last_broadcast.get(room_id, 0)
                is_dirty = self.room_dirty_flags.get(room_id, False)

                # Calculate time since last broadcast
                time_since_broadcast = current_time - last_broadcast

                # Determine if we should broadcast based on activity and timing
                should_broadcast = False

                if is_dirty:
                    # Active room - broadcast if minimum dirty threshold time has passed
                    if time_since_broadcast >= self.dirty_threshold:
                        should_broadcast = True
                else:
                    # Idle room - broadcast only if idle interval has passed
                    if time_since_broadcast >= self.idle_broadcast_interval:
                        should_broadcast = True

                if should_broadcast:
                    self._broadcast_room(room_id, clients)
                    self.room_dirty_flags[room_id] = False  # Clear dirty flag
                    self.room_last_broadcast[room_id] = current_time
                else:
                    self._increment_stat("skipped_broadcasts")

    def _broadcast_room(self, room_id, clients):
        """Broadcast a specific room's state"""
        # Filter out stealth clients from broadcasts
        client_transforms = [
            client["transform_data"]
            for client in clients.values()
            if client["transform_data"] and not client.get("is_stealth", False)
        ]

        if client_transforms:
            room_transform = {
                "roomId": room_id,
                "clients": client_transforms,
            }

            # Send binary format with client numbers
            topic_bytes = room_id.encode("utf-8")
            message_bytes = binary_serializer.serialize_room_transform(room_transform)
            self._enqueue_pub(topic_bytes, message_bytes)

    def _cleanup_clients(self, current_time):
        """Clean up disconnected clients with atomic operations to prevent memory leaks"""
        timeout = self.CLIENT_TIMEOUT

        with self._rooms_lock:
            rooms_to_remove = []

            for room_id, clients in list(self.rooms.items()):
                clients_to_remove = []

                # Use items() to avoid repeated dict lookups
                for device_id, client_data in clients.items():
                    if current_time - client_data["last_update"] > timeout:
                        clients_to_remove.append(device_id)

                # Remove timed out clients in batch
                if clients_to_remove:
                    for device_id in clients_to_remove:
                        client_no = clients[device_id].get("client_no")
                        del clients[device_id]
                        # Clean up binary cache by client number
                        if client_no and client_no in self.client_binary_cache:
                            del self.client_binary_cache[client_no]
                        # Note: We don't remove device ID->clientNo mapping here
                        # It will be cleaned up after DEVICE_ID_EXPIRY_TIME
                        logger.info(
                            f"Client {device_id[:8]}... (client number: {client_no}) removed (timeout)"
                        )

                    # Mark room as dirty since clients were removed
                    self.room_dirty_flags[room_id] = True

                    # Broadcast updated ID mappings after client removal
                    self._broadcast_id_mappings(room_id)

                # Mark empty rooms for removal
                if not clients:
                    rooms_to_remove.append(room_id)

            # Remove empty rooms and clean up ALL tracking data
            for room_id in rooms_to_remove:
                try:
                    # Delete from all room-related data structures
                    if room_id in self.rooms:
                        del self.rooms[room_id]
                    if room_id in self.room_dirty_flags:
                        del self.room_dirty_flags[room_id]
                    if room_id in self.room_last_broadcast:
                        del self.room_last_broadcast[room_id]
                    if room_id in self.room_client_no_counters:
                        del self.room_client_no_counters[room_id]
                    if room_id in self.room_device_id_to_client_no:
                        del self.room_device_id_to_client_no[room_id]
                    if room_id in self.room_client_no_to_device_id:
                        del self.room_client_no_to_device_id[room_id]

                    # Clean up NV-related structures
                    if room_id in self.global_variables:
                        del self.global_variables[room_id]
                    if room_id in self.client_variables:
                        del self.client_variables[room_id]
                    if room_id in self.pending_global_nv:
                        del self.pending_global_nv[room_id]
                    if room_id in self.pending_client_nv:
                        del self.pending_client_nv[room_id]
                    if room_id in self.room_last_nv_flush:
                        del self.room_last_nv_flush[room_id]
                    if room_id in self.room_client_nv_allowance:
                        del self.room_client_nv_allowance[room_id]
                    if room_id in self.room_nv_allowance:
                        del self.room_nv_allowance[room_id]
                    if room_id in self.nv_monitor_window:
                        del self.nv_monitor_window[room_id]

                    logger.info(f"Removed empty room: {room_id}")

                except Exception as e:
                    logger.error(f"Error during room cleanup for {room_id}: {e}")
                    # Continue with other rooms even if one fails

    def _start_beacon(self):
        """Start server discovery service to respond to client requests"""
        try:
            self.beacon_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            self.beacon_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            self.beacon_socket.bind(("", self.beacon_port))
            self.beacon_socket.settimeout(1.0)  # Timeout for graceful shutdown

            self.beacon_running = True
            self.beacon_thread = threading.Thread(
                target=self._beacon_loop, name="BeaconThread", daemon=True
            )
            self.beacon_thread.start()
            logger.info(f"Started discovery service on UDP port {self.beacon_port}")

        except Exception as e:
            logger.error(f"Failed to start discovery service: {e}")
            self.beacon_running = False

    def _stop_beacon(self):
        """Stop server discovery service"""
        self.beacon_running = False

        if self.beacon_socket:
            self.beacon_socket.close()
            self.beacon_socket = None

        if self.beacon_thread:
            self.beacon_thread.join(timeout=2)
            self.beacon_thread = None

        logger.info("Stopped discovery service")

    def _beacon_loop(self):
        """Discovery service loop - responds to client discovery requests"""
        # Response message format: STYLY-NETSYNC|dealerPort|pubPort|serverName
        response = (
            f"STYLY-NETSYNC|{self.dealer_port}|{self.pub_port}|{self.server_name}"
        )
        response_bytes = response.encode("utf-8")

        while self.beacon_running:
            try:
                # Wait for client discovery request
                data, client_addr = self.beacon_socket.recvfrom(1024)
                request = data.decode("utf-8")

                # Validate request format
                if request == "STYLY-NETSYNC-DISCOVER":
                    # Send response back to requesting client
                    self.beacon_socket.sendto(response_bytes, client_addr)
                    logger.debug(f"Responded to discovery request from {client_addr}")

            except TimeoutError:
                # Timeout is expected for graceful shutdown
                continue
            except Exception as e:
                if (
                    self.beacon_running
                ):  # Only log if we're still supposed to be running
                    logger.error(f"Discovery service error: {e}")


def display_logo():
    logo = """
CgobWzM4OzU7MjE2bSDilojilojilojilojilojilojilojilZcg4paI4paIG1szODs1OzIxMG3ilojilojilojilojilojilojilZcg4paI4paI4pWXICAg4paI4paI4pWXIOKWiOKWiOKVlyAgICAbWzM4OzU7MjE2bSAg4paI4paI4pWXICAg4paI4paI4pWXG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWU4pWQ4pWQ4pWQ4pWQ4pWdIOKVmuKVkBtbMzg7NTsyMTBt4pWQ4paI4paI4pWU4pWQ4pWQ4pWdIOKVmuKWiOKWiOKVlyDilojilojilZTilZ0g4paI4paI4pWRICAgIBtbMzg7NTsyMTZtICDilZrilojilojilZcg4paI4paI4pWU4pWdG1szOW0KG1szODs1OzIxNm0g4paI4paI4paI4paI4paI4paI4paI4pWXICAgG1szODs1OzIxMG0g4paI4paI4pWRICAgICDilZrilojilojilojilojilZTilZ0gIOKWiOKWiOKVkSAgICAbWzM4OzU7MjE2bSAgIOKVmuKWiOKWiOKWiOKWiOKVlOKVnSAbWzM5bQobWzM4OzU7MjE2bSDilZrilZDilZDilZDilZDilojilojilZEgICAbWzM4OzU7MjEwbSDilojilojilZEgICAgICDilZrilojilojilZTilZ0gICDilojilojilZEgICAgG1szODs1OzIxNm0gICAg4pWa4paI4paI4pWU4pWdICAbWzM5bQobWzM4OzU7MjE2bSDilojilojilojilojilojilojilojilZEgICAbWzM4OzU7MjEwbSDilojilojilZEgICAgICAg4paI4paI4pWRICAgIOKWiOKWiOKWiOKWiOKWiOKWiOKWiBtbMzg7NTsyMTZt4pWXICAgIOKWiOKWiOKVkSAgIBtbMzltChtbMzg7NTsyMTZtIOKVmuKVkOKVkOKVkOKVkOKVkOKVkOKVnSAgIBtbMzg7NTsyMTBtIOKVmuKVkOKVnSAgICAgICDilZrilZDilZ0gICAg4pWa4pWQ4pWQ4pWQ4pWQ4pWQ4pWQG1szODs1OzIxNm3ilZ0gICAg4pWa4pWQ4pWdICAgG1szOW0KChtbMzg7NTsyMTZtIOKWiOKWiOKWiOKVlyAgIOKWiOKWiOKVlyDilojilojilojilojilojilogbWzM4OzU7MjEwbeKWiOKVlyDilojilojilojilojilojilojilojilojilZcg4paI4paI4paI4paI4paI4paI4paI4pWXIOKWiOKWiOKVlyAgIOKWiOKWiOKVlyDilojilojilojilZcbWzM4OzU7MjE2bSAgIOKWiOKWiOKVlyAg4paI4paI4paI4paI4paI4paI4pWXG1szOW0KG1szODs1OzIxNm0g4paI4paI4paI4paI4pWXICDilojilojilZEg4paI4paI4pWU4pWQ4pWQ4pWQG1szODs1OzIxMG3ilZDilZ0g4pWa4pWQ4pWQ4paI4paI4pWU4pWQ4pWQ4pWdIOKWiOKWiOKVlOKVkOKVkOKVkOKVkOKVnSDilZrilojilojilZcg4paI4paI4pWU4pWdIOKWiOKWiOKWiOKWiBtbMzg7NTsyMTZt4pWXICDilojilojilZEg4paI4paI4pWU4pWQ4pWQ4pWQ4pWQ4pWdG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWU4paI4paI4pWXIOKWiOKWiOKVkSDilojilojilojilojilojilZcbWzM4OzU7MjEwbSAgICAgIOKWiOKWiOKVkSAgICDilojilojilojilojilojilojilojilZcgIOKVmuKWiOKWiOKWiOKWiOKVlOKVnSAg4paI4paI4pWU4paIG1szODs1OzIxNm3ilojilZcg4paI4paI4pWRIOKWiOKWiOKVkSAgICAgG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWR4pWa4paI4paI4pWX4paI4paI4pWRIOKWiOKWiOKVlOKVkOKVkOKVnRtbMzg7NTsyMTBtICAgICAg4paI4paI4pWRICAgIOKVmuKVkOKVkOKVkOKVkOKWiOKWiOKVkSAgIOKVmuKWiOKWiOKVlOKVnSAgIOKWiOKWiOKVkeKVmhtbMzg7NTsyMTZt4paI4paI4pWX4paI4paI4pWRIOKWiOKWiOKVkSAgICAgG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWRIOKVmuKWiOKWiOKWiOKWiOKVkSDilojilojilojilojilojilogbWzM4OzU7MjEwbeKWiOKVlyAgICDilojilojilZEgICAg4paI4paI4paI4paI4paI4paI4paI4pWRICAgIOKWiOKWiOKVkSAgICDilojilojilZEgG1szODs1OzIxNm3ilZrilojilojilojilojilZEg4pWa4paI4paI4paI4paI4paI4paI4pWXG1szOW0KG1szODs1OzIxNm0g4pWa4pWQ4pWdICDilZrilZDilZDilZDilZ0g4pWa4pWQ4pWQ4pWQ4pWQ4pWQG1szODs1OzIxMG3ilZDilZ0gICAg4pWa4pWQ4pWdICAgIOKVmuKVkOKVkOKVkOKVkOKVkOKVkOKVnSAgICDilZrilZDilZ0gICAg4pWa4pWQ4pWdIBtbMzg7NTsyMTZtIOKVmuKVkOKVkOKVkOKVnSAg4pWa4pWQ4pWQ4pWQ4pWQ4pWQ4pWdG1szOW0KCgo=
""".strip()
    sys.stdout.buffer.write(base64.b64decode(logo))
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser(description="STYLY NetSync Server")
    parser.add_argument(
        "--dealer-port",
        type=int,
        default=5555,
        help="Port for DEALER socket (default: 5555)",
    )
    parser.add_argument(
        "--pub-port", type=int, default=5556, help="Port for PUB socket (default: 5556)"
    )
    parser.add_argument(
        "--beacon-port",
        type=int,
        default=9999,
        help="Port for UDP beacon discovery (default: 9999)",
    )
    parser.add_argument(
        "--name",
        default="STYLY-NetSync-Server",
        help="Server name for discovery (default: STYLY-NetSync-Server)",
    )
    parser.add_argument(
        "--no-beacon", action="store_true", help="Disable beacon discovery"
    )
    parser.add_argument(
        "--nv-flush-policy",
        choices=["drain", "rate_limited"],
        default="drain",
        help="Network variable flush policy (default: drain)",
    )
    parser.add_argument(
        "-V",
        "--version",
        action="version",
        version=f"%(prog)s {get_version()}",
        help="Show version and exit",
    )

    args = parser.parse_args()

    display_logo()

    logger.info("=" * 80)
    logger.info("STYLY NetSync Server Starting")
    logger.info("=" * 80)
    logger.info(f"  Version: {get_version()}")
    logger.info(f"  DEALER port: {args.dealer_port}")
    logger.info(f"  PUB port: {args.pub_port}")
    if not args.no_beacon:
        logger.info(f"  Beacon port: {args.beacon_port}")
        logger.info(f"  Server name: {args.name}")
    else:
        logger.info("  Discovery: Disabled")
    logger.info(f"  NV flush policy: {args.nv_flush_policy}")
    logger.info("=" * 80)

    server = NetSyncServer(
        dealer_port=args.dealer_port,
        pub_port=args.pub_port,
        enable_beacon=not args.no_beacon,
        beacon_port=args.beacon_port,
        server_name=args.name,
        nv_flush_policy=args.nv_flush_policy,
    )

    try:
        server.start()

        logger.info("Server started successfully. Press Ctrl+C to stop.")

        # Keep server running with proper signal handling
        while True:
            try:
                time.sleep(1)
            except KeyboardInterrupt:
                # Handle Ctrl+C gracefully
                logger.info("\nReceived interrupt signal (Ctrl+C)...")
                break

    except SystemExit:
        # Server failed to start due to port already in use
        logger.info("Server startup failed. Exiting...")
        return
    except KeyboardInterrupt:
        # Handle Ctrl+C during startup
        logger.info("\nReceived interrupt signal during startup...")
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        logger.error(traceback.format_exc())
    finally:
        # Always try to stop the server cleanly
        try:
            server.stop()
        except Exception as e:
            logger.error(f"Error during server shutdown: {e}")
        logger.info("Server shutdown complete.")
