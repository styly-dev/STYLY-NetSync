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
import os
import platform
import socket
import threading
import time
import traceback
from functools import lru_cache
from pathlib import Path
from queue import Empty, Full, Queue
import struct
from types import ModuleType
from typing import Any, TYPE_CHECKING, cast

import zmq.sugar.socket

resource: ModuleType | None
try:
    import resource as _resource

    resource = _resource
except Exception:
    resource = None

import zmq
from loguru import logger
from . import binary_serializer
from . import network_utils
from .logging_utils import configure_logging
from .config import (
    ConfigurationError,
    DefaultConfigError,
    ServerConfig,
    create_config_from_args,
    load_default_config,
)

if TYPE_CHECKING:
    from uvicorn import Server

# Note: Default values are defined in default.toml, not in code.
# Use load_default_config() from config module to get defaults.


def valid_port(value: str) -> int:
    """Return a validated TCP/UDP port number."""
    try:
        port = int(value)
    except ValueError as exc:  # pragma: no cover - argparse handles messaging
        raise argparse.ArgumentTypeError("Port must be an integer") from exc

    if not 1 <= port <= 65535:
        raise argparse.ArgumentTypeError("Port must be between 1 and 65535")

    return port


@lru_cache(maxsize=1)
def get_version() -> str:
    """
    Return the server version.
    Priority (updated for development mode support):
      1) parse pyproject.toml if in development mode (editable install)
      2) importlib.metadata for 'styly-netsync-server' (when installed normally)
      3) 'unknown'
    """
    # Python 3.11+ guaranteed, so we can use these imports directly
    import importlib.metadata as im
    import tomllib

    # Helper function to detect development mode
    def is_development_mode() -> bool:
        """Detect if package is installed in development mode (pip install -e .)"""
        try:
            dist = im.distribution("styly-netsync-server")
            # Development installs typically have .egg-link or direct path references
            if hasattr(dist, "files") and dist.files:
                # Check if any file path contains 'site-packages' - normal install
                # vs direct source paths - development install
                for file in list(dist.files)[:5]:  # Check first few files
                    if file and "site-packages" not in str(file):
                        return True
            return False
        except im.PackageNotFoundError:
            return False  # If not found in metadata, not development mode

    # 1) For development mode, prioritize pyproject.toml
    if is_development_mode():
        for parent in Path(__file__).resolve().parents:
            toml_path = parent / "pyproject.toml"
            if toml_path.exists():
                try:
                    data = tomllib.loads(toml_path.read_text(encoding="utf-8"))
                    v = (data.get("project") or {}).get("version")
                    if v:
                        return cast(str, v)
                except (tomllib.TOMLDecodeError, OSError, UnicodeDecodeError):
                    # Continue to next fallback if TOML parsing fails
                    pass
                break

    # 2) Try installed package metadata
    try:
        return im.version("styly-netsync-server")
    except im.PackageNotFoundError:
        # Fallback: reverse lookup from package name to distribution
        for dist in im.packages_distributions().get("styly_netsync", []):
            try:
                return im.version(dist)
            except im.PackageNotFoundError:
                pass

    # 3) Final fallback - parse pyproject.toml (for cases where dev mode detection failed)
    for parent in Path(__file__).resolve().parents:
        toml_path = parent / "pyproject.toml"
        if toml_path.exists():
            try:
                data = tomllib.loads(toml_path.read_text(encoding="utf-8"))
                v = (data.get("project") or {}).get("version")
                if v:
                    return cast(str, v)
            except (tomllib.TOMLDecodeError, OSError, UnicodeDecodeError):
                pass
            break

    return "unknown"


class NetSyncServer:
    # Note: All default values are defined in default.toml, not in code.
    # The BROADCAST_CHECK_INTERVAL is derived from transform_broadcast_rate in config.

    # Non-configurable constants
    ROUTER_BACKLOG = 512  # Accept queue depth for DEALER/ROUTER connections
    PUB_BACKLOG = 512  # Accept queue depth for SUB connections
    DEFAULT_FD_LIMIT = 4096
    ID_MAPPING_DEBOUNCE_INTERVAL = 0.5  # Debounce ID mapping broadcasts (500ms)

    # Control traffic priority settings:
    # - CTRL_DRAIN_BATCH: Max control messages (RPC/NV) to drain per publisher loop
    # - CTRL_BACKLOG_WATERMARK: Skip transform work when control queue exceeds this
    # - TRANSFORM_BUDGET_BYTES_PER_SEC: Token bucket rate limit for transform broadcasts
    # - BACKLOG_SLEEP_SEC: Sleep duration when control backlog is high (5ms)
    # - MAX_COALESCE_BUFFER_SIZE: Max rooms in coalesce buffer before dropping oldest
    # - ROUTER_CTRL_DRAIN_BATCH: Max control messages to drain via ROUTER per receive loop
    # - ROUTER_CTRL_QUEUE_MAXSIZE: Max size for router control queue (ring buffer)
    CTRL_DRAIN_BATCH = 256
    ROUTER_CTRL_DRAIN_BATCH = 64
    ROUTER_CTRL_QUEUE_MAXSIZE = 1024
    CTRL_BACKLOG_WATERMARK = 500
    TRANSFORM_BUDGET_BYTES_PER_SEC = 15_000_000
    BACKLOG_SLEEP_SEC = 0.005
    MAX_COALESCE_BUFFER_SIZE = 1000

    def __init__(
        self,
        dealer_port: int | None = None,
        pub_port: int | None = None,
        enable_server_discovery: bool | None = None,
        server_discovery_port: int | None = None,
        server_name: str | None = None,
        config: ServerConfig | None = None,
    ):
        # Load default config if not provided
        if config is None:
            config = load_default_config()

        # Store config for reference
        self._config = config

        # Apply overrides from individual arguments (for backward compatibility)
        self.dealer_port = (
            dealer_port if dealer_port is not None else config.dealer_port
        )
        self.pub_port = pub_port if pub_port is not None else config.pub_port
        self.context = zmq.Context()

        # Initialize timing settings from config
        self.IDLE_BROADCAST_INTERVAL = config.idle_broadcast_interval
        # Convert transform_broadcast_rate (Hz) to interval (seconds)
        broadcast_interval = 1.0 / config.transform_broadcast_rate
        self.DIRTY_THRESHOLD = broadcast_interval
        self.BROADCAST_CHECK_INTERVAL = broadcast_interval
        self.CLEANUP_INTERVAL = config.cleanup_interval
        self.STATUS_LOG_INTERVAL = config.status_log_interval
        self.MAIN_LOOP_SLEEP = config.main_loop_sleep
        self.CLIENT_TIMEOUT = config.client_timeout
        self.DEVICE_ID_EXPIRY_TIME = config.device_id_expiry_time
        self.EMPTY_ROOM_EXPIRY_TIME = config.empty_room_expiry_time
        self.POLL_TIMEOUT = config.poll_timeout

        # Server discovery settings (with override support)
        self.enable_server_discovery = (
            enable_server_discovery
            if enable_server_discovery is not None
            else config.enable_server_discovery
        )
        self.server_discovery_port = (
            server_discovery_port
            if server_discovery_port is not None
            else config.server_discovery_port
        )
        self.server_name = (
            server_name if server_name is not None else config.server_name
        )
        self.server_discovery_socket: socket.socket | None = None
        self.server_discovery_thread: threading.Thread | None = None
        self.server_discovery_running = False

        # TCP server discovery settings
        self.tcp_server_discovery_socket: socket.socket | None = None
        self.tcp_server_discovery_thread: threading.Thread | None = None
        self.tcp_server_discovery_running = False

        # Sockets
        self.router: zmq.sugar.socket.Socket[bytes] | None = None
        self.pub: zmq.sugar.socket.Socket[bytes] | None = (
            None  # Will be created/owned by Publisher thread only
        )

        # Publisher thread infrastructure
        self._pub_queue_ctrl: Queue[tuple[bytes | None, bytes | None]] = Queue(
            maxsize=config.pub_queue_maxsize
        )
        self._publisher_thread: threading.Thread | None = None
        self._publisher_running = False
        self._pub_ready = threading.Event()  # signaled after successful bind
        self._publisher_exception: Exception | None = None  # bind/run errors stored
        self._transform_tokens = float(self.TRANSFORM_BUDGET_BYTES_PER_SEC)
        self._transform_last_refill = time.monotonic()
        self._transform_budget_lock = threading.Lock()

        # Router control queue for unicast control messages (RPC/NV/ID mapping)
        # Format: (identity, room_id_bytes, message_bytes)
        self._router_queue_ctrl: Queue[tuple[bytes, bytes, bytes]] = Queue(
            maxsize=self.ROUTER_CTRL_QUEUE_MAXSIZE
        )

        # Statistics for router control messages
        self.ctrl_unicast_sent = 0
        self.ctrl_unicast_wouldblock = 0
        self.ctrl_unicast_dropped = 0

        # PUB socket monitor for tracking SUB connections
        self._pub_monitor: zmq.sugar.socket.Socket[bytes] | None = None
        self._pub_monitor_thread: threading.Thread | None = None
        self._sub_connection_count = 0
        self._sub_connection_lock = threading.Lock()

        # Latest-only coalescing buffer for high-rate topics (e.g., room transforms)
        self._coalesce_lock = threading.Lock()
        # topic_bytes -> message_bytes (keep only the most-recent per topic)
        self._coalesce_latest: dict[bytes, bytes] = {}

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
        self.client_transform_body_cache: dict[int, bytes] = (
            {}
        )  # Cache client_no -> body data (room transform without deviceId)

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

        # Empty room tracking for delayed removal
        self.room_empty_since: dict[str, float] = (
            {}
        )  # room_id -> timestamp when room became empty

        # ID Mapping broadcast debouncing
        self.room_id_mapping_dirty: dict[str, bool] = (
            {}
        )  # room_id -> needs ID mapping broadcast
        self.room_last_id_mapping_broadcast: dict[str, float] = (
            {}
        )  # room_id -> last broadcast timestamp

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

        # NV flush cadence configuration (from config)
        self.nv_flush_interval = config.nv_flush_interval
        self.room_last_nv_flush: dict[str, float] = {}  # room_id -> last_flush_time

        # NV monitoring window (sliding window for logging only)
        self.nv_monitor_window: dict[str, list] = {}  # room_id -> [timestamps]
        self.nv_monitor_window_size = config.nv_monitor_window_size
        self.nv_monitor_threshold = config.nv_monitor_threshold

        # Network Variables limits (from config)
        self.MAX_GLOBAL_VARS = config.max_global_vars
        self.MAX_CLIENT_VARS = config.max_client_vars
        self.MAX_VAR_NAME_LENGTH = config.max_var_name_length
        self.MAX_VAR_VALUE_LENGTH = config.max_var_value_length

        # Thread synchronization
        self._rooms_lock = threading.RLock()  # Reentrant lock for nested access
        self._stats_lock = threading.Lock()  # Lock for statistics

        # Threading
        self.running = False
        self.receive_thread: threading.Thread | None = None
        self.periodic_thread: threading.Thread | None = (
            None  # Combined broadcast + cleanup
        )

        # Performance configuration (now using constants)
        self.idle_broadcast_interval = self.IDLE_BROADCAST_INTERVAL
        self.dirty_threshold = self.DIRTY_THRESHOLD

        # Statistics
        self.message_count = 0
        self.broadcast_count = 0
        self.skipped_broadcasts = 0

        # REST bridge lifecycle
        self._rest_thread: threading.Thread | None = None
        self._rest_server: Server | None = None

    def _increment_stat(self, stat_name: str, amount: int = 1) -> None:
        """Thread-safe increment of statistics"""
        with self._stats_lock:
            setattr(self, stat_name, getattr(self, stat_name) + amount)

    def _bump_fd_soft_limit(self, target: int) -> None:
        """Best-effort bump of RLIMIT_NOFILE for macOS/Linux."""
        if resource is None:
            logger.info(
                "Skipping FD soft limit bump: 'resource' module not available on this platform."
            )
            return

        try:
            soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
            new_soft = min(max(soft, target), hard)
            if new_soft != soft:
                resource.setrlimit(resource.RLIMIT_NOFILE, (new_soft, hard))
            else:
                logger.info(
                    "FD limits already sufficient (soft=%d, hard=%d)", soft, hard
                )
        except Exception as exc:
            logger.warning("Failed to raise RLIMIT_NOFILE: %s", exc)

    def _get_fd_snapshot(self) -> tuple[int | None, int | None, int | None]:
        """
        Best-effort snapshot of:
          - open_fds: current process open FD count
          - soft/hard: RLIMIT_NOFILE
        """
        soft: int | None = None
        hard: int | None = None
        if resource is not None:
            try:
                soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
            except Exception:
                pass

        open_fds: int | None = None
        # macOS: /dev/fd is available. Linux often has it too.
        try:
            open_fds = len(os.listdir("/dev/fd"))
        except Exception:
            # fallback (Linux /proc)
            try:
                open_fds = len(os.listdir(f"/proc/{os.getpid()}/fd"))
            except Exception:
                pass

        return open_fds, soft, hard

    def _pub_monitor_loop(self) -> None:
        """Monitor PUB socket for SUB connection/disconnection events."""
        from zmq.utils.monitor import recv_monitor_message

        try:
            while self._publisher_running:
                pub_monitor = self._pub_monitor
                if pub_monitor is None:
                    break
                try:
                    # Poll with timeout to allow checking _publisher_running
                    if pub_monitor.poll(100):
                        msg = recv_monitor_message(pub_monitor)
                        event = msg["event"]
                        if event == zmq.EVENT_ACCEPTED:
                            with self._sub_connection_lock:
                                self._sub_connection_count += 1
                                count = self._sub_connection_count
                            logger.info(f"SUB connected (total: {count})")
                        elif event == zmq.EVENT_DISCONNECTED:
                            with self._sub_connection_lock:
                                self._sub_connection_count = max(
                                    0, self._sub_connection_count - 1
                                )
                                count = self._sub_connection_count
                            logger.info(f"SUB disconnected (total: {count})")
                except zmq.ZMQError as e:
                    logger.debug("PUB monitor ZMQError: %s", e)
                    break
        except Exception as e:
            logger.error(f"PUB monitor error: {e}")
        finally:
            pub_monitor = self._pub_monitor
            if pub_monitor is not None:
                try:
                    pub_monitor.close()
                except Exception:
                    pass
                self._pub_monitor = None

    def _get_sub_connection_count(self) -> int:
        """Get current number of connected SUB sockets."""
        with self._sub_connection_lock:
            return self._sub_connection_count

    def _control_backlog_exceeded(self) -> bool:
        """Return True when control queue backlog exceeds watermark."""
        return self._pub_queue_ctrl.qsize() > self.CTRL_BACKLOG_WATERMARK

    def _refill_transform_tokens(self) -> None:
        """Refill token bucket for transform rate limiting (thread-safe)."""
        now = time.monotonic()
        with self._transform_budget_lock:
            elapsed = now - self._transform_last_refill
            if elapsed <= 0.0:
                return
            refill = elapsed * self.TRANSFORM_BUDGET_BYTES_PER_SEC
            self._transform_tokens = min(
                float(self.TRANSFORM_BUDGET_BYTES_PER_SEC),
                self._transform_tokens + refill,
            )
            self._transform_last_refill = now

    def _try_send_transform(self) -> bool:
        """Send at most one coalesced transform message if budget allows."""
        pub = self.pub
        if pub is None:
            return False

        self._refill_transform_tokens()
        with self._coalesce_lock:
            if not self._coalesce_latest:
                return False
            topic_bytes, message_bytes = next(iter(self._coalesce_latest.items()))

        sub_count = max(1, self._get_sub_connection_count())
        cost = len(message_bytes) * sub_count

        with self._transform_budget_lock:
            if self._transform_tokens < cost:
                return False
            # Deduct tokens before sending (optimistic)
            self._transform_tokens -= cost

        try:
            pub.send_multipart([topic_bytes, message_bytes], flags=zmq.DONTWAIT)
            self._increment_stat("broadcast_count")
            with self._coalesce_lock:
                # Remove only if the message is still the latest.
                if self._coalesce_latest.get(topic_bytes) == message_bytes:
                    del self._coalesce_latest[topic_bytes]
            return True
        except zmq.Again:
            # Refund tokens on failure
            with self._transform_budget_lock:
                self._transform_tokens += cost
            return False
        except Exception as exc:
            logger.error(f"Publisher failed to send (transform): {exc}")
            # Refund tokens on failure
            with self._transform_budget_lock:
                self._transform_tokens += cost
            return False

    def _publisher_loop(self) -> None:
        """The only place that owns/uses self.pub and performs ZMQ sends."""
        try:
            # Create/bind PUB in this thread to avoid cross-thread use
            self.pub = self.context.socket(zmq.PUB)
            # Set LINGER=0 to prevent FD accumulation during rapid connect/disconnect
            self.pub.setsockopt(zmq.LINGER, 0)
            try:
                self.pub.setsockopt(zmq.BACKLOG, self.PUB_BACKLOG)
            except Exception:
                pass
            try:
                # Low SNDHWM (2) ensures we drop stale messages when clients are slow.
                # Transform data only needs the latest; queueing old frames wastes bandwidth.
                self.pub.setsockopt(zmq.SNDHWM, 2)
            except Exception:
                # Best effort; ignore if high-water mark option is unsupported
                pass
            self.pub.bind(f"tcp://*:{self.pub_port}")

            # Set up socket monitor for tracking SUB connections
            self._pub_monitor = self.pub.get_monitor_socket(
                zmq.EVENT_ACCEPTED | zmq.EVENT_DISCONNECTED
            )
            self._pub_monitor.setsockopt(zmq.LINGER, 0)
            self._pub_monitor_thread = threading.Thread(
                target=self._pub_monitor_loop, name="PubMonitor", daemon=True
            )
            self._pub_monitor_thread.start()

            self._pub_ready.set()

            while self._publisher_running:
                drained = 0
                while drained < self.CTRL_DRAIN_BATCH:
                    try:
                        item = self._pub_queue_ctrl.get_nowait()
                    except Empty:
                        break

                    # Sentinel for shutdown
                    if not item or item[0] is None:
                        self._publisher_running = False
                        break

                    topic_bytes, message_bytes = item

                    try:
                        self.pub.send_multipart(
                            [topic_bytes, message_bytes], flags=zmq.DONTWAIT
                        )
                        self._increment_stat("broadcast_count")
                    except zmq.Again:
                        # Socket buffer full; drop the message (PUB-SUB is unreliable by design)
                        self._increment_stat("control_drop_count")
                    except Exception as e:
                        logger.error(f"Publisher failed to send: {e}")

                    drained += 1

                if not self._publisher_running:
                    break

                if self._control_backlog_exceeded():
                    time.sleep(self.BACKLOG_SLEEP_SEC)
                    continue

                transform_sent = self._try_send_transform()
                if drained == 0 and not transform_sent:
                    time.sleep(self.BACKLOG_SLEEP_SEC)

        except Exception as e:
            # On bind or loop failure, publish the exception and wake starters
            self._publisher_exception = e
            self._pub_ready.set()
            logger.error(f"Publisher error during startup/run: {e}")
        finally:
            self._publisher_running = False
            if self._pub_monitor_thread:
                self._pub_monitor_thread.join(timeout=1.0)
                self._pub_monitor_thread = None
            if self.pub is not None:
                try:
                    self.pub.close()
                except Exception:
                    pass
                self.pub = None
            logger.info("Publisher loop ended")

    def _enqueue_pub(self, topic_bytes: bytes, message_bytes: bytes) -> None:
        """Thread-safe enqueue of a broadcast for reliable/low-rate messages (RPC/NV).
        Backpressure policy: drop the oldest item (ring buffer) to prefer newer updates.
        """
        try:
            self._pub_queue_ctrl.put_nowait((topic_bytes, message_bytes))
        except Full:
            # Ring-buffer behavior: remove oldest then enqueue latest
            try:
                _ = self._pub_queue_ctrl.get_nowait()
            except Empty:
                # Queue became empty between Full and get_nowait (rare race condition)
                pass
            else:
                # Count and log the drop of the oldest message
                self._increment_stat("skipped_broadcasts")
                logger.debug("PUB queue full: dropping oldest message")
            try:
                self._pub_queue_ctrl.put_nowait((topic_bytes, message_bytes))
            except Full:
                # If still full after removing one, count as dropped (another drop)
                self._increment_stat("skipped_broadcasts")
                logger.debug("PUB queue full: dropping new message")

    def _enqueue_router(
        self, identity: bytes, room_id: str, message_bytes: bytes
    ) -> None:
        """Thread-safe enqueue of a control message for unicast via ROUTER.

        Used for control-like messages (RPC, NV sync, ID mapping) that are
        more reliable than PUB/SUB, but still drop under backpressure and
        send failures (ring-buffer drop-on-full and non-blocking router drain).

        Args:
            identity: Client's ZeroMQ identity (from ROUTER socket)
            room_id: Room ID string
            message_bytes: Serialized message payload
        """
        room_bytes = room_id.encode("utf-8")
        try:
            self._router_queue_ctrl.put_nowait((identity, room_bytes, message_bytes))
        except Full:
            # Ring-buffer behavior: drop oldest to make room for newest
            try:
                _ = self._router_queue_ctrl.get_nowait()
            except Empty:
                # Queue became empty between Full and get_nowait (rare race condition)
                pass
            else:
                # Count and log the drop of the oldest message
                self._increment_stat("ctrl_unicast_dropped")
                logger.debug(
                    "Router control queue full: dropping oldest control message"
                )
            try:
                self._router_queue_ctrl.put_nowait(
                    (identity, room_bytes, message_bytes)
                )
            except Full:
                # If still full after removing one, count as dropped (another drop)
                self._increment_stat("ctrl_unicast_dropped")
                logger.debug("Router control queue full: dropping new message")

    def _send_ctrl_to_room_via_router(
        self,
        room_id: str,
        message_bytes: bytes,
        exclude_identity: bytes | None = None,
    ) -> None:
        """Send a control message to all clients in a room via ROUTER unicast.

        This method enqueues the message for each client in the room, providing
        more reliable delivery than PUB/SUB (though still subject to drops under
        backpressure).

        Args:
            room_id: Room ID to broadcast to
            message_bytes: Serialized message payload
            exclude_identity: Optional client identity to exclude (e.g., sender)
        """
        identities_to_send: list[bytes] = []
        with self._rooms_lock:
            if room_id not in self.rooms:
                return

            # Collect all client identities in the room while holding the lock
            for _device_id, client_data in self.rooms[room_id].items():
                identity = client_data.get("identity")
                if identity is None:
                    continue
                if exclude_identity is not None and identity == exclude_identity:
                    continue
                identities_to_send.append(identity)

        # Enqueue control messages outside of the rooms lock to reduce contention
        for identity in identities_to_send:
            self._enqueue_router(identity, room_id, message_bytes)

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

    def _initialize_room(self, room_id: str) -> None:
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

            # Initialize NV pending buffers
            self.pending_global_nv[room_id] = {}
            self.pending_client_nv[room_id] = {}
            self.room_last_nv_flush[room_id] = 0

            # Initialize monitoring window
            self.nv_monitor_window[room_id] = []

            logger.info(f"Created new room: {room_id}")

    def _get_device_id_from_identity(
        self, client_identity: bytes, room_id: str
    ) -> str | None:
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
                # Clear stale cache entry to prevent broadcasting old transform
                if client_no in self.client_transform_body_cache:
                    del self.client_transform_body_cache[client_no]
                return client_no

            if (
                current_time - self.device_id_last_seen[device_id]
                > self.DEVICE_ID_EXPIRY_TIME
            ):
                # Device ID has expired, can reuse
                del self.room_client_no_to_device_id[room_id][client_no]
                del self.room_device_id_to_client_no[room_id][device_id]
                del self.device_id_last_seen[device_id]
                # Clear stale cache entry to prevent broadcasting old transform
                if client_no in self.client_transform_body_cache:
                    del self.client_transform_body_cache[client_no]
                return client_no

        return -1  # No reusable client number found

    def _cleanup_expired_device_id_mappings(self, current_time: float) -> None:
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

    def start(self, ip_addresses: list[str] | None = None) -> None:
        """Start the server"""
        try:
            # Raise FD soft limit early to avoid connection drops when many clients connect
            self._bump_fd_soft_limit(self.DEFAULT_FD_LIMIT)

            # Setup ROUTER socket
            self.router = self.context.socket(zmq.ROUTER)
            # Set LINGER=0 to prevent FD accumulation during rapid connect/disconnect
            self.router.setsockopt(zmq.LINGER, 0)
            # Increase accept backlog to survive connection stampedes from simulators
            try:
                self.router.setsockopt(zmq.BACKLOG, self.ROUTER_BACKLOG)
            except Exception:
                # Best effort; fall back to default backlog if unsupported
                pass
            try:
                self.router.setsockopt(zmq.RCVHWM, 10000)
            except Exception:
                # Best effort; ignore if high-water mark option is unsupported
                pass
            self.router.bind(f"tcp://*:{self.dealer_port}")

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

            # Start server discovery if enabled
            if self.enable_server_discovery:
                self._start_server_discovery()

            try:
                from .rest_bridge import create_app, run_uvicorn_in_thread

                app = create_app(
                    server_addr="tcp://127.0.0.1",
                    dealer_port=self.dealer_port,
                    sub_port=self.pub_port,
                )
                rest_port = int(os.getenv("NETSYNC_REST_PORT", "8800"))
                self._rest_thread, self._rest_server = run_uvicorn_in_thread(
                    app, host="0.0.0.0", port=rest_port
                )
                display_ip = ip_addresses[0] if ip_addresses else "0.0.0.0"
                logger.info(f"REST bridge started on http://{display_ip}:{rest_port}")
                # Display logo after all initialization is complete
                display_logo()
            except Exception as rest_exc:
                logger.error(f"Failed to start REST bridge: {rest_exc}")

            logger.info("Server is ready and waiting for connections...")

        except zmq.error.ZMQError as e:
            if "Address already in use" in str(e) or "Address in use" in str(e):
                logger.error(
                    f"Error: Another server instance is already running on port {self.dealer_port}"
                )
                logger.error(
                    "Please stop the existing server before starting a new one."
                )

                # Provide platform-specific instructions
                if platform.system() == "Windows":
                    logger.error(
                        f"You can find the process using: netstat -ano | findstr :{self.dealer_port}"
                    )
                    logger.error("And stop it using: taskkill /PID <PID> /F")
                else:
                    logger.error(
                        f"You can find the process using: lsof -i :{self.dealer_port}"
                    )
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

    def stop(self) -> None:
        """Stop the server"""
        logger.info("Stopping server...")
        self.running = False

        # Stop server discovery
        if self.server_discovery_running:
            self._stop_server_discovery()

        if self._rest_server is not None:
            try:
                self._rest_server.should_exit = True
            except Exception as rest_exc:  # pragma: no cover - defensive
                logger.debug("Failed to signal REST bridge shutdown: %s", rest_exc)
        if self._rest_thread and self._rest_thread.is_alive():
            self._rest_thread.join(timeout=2.0)
        self._rest_thread = None
        self._rest_server = None

        if self.receive_thread:
            self.receive_thread.join()
            logger.info("Receive thread stopped")
        if self.periodic_thread:
            self.periodic_thread.join()
            logger.info("Periodic thread stopped")

        # Stop Publisher thread
        self._publisher_running = False
        try:
            self._pub_queue_ctrl.put_nowait((None, None))  # sentinel
        except Full:
            # Best-effort to make room, then send sentinel
            try:
                _ = self._pub_queue_ctrl.get_nowait()
            except Empty:
                pass
            try:
                self._pub_queue_ctrl.put_nowait((None, None))
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
            f"Broadcasts sent: {self.broadcast_count}, Skipped broadcasts: {self.skipped_broadcasts}, "
            f"Control unicasts sent: {self.ctrl_unicast_sent}, "
            f"Control unicast wouldblock: {self.ctrl_unicast_wouldblock}, "
            f"Control unicast dropped: {self.ctrl_unicast_dropped}"
        )

    def _receive_loop(self) -> None:
        """Receive messages from clients"""
        while self.running:
            try:
                # Check for incoming messages
                if self.router is not None and self.router.poll(
                    self.POLL_TIMEOUT, zmq.POLLIN
                ):
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
                        # Protocol v3 binary-only handling (no JSON fallback)
                        try:
                            msg_type, data, raw_payload = binary_serializer.deserialize(
                                message_bytes
                            )
                            if data is None:
                                logger.warning("Received message with None data")
                                continue
                            if msg_type == binary_serializer.MSG_CLIENT_POSE:
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
                        except Exception as e:
                            logger.warning(
                                "Failed to decode protocol v3 message from room %s: %s",
                                room_id,
                                e,
                            )

                    else:
                        logger.warning(
                            f"Received incomplete message with only {len(parts)} parts"
                        )

                # Drain control messages via ROUTER after processing incoming
                self._drain_router_ctrl_queue()

            except Exception as e:
                logger.error(f"Error in receive loop: {e}")
                logger.error(traceback.format_exc())

        logger.info("Receive loop ended")

    def _drain_router_ctrl_queue(self) -> None:
        """Drain pending control messages from the router queue.

        This method sends queued control messages (RPC, NV sync, ID mapping)
        via the ROUTER socket to specific clients. It is called from the
        receive loop to ensure timely delivery of control messages.
        """
        router = self.router
        if router is None:
            return

        for _ in range(self.ROUTER_CTRL_DRAIN_BATCH):
            try:
                ident, room_bytes, msg_bytes = self._router_queue_ctrl.get_nowait()
            except Empty:
                break

            try:
                # Send via ROUTER: [identity, room_id, payload]
                router.send_multipart(
                    [ident, room_bytes, msg_bytes], flags=zmq.DONTWAIT
                )
                self._increment_stat("ctrl_unicast_sent")
            except zmq.Again:
                # Socket would block - drop the message (don't re-enqueue)
                self._increment_stat("ctrl_unicast_wouldblock")
            except Exception as exc:
                logger.error(f"Router send error: {exc}")

    def _process_message(
        self, client_identity: bytes, room_id: str, message: str
    ) -> None:
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
    ) -> None:
        """Handle client transform update"""
        device_id_raw = data.get("deviceId")  # Receiving device ID from client
        if device_id_raw is None or not isinstance(device_id_raw, str):
            logger.warning("Received client transform with missing or invalid deviceId")
            return
        device_id: str = device_id_raw
        body_bytes = self._extract_transform_body(raw_payload)

        # Detect stealth mode using flags
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
                if body_bytes:
                    self.client_transform_body_cache[client_no] = body_bytes
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

                if body_bytes:
                    self.client_transform_body_cache[client_no] = body_bytes

                # Mark room as dirty since transform data has arrived
                self.room_dirty_flags[room_id] = True

            # Mark room for debounced ID mapping broadcast when a new client joins
            if is_new_client:
                self.room_id_mapping_dirty[room_id] = True

        # Sync network variables to new client (outside lock to avoid deadlocks)
        if is_new_client:
            self._sync_network_variables_to_new_client(room_id)

    def _extract_transform_body(self, raw_payload: bytes) -> bytes:
        """Extract the transform body without device ID from raw payload."""
        return raw_payload or b""

    def _send_rpc_to_room(self, room_id: str, rpc_data: dict[str, Any]) -> None:
        """Send RPC to target clients in room via ROUTER unicast.

        If targetClientNos is empty, the RPC is broadcast to all clients in room.
        """
        sender_client_no = rpc_data.get("senderClientNo", 0)
        function_name = rpc_data.get("functionName", "unknown")
        args = rpc_data.get("args", [])
        target_client_nos = rpc_data.get("targetClientNos", [])
        logger.info(
            f"RPC: sender={sender_client_no}, targets={target_client_nos}, function={function_name}, args={args}, room={room_id}"
        )

        message_bytes = binary_serializer.serialize_rpc_message(rpc_data)

        if not target_client_nos:
            self._send_ctrl_to_room_via_router(room_id, message_bytes)
            return

        target_set = set(target_client_nos)
        identities_to_send: list[bytes] = []
        with self._rooms_lock:
            if room_id not in self.rooms:
                return
            for _device_id, client_data in self.rooms[room_id].items():
                client_no = client_data.get("client_no")
                if client_no not in target_set:
                    continue
                identity = client_data.get("identity")
                if identity is None:
                    continue
                identities_to_send.append(identity)

        for identity in identities_to_send:
            self._enqueue_router(identity, room_id, message_bytes)

    def _monitor_nv_sliding_window(self, room_id: str) -> None:
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

    def _buffer_global_var_set(self, room_id: str, data: dict[str, Any]) -> None:
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

    def _handle_global_var_set(self, room_id: str, data: dict[str, Any]) -> None:
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

    def _buffer_client_var_set(self, room_id: str, data: dict[str, Any]) -> None:
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

    def _handle_client_var_set(self, room_id: str, data: dict[str, Any]) -> None:
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

    def _broadcast_global_var_sync(self, room_id: str) -> None:
        """Broadcast global variables sync to all clients in room via ROUTER unicast.

        Network Variable syncs are sent via ROUTER for reliable delivery to each client,
        rather than via PUB which can drop messages under load.
        """
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
                message_bytes = binary_serializer.serialize_global_var_sync(
                    {"variables": variables}
                )
                # Send via ROUTER unicast (lock is held, but _send_ctrl_to_room_via_router
                # will acquire the lock again - RLock allows this)
                self._send_ctrl_to_room_via_router(room_id, message_bytes)
                logger.debug(
                    f"Broadcasted {len(variables)} global variables to room {room_id}"
                )

    def _broadcast_client_var_sync(self, room_id: str) -> None:
        """Broadcast client variables sync to all clients in room via ROUTER unicast.

        Network Variable syncs are sent via ROUTER for reliable delivery to each client,
        rather than via PUB which can drop messages under load.
        """
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
                message_bytes = binary_serializer.serialize_client_var_sync(
                    {"clientVariables": client_variables}
                )
                # Send via ROUTER unicast (lock is held, but _send_ctrl_to_room_via_router
                # will acquire the lock again - RLock allows this)
                self._send_ctrl_to_room_via_router(room_id, message_bytes)
                logger.debug(
                    f"Broadcasted client variables for {len(client_variables)} clients to room {room_id}"
                )

    def _sync_network_variables_to_new_client(self, room_id: str) -> None:
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

        # Send NV syncs via ROUTER unicast for reliable delivery
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
            self._send_ctrl_to_room_via_router(room_id, msg)

        if applied_clients:
            client_vars = {str(cno): lst for cno, lst in applied_clients.items()}
            msg = binary_serializer.serialize_client_var_sync(
                {"clientVariables": client_vars}
            )
            self._send_ctrl_to_room_via_router(room_id, msg)

        elapsed_ms = (time.perf_counter() - start) * 1000.0
        if elapsed_ms > 10.0:
            logger.info(f"NV flush took {elapsed_ms:.2f} ms (room={room_id})")
        else:
            logger.debug(f"NV flush took {elapsed_ms:.2f} ms (room={room_id})")

    def _broadcast_id_mappings(self, room_id: str) -> None:
        """Broadcast all device ID mappings for a room via ROUTER unicast.

        ID mapping messages are sent via ROUTER for reliable delivery to each client,
        rather than via PUB which can drop messages under load.
        """
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
                # Serialize and broadcast the mappings with server version
                server_version = binary_serializer.parse_version(get_version())
                message_bytes = binary_serializer.serialize_device_id_mapping(
                    mappings, server_version
                )
                # Send via ROUTER unicast (lock is held, but _send_ctrl_to_room_via_router
                # will acquire the lock again - RLock allows this)
                self._send_ctrl_to_room_via_router(room_id, message_bytes)
                logger.info(
                    f"Broadcasted {len(mappings)} ID mappings to room {room_id} via ROUTER"
                )

    def _flush_debounced_id_mapping_broadcasts(self, current_time: float) -> None:
        """Flush ID mapping broadcasts that have been debounced long enough."""
        # Get list of rooms that need ID mapping broadcast and mark them as processed
        rooms_to_broadcast: list[str] = []
        with self._rooms_lock:
            for room_id, is_dirty in list(self.room_id_mapping_dirty.items()):
                if not is_dirty:
                    continue
                last_broadcast = self.room_last_id_mapping_broadcast.get(room_id, 0.0)
                if current_time - last_broadcast >= self.ID_MAPPING_DEBOUNCE_INTERVAL:
                    rooms_to_broadcast.append(room_id)

            # Mark rooms as having been broadcast at this time
            for room_id in rooms_to_broadcast:
                self.room_id_mapping_dirty[room_id] = False
                self.room_last_id_mapping_broadcast[room_id] = current_time

        # Broadcast ID mappings for eligible rooms outside the lock
        for room_id in rooms_to_broadcast:
            self._broadcast_id_mappings(room_id)

    def _periodic_loop(self) -> None:
        """Combined broadcast and cleanup loop with adaptive rates"""
        last_broadcast_check: float = 0.0
        last_cleanup: float = 0.0
        last_device_id_cleanup: float = 0.0
        last_log = time.monotonic()
        DEVICE_ID_CLEANUP_INTERVAL = 60.0  # Clean up expired device IDs every minute

        while self.running:
            try:
                current_time = time.monotonic()

                # Check for broadcasts at higher frequency but only broadcast when needed
                if current_time - last_broadcast_check >= self.BROADCAST_CHECK_INTERVAL:
                    # Flush Network Variables before other categories
                    with self._rooms_lock:
                        rooms = list(self.rooms.keys())
                    for room_id in rooms:
                        last_flush = self.room_last_nv_flush.get(room_id, 0.0)
                        if current_time - last_flush >= self.nv_flush_interval:
                            self._flush_nv_drain(room_id)
                            self.room_last_nv_flush[room_id] = current_time

                    if not self._control_backlog_exceeded():
                        self._adaptive_broadcast_all_rooms(current_time)
                    last_broadcast_check = current_time

                # Cleanup at regular intervals
                if current_time - last_cleanup >= self.CLEANUP_INTERVAL:
                    self._cleanup_clients(current_time)
                    last_cleanup = current_time

                # Debounced ID mapping broadcasts
                self._flush_debounced_id_mapping_broadcasts(current_time)

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

                    # Get FD information for status log
                    open_fds, soft, _ = self._get_fd_snapshot()
                    fd_part = ""
                    if open_fds is not None:
                        if soft is not None:
                            fd_part = f", FD {open_fds}/{soft}"
                        else:
                            fd_part = f", FD {open_fds}"

                    logger.info(
                        f"Status: {len(self.rooms)} rooms, {normal_clients} normal clients, "
                        f"{stealth_clients} stealth clients, "
                        f"{dirty_rooms} dirty rooms, {total_device_ids} tracked device IDs"
                        f"{fd_part}"
                    )
                    last_log = current_time

                time.sleep(self.MAIN_LOOP_SLEEP)  # 50Hz loop for better responsiveness

            except Exception as e:
                logger.error(f"Error in periodic loop: {e}")

        logger.info("Periodic loop ended")

    def _adaptive_broadcast_all_rooms(self, current_time: float) -> None:
        """Broadcast room state with adaptive rates based on activity"""
        rooms_to_broadcast: list[
            tuple[str, list[tuple[int, float, dict[str, Any] | None, bytes]]]
        ] = []

        with self._rooms_lock:
            for room_id, clients in self.rooms.items():
                if not clients:  # Skip empty rooms
                    continue

                # Check if room needs broadcasting
                last_broadcast = self.room_last_broadcast.get(room_id, 0.0)
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
                    client_snapshot = []
                    for client_data in clients.values():
                        if client_data.get("is_stealth", False):
                            continue
                        client_no = client_data.get("client_no", 0)
                        transform_data = client_data.get("transform_data")
                        pose_time = client_data.get("last_update", 0.0)
                        body_bytes = self.client_transform_body_cache.get(
                            client_no, b""
                        )
                        client_snapshot.append(
                            (client_no, pose_time, transform_data, body_bytes)
                        )
                    rooms_to_broadcast.append((room_id, client_snapshot))
                    self.room_dirty_flags[room_id] = False  # Clear dirty flag
                    self.room_last_broadcast[room_id] = current_time
                else:
                    self._increment_stat("skipped_broadcasts")

        for room_id, client_snapshot in rooms_to_broadcast:
            self._broadcast_room(room_id, client_snapshot)

    def _enqueue_pub_latest(self, topic_bytes: bytes, message_bytes: bytes) -> None:
        """Latest-only coalescing enqueue for high-rate topics (e.g., room transforms)."""
        with self._coalesce_lock:
            # Prevent unbounded growth: drop oldest entry if buffer exceeds limit
            if (
                topic_bytes not in self._coalesce_latest
                and len(self._coalesce_latest) >= self.MAX_COALESCE_BUFFER_SIZE
            ):
                oldest_key = next(iter(self._coalesce_latest))
                del self._coalesce_latest[oldest_key]
                self._increment_stat("skipped_broadcasts")
            self._coalesce_latest[topic_bytes] = message_bytes

    def _broadcast_room(
        self,
        room_id: str,
        client_snapshot: list[tuple[int, float, dict[str, Any] | None, bytes]],
    ) -> None:
        """Broadcast a specific room's state from a snapshot."""
        message_bytes = self._serialize_room_transform(room_id, client_snapshot)
        if not message_bytes:
            return

        topic_bytes = room_id.encode("utf-8")
        self._enqueue_pub_latest(topic_bytes, message_bytes)

    def _serialize_room_transform(
        self,
        room_id: str,
        client_snapshot: list[tuple[int, float, dict[str, Any] | None, bytes]],
    ) -> bytes | None:
        if not client_snapshot:
            return None

        buffer = bytearray()
        buffer.append(binary_serializer.MSG_ROOM_POSE)
        buffer.append(binary_serializer.PROTOCOL_VERSION)
        binary_serializer._pack_string(buffer, room_id)
        buffer.extend(struct.pack("<d", time.monotonic()))
        count_offset = len(buffer)
        buffer.extend(b"\x00\x00")

        count = 0
        for client_no, pose_time, transform_data, body_bytes in client_snapshot:
            if body_bytes:
                buffer.extend(struct.pack("<H", client_no))
                buffer.extend(struct.pack("<d", pose_time))
                buffer.extend(body_bytes)
                count += 1
                continue

            if transform_data:
                transform_data["poseTime"] = pose_time
                binary_serializer._serialize_client_data_short(buffer, transform_data)
                count += 1

        if count == 0:
            return None

        struct.pack_into("<H", buffer, count_offset, count)
        return bytes(buffer)

    def _cleanup_clients(self, current_time: float) -> None:
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
                        if client_no and client_no in self.client_transform_body_cache:
                            del self.client_transform_body_cache[client_no]
                        # Note: We don't remove device ID->clientNo mapping here
                        # It will be cleaned up after DEVICE_ID_EXPIRY_TIME
                        logger.info(
                            f"Client {device_id[:8]}... (client number: {client_no}) removed (timeout)"
                        )

                    # Mark room as dirty since clients were removed
                    self.room_dirty_flags[room_id] = True

                    # Mark room for debounced ID mapping broadcast
                    self.room_id_mapping_dirty[room_id] = True

                # Handle empty room tracking with delayed removal
                if not clients:
                    # Room is empty - track when it became empty
                    if room_id not in self.room_empty_since:
                        # Just became empty - start tracking
                        self.room_empty_since[room_id] = current_time
                        logger.info(
                            f"Room {room_id} became empty, "
                            f"will expire in {self.EMPTY_ROOM_EXPIRY_TIME}s"
                        )
                    elif (
                        current_time - self.room_empty_since[room_id]
                        > self.EMPTY_ROOM_EXPIRY_TIME
                    ):
                        # Been empty long enough - mark for removal
                        rooms_to_remove.append(room_id)
                else:
                    # Room has clients - clear empty tracking if exists
                    if room_id in self.room_empty_since:
                        del self.room_empty_since[room_id]
                        logger.info(f"Room {room_id} is no longer empty")

            # Remove rooms that have been empty for longer than EMPTY_ROOM_EXPIRY_TIME
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

                    # Clean up ID mapping debounce tracking
                    if room_id in self.room_id_mapping_dirty:
                        del self.room_id_mapping_dirty[room_id]
                    if room_id in self.room_last_id_mapping_broadcast:
                        del self.room_last_id_mapping_broadcast[room_id]

                    # Clean up empty room tracking
                    if room_id in self.room_empty_since:
                        del self.room_empty_since[room_id]

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
                    if room_id in self.nv_monitor_window:
                        del self.nv_monitor_window[room_id]

                    logger.info(f"Removed empty room: {room_id}")

                except Exception as e:
                    logger.error(f"Error during room cleanup for {room_id}: {e}")
                    # Continue with other rooms even if one fails

    def _start_server_discovery(self) -> None:
        """Start server discovery service to respond to client requests"""
        # Start UDP server discovery
        try:
            self.server_discovery_socket = socket.socket(
                socket.AF_INET, socket.SOCK_DGRAM
            )
            self.server_discovery_socket.setsockopt(
                socket.SOL_SOCKET, socket.SO_REUSEADDR, 1
            )
            self.server_discovery_socket.bind(("", self.server_discovery_port))
            self.server_discovery_socket.settimeout(
                1.0
            )  # Timeout for graceful shutdown

            self.server_discovery_running = True
            self.server_discovery_thread = threading.Thread(
                target=self._server_discovery_loop,
                name="ServerDiscoveryThread",
                daemon=True,
            )
            self.server_discovery_thread.start()

        except Exception as e:
            logger.error(f"Failed to start UDP discovery service: {e}")
            self.server_discovery_running = False

        # Start TCP server discovery
        self._start_tcp_server_discovery()

    def _stop_server_discovery(self) -> None:
        """Stop server discovery service"""
        # Stop UDP server discovery
        self.server_discovery_running = False

        udp_socket = self.server_discovery_socket
        if udp_socket is not None:
            udp_socket.close()
            self.server_discovery_socket = None

        udp_thread = self.server_discovery_thread
        if udp_thread is not None:
            udp_thread.join(timeout=2)
            self.server_discovery_thread = None

        # Stop TCP server discovery
        self._stop_tcp_server_discovery()

        logger.info("Stopped discovery service")

    def _server_discovery_loop(self) -> None:
        """UDP discovery service loop - responds to client discovery requests"""
        # Response message format: STYLY-NETSYNC|dealerPort|pubPort|serverName
        response = (
            f"STYLY-NETSYNC|{self.dealer_port}|{self.pub_port}|{self.server_name}"
        )
        response_bytes = response.encode("utf-8")
        udp_socket = self.server_discovery_socket
        if udp_socket is None:
            return

        while self.server_discovery_running:
            try:
                # Wait for client discovery request
                data, client_addr = udp_socket.recvfrom(1024)
                request = data.decode("utf-8")

                # Validate request format
                if request == "STYLY-NETSYNC-DISCOVER":
                    # Send response back to requesting client
                    udp_socket.sendto(response_bytes, client_addr)
                    logger.debug(
                        f"Responded to UDP discovery request from {client_addr}"
                    )

            except TimeoutError:
                # Timeout is expected for graceful shutdown
                continue
            except Exception as e:
                if (
                    self.server_discovery_running
                ):  # Only log if we're still supposed to be running
                    logger.error(f"UDP discovery service error: {e}")

    def _start_tcp_server_discovery(self) -> None:
        """Start TCP-based server discovery service"""
        try:
            self.tcp_server_discovery_socket = socket.socket(
                socket.AF_INET, socket.SOCK_STREAM
            )
            self.tcp_server_discovery_socket.setsockopt(
                socket.SOL_SOCKET, socket.SO_REUSEADDR, 1
            )
            self.tcp_server_discovery_socket.bind(("", self.server_discovery_port))
            self.tcp_server_discovery_socket.listen(5)
            self.tcp_server_discovery_socket.settimeout(
                1.0
            )  # Timeout for graceful shutdown

            self.tcp_server_discovery_running = True
            self.tcp_server_discovery_thread = threading.Thread(
                target=self._tcp_server_discovery_loop,
                name="TcpServerDiscoveryThread",
                daemon=True,
            )
            self.tcp_server_discovery_thread.start()

        except Exception as e:
            logger.error(f"Failed to start TCP discovery service: {e}")
            self.tcp_server_discovery_running = False

    def _stop_tcp_server_discovery(self) -> None:
        """Stop TCP-based server discovery service"""
        self.tcp_server_discovery_running = False

        tcp_socket = self.tcp_server_discovery_socket
        if tcp_socket is not None:
            tcp_socket.close()
            self.tcp_server_discovery_socket = None

        tcp_thread = self.tcp_server_discovery_thread
        if tcp_thread is not None:
            tcp_thread.join(timeout=2)
            self.tcp_server_discovery_thread = None

    def _tcp_server_discovery_loop(self) -> None:
        """TCP discovery service loop - responds to client discovery requests"""
        # Response message format: STYLY-NETSYNC|dealerPort|pubPort|serverName\n
        response = (
            f"STYLY-NETSYNC|{self.dealer_port}|{self.pub_port}|{self.server_name}\n"
        )
        response_bytes = response.encode("utf-8")
        tcp_socket = self.tcp_server_discovery_socket
        if tcp_socket is None:
            return

        while self.tcp_server_discovery_running:
            try:
                # Accept incoming connection
                client_socket, client_addr = tcp_socket.accept()
                client_socket.settimeout(2.0)  # Timeout for client operations

                try:
                    # Receive discovery request
                    data = client_socket.recv(1024)
                    request = data.decode("utf-8").strip()

                    # Validate request format
                    if request == "STYLY-NETSYNC-DISCOVER":
                        # Send response back to requesting client
                        client_socket.sendall(response_bytes)
                        logger.debug(
                            f"Responded to TCP discovery request from {client_addr}"
                        )

                except Exception as e:
                    logger.debug(f"Error handling TCP client {client_addr}: {e}")
                finally:
                    client_socket.close()

            except TimeoutError:
                # Timeout is expected for graceful shutdown
                continue
            except Exception as e:
                if (
                    self.tcp_server_discovery_running
                ):  # Only log if we're still supposed to be running
                    logger.error(f"TCP discovery service error: {e}")


def display_logo() -> None:
    logo = """
CgobWzM4OzU7MjE2bSDilojilojilojilojilojilojilojilZcg4paI4paIG1szODs1OzIxMG3ilojilojilojilojilojilojilZcg4paI4paI4pWXICAg4paI4paI4pWXIOKWiOKWiOKVlyAgICAbWzM4OzU7MjE2bSAg4paI4paI4pWXICAg4paI4paI4pWXG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWU4pWQ4pWQ4pWQ4pWQ4pWdIOKVmuKVkBtbMzg7NTsyMTBt4pWQ4paI4paI4pWU4pWQ4pWQ4pWdIOKVmuKWiOKWiOKVlyDilojilojilZTilZ0g4paI4paI4pWRICAgIBtbMzg7NTsyMTZtICDilZrilojilojilZcg4paI4paI4pWU4pWdG1szOW0KG1szODs1OzIxNm0g4paI4paI4paI4paI4paI4paI4paI4pWXICAgG1szODs1OzIxMG0g4paI4paI4pWRICAgICDilZrilojilojilojilojilZTilZ0gIOKWiOKWiOKVkSAgICAbWzM4OzU7MjE2bSAgIOKVmuKWiOKWiOKWiOKWiOKVlOKVnSAbWzM5bQobWzM4OzU7MjE2bSDilZrilZDilZDilZDilZDilojilojilZEgICAbWzM4OzU7MjEwbSDilojilojilZEgICAgICDilZrilojilojilZTilZ0gICDilojilojilZEgICAgG1szODs1OzIxNm0gICAg4pWa4paI4paI4pWU4pWdICAbWzM5bQobWzM4OzU7MjE2bSDilojilojilojilojilojilojilojilZEgICAbWzM4OzU7MjEwbSDilojilojilZEgICAgICAg4paI4paI4pWRICAgIOKWiOKWiOKWiOKWiOKWiOKWiOKWiBtbMzg7NTsyMTZt4pWXICAgIOKWiOKWiOKVkSAgIBtbMzltChtbMzg7NTsyMTZtIOKVmuKVkOKVkOKVkOKVkOKVkOKVkOKVnSAgIBtbMzg7NTsyMTBtIOKVmuKVkOKVnSAgICAgICDilZrilZDilZ0gICAg4pWa4pWQ4pWQ4pWQ4pWQ4pWQ4pWQG1szODs1OzIxNm3ilZ0gICAg4pWa4pWQ4pWdICAgG1szOW0KChtbMzg7NTsyMTZtIOKWiOKWiOKWiOKVlyAgIOKWiOKWiOKVlyDilojilojilojilojilojilogbWzM4OzU7MjEwbeKWiOKVlyDilojilojilojilojilojilojilojilojilZcg4paI4paI4paI4paI4paI4paI4paI4pWXIOKWiOKWiOKVlyAgIOKWiOKWiOKVlyDilojilojilojilZcbWzM4OzU7MjE2bSAgIOKWiOKWiOKVlyAg4paI4paI4paI4paI4paI4paI4pWXG1szOW0KG1szODs1OzIxNm0g4paI4paI4paI4paI4pWXICDilojilojilZEg4paI4paI4pWU4pWQ4pWQ4pWQG1szODs1OzIxMG3ilZDilZ0g4pWa4pWQ4pWQ4paI4paI4pWU4pWQ4pWQ4pWdIOKWiOKWiOKVlOKVkOKVkOKVkOKVkOKVnSDilZrilojilojilZcg4paI4paI4pWU4pWdIOKWiOKWiOKWiOKWiBtbMzg7NTsyMTZt4pWXICDilojilojilZEg4paI4paI4pWU4pWQ4pWQ4pWQ4pWQ4pWdG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWU4paI4paI4pWXIOKWiOKWiOKVkSDilojilojilojilojilojilZcbWzM4OzU7MjEwbSAgICAgIOKWiOKWiOKVkSAgICDilojilojilojilojilojilojilojilZcgIOKVmuKWiOKWiOKWiOKWiOKVlOKVnSAg4paI4paI4pWU4paIG1szODs1OzIxNm3ilojilZcg4paI4paI4pWRIOKWiOKWiOKVkSAgICAgG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWR4pWa4paI4paI4pWX4paI4paI4pWRIOKWiOKWiOKVlOKVkOKVkOKVnRtbMzg7NTsyMTBtICAgICAg4paI4paI4pWRICAgIOKVmuKVkOKVkOKVkOKVkOKWiOKWiOKVkSAgIOKVmuKWiOKWiOKVlOKVnSAgIOKWiOKWiOKVkeKVmhtbMzg7NTsyMTZt4paI4paI4pWX4paI4paI4pWRIOKWiOKWiOKVkSAgICAgG1szOW0KG1szODs1OzIxNm0g4paI4paI4pWRIOKVmuKWiOKWiOKWiOKWiOKVkSDilojilojilojilojilojilogbWzM4OzU7MjEwbeKWiOKVlyAgICDilojilojilZEgICAg4paI4paI4paI4paI4paI4paI4paI4pWRICAgIOKWiOKWiOKVkSAgICDilojilojilZEgG1szODs1OzIxNm3ilZrilojilojilojilojilZEg4pWa4paI4paI4paI4paI4paI4paI4pWXG1szOW0KG1szODs1OzIxNm0g4pWa4pWQ4pWdICDilZrilZDilZDilZDilZ0g4pWa4pWQ4pWQ4pWQ4pWQ4pWQG1szODs1OzIxMG3ilZDilZ0gICAg4pWa4pWQ4pWdICAgIOKVmuKVkOKVkOKVkOKVkOKVkOKVkOKVnSAgICDilZrilZDilZ0gICAg4pWa4pWQ4pWdIBtbMzg7NTsyMTZtIOKVmuKVkOKVkOKVkOKVnSAg4pWa4pWQ4pWQ4pWQ4pWQ4pWQ4pWdG1szOW0KCgo=
""".strip()
    sys.stdout.buffer.write(base64.b64decode(logo))
    sys.stdout.flush()


def main() -> None:
    parser = argparse.ArgumentParser(description="STYLY NetSync Server")
    parser.add_argument(
        "--config",
        type=Path,
        metavar="FILE",
        help="Path to user TOML configuration file (overrides defaults)",
    )
    parser.add_argument(
        "--no-server-discovery", action="store_true", help="Disable server discovery"
    )
    parser.add_argument(
        "--server-discovery-port",
        type=valid_port,
        metavar="PORT",
        help="UDP port used for server discovery (see default.toml for default)",
    )
    parser.add_argument(
        "-V",
        "--version",
        action="version",
        version=f"%(prog)s {get_version()}",
        help="Show version and exit",
    )
    parser.add_argument(
        "--log-dir",
        type=Path,
        help="Directory for log files (enables file logging with rotation)",
    )
    parser.add_argument(
        "--log-rotation",
        type=str,
        help=(
            "Rotation rule for log files (loguru syntax, e.g., '10 MB', '1 day', "
            "'12:00'); default triggers at 10MB or 7 days"
        ),
    )
    parser.add_argument(
        "--log-retention",
        type=str,
        help=(
            "Retention rule for log files (loguru syntax, e.g., '5', '1 week', "
            "'keep 10 files'); default keeps newest 20 files"
        ),
    )
    parser.add_argument(
        "--log-json-console",
        action="store_true",
        help="Emit console logs as JSON instead of text",
    )
    parser.add_argument(
        "--log-level-console",
        default=None,
        help="Console log level (default: INFO)",
    )

    args = parser.parse_args()

    # Load configuration from file and/or CLI args
    # Note: Logging is configured after this to use config values
    try:
        config, config_overrides = create_config_from_args(args)
    except DefaultConfigError as e:
        # Fatal error: default.toml cannot be loaded
        print(f"FATAL: {e}")
        sys.exit(1)
    except FileNotFoundError:
        # User config file not found
        print(f"ERROR: User configuration file not found: {args.config}")
        sys.exit(1)
    except ConfigurationError as e:
        # Validation errors - print each error clearly
        print("ERROR: Configuration validation failed:")
        for error in e.errors:
            print(f"  - {error}")
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: Failed to load configuration: {e}")
        sys.exit(1)

    # Configure logging with merged settings (config file + CLI overrides)
    log_dir = Path(config.log_dir) if config.log_dir else None
    configure_logging(
        log_dir=log_dir,
        console_level=config.log_level_console,
        console_json=config.log_json_console,
        rotation=config.log_rotation,
        retention=config.log_retention,
    )

    # Log config file info after logging is configured
    if args.config is not None:
        logger.info(f"Loaded user configuration from {args.config}")
        if config_overrides:
            logger.info("Configuration overrides from user config:")
            for override in config_overrides:
                logger.info(
                    f"  {override.key}: {override.default_value} -> {override.new_value}"
                )

    # Apply global configuration settings
    binary_serializer.set_max_virtual_transforms(config.max_virtual_transforms)

    logger.info("=" * 80)
    logger.info("STYLY NetSync Server Starting")
    logger.info("=" * 80)
    logger.info(f"  Version: {get_version()}")

    # Display local IP addresses
    ip_addresses = network_utils.get_local_ip_addresses()
    if ip_addresses:
        logger.info("  Server IP addresses:")
        for ip in ip_addresses:
            logger.info(f"    - {ip}")
    else:
        logger.info("  Server IP addresses: Unable to detect")

    logger.info(f"  DEALER port: {config.dealer_port}")
    logger.info(f"  PUB port: {config.pub_port}")
    if config.enable_server_discovery:
        logger.info(f"  Server discovery port: {config.server_discovery_port}")
        logger.info(f"  Server name: {config.server_name}")
    else:
        logger.info("  Discovery: Disabled")
    logger.info("=" * 80)

    server = NetSyncServer(
        dealer_port=config.dealer_port,
        pub_port=config.pub_port,
        enable_server_discovery=config.enable_server_discovery,
        server_discovery_port=config.server_discovery_port,
        server_name=config.server_name,
        config=config,
    )

    try:
        server.start(ip_addresses=ip_addresses)

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
