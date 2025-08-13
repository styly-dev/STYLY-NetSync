# server.py
import argparse
import json
import logging
import socket
import threading
import time
import traceback
from typing import Any, Dict

import zmq

# Handle both package and direct script execution
try:
    from . import binary_serializer
except ImportError:
    import binary_serializer

# Log configuration
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(levelname)s - %(message)s",
    datefmt="%H:%M:%S",
)
logger = logging.getLogger(__name__)


class NetSyncServer:
    # Performance and timing constants
    BASE_BROADCAST_INTERVAL = 0.1      # 10Hz base rate
    IDLE_BROADCAST_INTERVAL = 0.5       # 2Hz when idle
    DIRTY_THRESHOLD = 0.05              # 20Hz max rate when very active
    BROADCAST_CHECK_INTERVAL = 0.05     # Check broadcasts every 50ms
    CLEANUP_INTERVAL = 1.0              # Cleanup every 1 second
    STATUS_LOG_INTERVAL = 10.0          # Log status every 10 seconds
    MAIN_LOOP_SLEEP = 0.02              # 50Hz main loop sleep
    CLIENT_TIMEOUT = 1.0                # 1 second timeout for client disconnect
    DEVICE_ID_EXPIRY_TIME = 300.0       # 5 minutes - remove device ID mappings after this time
    POLL_TIMEOUT = 100                  # ZMQ poll timeout in ms

    def __init__(self, dealer_port=5555, pub_port=5556, enable_beacon=True, beacon_port=9999, server_name="STYLY-LBE-Server"):
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
        self.router = None  # ROUTER socket for receiving from clients
        self.pub = None  # PUB socket for broadcasting to clients

        # Thread-safe room management with locks
        self.rooms: Dict[str, Dict[str, Any]] = {}  # room_id -> {device_id: {client_data}}
        self.room_dirty_flags: Dict[str, bool] = {}  # Track which rooms have changed data
        self.room_last_broadcast: Dict[str, float] = {}  # Track last broadcast time per room
        self.client_binary_cache: Dict[str, bytes] = {}  # Cache client_no -> binary data

        # Client number management per room
        self.room_client_no_counters: Dict[str, int] = {}  # room_id -> next_client_no
        self.room_device_id_to_client_no: Dict[str, Dict[str, int]] = {}  # room_id -> {device_id: client_no}
        self.room_client_no_to_device_id: Dict[str, Dict[int, str]] = {}  # room_id -> {client_no: device_id}
        self.device_id_last_seen: Dict[str, float] = {}  # device_id -> last_seen_timestamp

        # Network Variables storage
        self.global_variables: Dict[str, Dict[str, Any]] = {}  # room_id -> {var_name: {value, timestamp, lastWriterClientNo}}
        self.client_variables: Dict[str, Dict[int, Dict[str, Any]]] = {}  # room_id -> {client_no -> {var_name: {value, timestamp, lastWriterClientNo}}}
        self.client_rate_limiter: Dict[str, Dict[str, float]] = {}  # room_id -> {device_id: last_request_times}

        # Network Variables limits
        self.MAX_GLOBAL_VARS = 20
        self.MAX_CLIENT_VARS = 20
        self.MAX_VAR_NAME_LENGTH = 64
        self.MAX_VAR_VALUE_LENGTH = 1024
        self.MAX_REQUESTS_PER_SECOND = 10

        # Thread synchronization
        self._rooms_lock = threading.RLock()  # Reentrant lock for nested access
        self._stats_lock = threading.Lock()   # Lock for statistics

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

    def _get_or_assign_client_no(self, room_id: str, device_id: str) -> int:
        """Get existing client number or assign a new one for the given device ID in the room"""
        with self._rooms_lock:
            # Initialize room structures if needed
            self._initialize_room(room_id)

            # Update last seen time
            self.device_id_last_seen[device_id] = time.time()

            # Check if device ID already has a client number in this room
            if device_id in self.room_device_id_to_client_no[room_id]:
                return self.room_device_id_to_client_no[room_id][device_id]

            # Assign new client number
            client_no = self.room_client_no_counters[room_id]
            if client_no > 65535:  # Max value for 2 bytes
                # Find and reuse expired client numbers
                client_no = self._find_reusable_client_no(room_id)
                if client_no == -1:
                    raise ValueError(f"Room {room_id} has exhausted all available client numbers")
            else:
                self.room_client_no_counters[room_id] += 1

            # Store mappings
            self.room_device_id_to_client_no[room_id][device_id] = client_no
            self.room_client_no_to_device_id[room_id][client_no] = device_id

            logger.info(f"Assigned client number {client_no} to device ID {device_id[:8]}... in room {room_id}")
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
            self.client_rate_limiter[room_id] = {}

            logger.info(f"Created new room: {room_id}")

    def _get_device_id_from_identity(self, client_identity: bytes, room_id: str) -> str:
        """Get device ID from client identity"""
        with self._rooms_lock:
            if room_id in self.rooms:
                for device_id, client_data in self.rooms[room_id].items():
                    if client_data.get('identity') == client_identity:
                        return device_id
        return None

    def _get_client_no_for_device_id(self, room_id: str, device_id: str) -> int:
        """Get client number for a given device ID in a room"""
        with self._rooms_lock:
            if room_id in self.room_device_id_to_client_no:
                return self.room_device_id_to_client_no[room_id].get(device_id, 0)
        return 0

    def _get_device_id_from_client_no(self, room_id: str, client_no: int) -> str:
        """Get device ID from client number in a room"""
        with self._rooms_lock:
            if room_id in self.room_client_no_to_device_id:
                return self.room_client_no_to_device_id[room_id].get(client_no, None)
        return None

    def _find_reusable_client_no(self, room_id: str) -> int:
        """Find a client number that can be reused (from expired device IDs)"""
        current_time = time.time()

        # Check all client numbers in the room
        for client_no, device_id in list(self.room_client_no_to_device_id[room_id].items()):
            if device_id not in self.device_id_last_seen:
                # Device ID has no last seen time, can reuse
                del self.room_client_no_to_device_id[room_id][client_no]
                del self.room_device_id_to_client_no[room_id][device_id]
                return client_no

            if current_time - self.device_id_last_seen[device_id] > self.DEVICE_ID_EXPIRY_TIME:
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
                        logger.info(f"Cleaned up expired device ID {device_id[:8]}... (client number: {client_no}) from room {room_id}")

            if expired_device_ids:
                logger.info(f"Cleaned up {len(expired_device_ids)} expired device ID mappings")

    def start(self):
        """Start the server"""
        logger.info(
            f"Starting server on ports {self.dealer_port} (ROUTER) and {self.pub_port} (PUB)"
        )

        try:
            # Setup sockets
            self.router = self.context.socket(zmq.ROUTER)
            self.router.bind(f"tcp://*:{self.dealer_port}")
            logger.info(f"ROUTER socket bound to port {self.dealer_port}")

            self.pub = self.context.socket(zmq.PUB)
            self.pub.bind(f"tcp://*:{self.pub_port}")
            logger.info(f"PUB socket bound to port {self.pub_port}")

            self.running = True

            # Start threads
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
                logger.error(f"Error: Another server instance is already running on port {self.dealer_port}")
                logger.error("Please stop the existing server before starting a new one.")
                logger.error("You can find the process using: lsof -i :5555")
                logger.error("And stop it using: kill <PID>")
                # Clean up sockets if partially created
                if self.router:
                    self.router.close()
                if self.pub:
                    self.pub.close()
                self.context.term()
                raise SystemExit(1)
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

        if self.router:
            self.router.close()
        if self.pub:
            self.pub.close()
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
                    self._increment_stat('message_count')

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
                            msg_type, data, raw_payload = binary_serializer.deserialize(message_bytes)
                            if msg_type == binary_serializer.MSG_CLIENT_TRANSFORM:
                                self._handle_client_transform(client_identity, room_id, data, raw_payload)
                            elif msg_type == binary_serializer.MSG_RPC_BROADCAST:
                                # Get sender's client number from client identity
                                sender_device_id = self._get_device_id_from_identity(client_identity, room_id)
                                if sender_device_id:
                                    sender_client_no = self._get_client_no_for_device_id(room_id, sender_device_id)
                                    data['senderClientNo'] = sender_client_no
                                # Broadcast RPC to room excluding sender
                                self._broadcast_rpc_to_room(room_id, data)
                            elif msg_type == binary_serializer.MSG_RPC_SERVER:
                                # Handle client-to-server RPC request
                                self._handle_rpc_request(client_identity, room_id, data)
                            elif msg_type == binary_serializer.MSG_RPC_CLIENT:
                                # Get sender's client number
                                sender_device_id = self._get_device_id_from_identity(client_identity, room_id)
                                if sender_device_id:
                                    sender_client_no = self._get_client_no_for_device_id(room_id, sender_device_id)
                                    data['senderClientNo'] = sender_client_no
                                # Handle client-to-client RPC request
                                self._handle_rpc_client_request(room_id, data)
                            elif msg_type == binary_serializer.MSG_GLOBAL_VAR_SET:
                                # Handle global variable set request
                                sender_device_id = self._get_device_id_from_identity(client_identity, room_id)
                                if sender_device_id and self._check_rate_limit(room_id, sender_device_id):
                                    self._handle_global_var_set(room_id, data)
                            elif msg_type == binary_serializer.MSG_CLIENT_VAR_SET:
                                # Handle client variable set request
                                sender_device_id = self._get_device_id_from_identity(client_identity, room_id)
                                if sender_device_id and self._check_rate_limit(room_id, sender_device_id):
                                    self._handle_client_var_set(room_id, data)
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
            elif msg_type in ["RpcBroadcast", "rpc_broadcast"]:
                logger.info(f"JSON RPC broadcast received in room {room_id}: {data}")
                self._broadcast_rpc_to_room(room_id, data)
            # JSON-based RPC server request
            elif msg_type in ["RpcServer", "rpc_server"]:
                logger.info(f"JSON RPC request received from {client_identity.hex()[:8]} in room {room_id}: {data}")
                self._handle_rpc_request(client_identity, room_id, data)
            else:
                logger.warning(f"Unknown message type: {msg_type}")

        except json.JSONDecodeError as e:
            logger.error(f"Failed to parse JSON: {e}")
            logger.error(f"Raw message: {message}")
        except Exception as e:
            logger.error(f"Error processing message: {e}")
            logger.error(traceback.format_exc())

    def _handle_client_transform(self, client_identity: bytes, room_id: str, data: Dict[str, Any], raw_payload: bytes = b''):
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
                    "last_update": time.time(),
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
                logger.info(f"New client {device_id[:8]}... (client number: {client_no}){stealth_text} joined room {room_id}")
            else:
                # Update existing client and mark room as dirty
                self.rooms[room_id][device_id]["transform_data"] = data_with_client_no
                self.rooms[room_id][device_id]["last_update"] = time.time()
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

    def _broadcast_rpc_to_room(self, room_id: str, rpc_data: Dict[str, Any]):
        """Broadcast RPC to all clients in room except sender"""
        # Log RPC broadcast
        sender_client_no = rpc_data.get('senderClientNo', 0)
        function_name = rpc_data.get('functionName', 'unknown')
        args = rpc_data.get('args', [])
        logger.info(f"RPC Broadcast: sender={sender_client_no}, function={function_name}, args={args}, room={room_id}")

        # Prepare topic and payload
        topic_bytes = room_id.encode('utf-8')
        message_bytes = binary_serializer.serialize_rpc_message(rpc_data)

        # Send multipart [roomId, payload]
        try:
            self.pub.send_multipart([topic_bytes, message_bytes])
            self._increment_stat('broadcast_count')
        except Exception as e:
            logger.error(f"Failed to broadcast RPC to room {room_id}: {e}")

    def _handle_rpc_request(self, client_identity: bytes, room_id: str, rpc_data: Dict[str, Any]):
        """Handle client-to-server RPC request"""
        # Log RPC server request
        sender_client_no = rpc_data.get('senderClientNo', 0)
        function_name = rpc_data.get('functionName', 'unknown')
        args = rpc_data.get('args', [])
        logger.info(f"RPC Server Request: sender={sender_client_no}, function={function_name}, args={args}, room={room_id}")
        # Here you can add your server-side RPC handling logic
        pass

    def _handle_rpc_client_request(self, room_id: str, rpc_data: Dict[str, Any]):
        """Handle client-to-client RPC request"""
        # Log RPC client request
        sender_client_no = rpc_data.get('senderClientNo', 0)
        target_client_no = rpc_data.get('targetClientNo', 0)
        function_name = rpc_data.get('functionName', 'unknown')
        args = rpc_data.get('args', [])
        logger.info(f"RPC Client Request: sender={sender_client_no}, target={target_client_no}, function={function_name}, args={args}, room={room_id}")

        # Resolve target device ID from client number
        target_device_id = self._get_device_id_from_client_no(room_id, target_client_no)
        if not target_device_id:
            logger.warning(f"Unknown target client number: {target_client_no} in room {room_id}")
            return

        # Send RPC to specific target client
        self._send_rpc_to_client(room_id, target_device_id, rpc_data)

    def _send_rpc_to_client(self, room_id: str, target_device_id: str, rpc_data: Dict[str, Any]):
        """Send RPC to a specific client in the room"""
        with self._rooms_lock:
            # Find the target client in the room
            room_clients = self.rooms.get(room_id, {})
            target_client_data = room_clients.get(target_device_id)

            if target_client_data:
                # Get the client's identity for direct messaging
                target_identity = target_client_data.get('identity')
                if target_identity:
                    try:
                        # Prepare topic and payload
                        topic_bytes = room_id.encode('utf-8')
                        message_bytes = binary_serializer.serialize_rpc_client_message(rpc_data)

                        # Send directly to the target client via PUB socket
                        # The client will receive this on their SUB socket subscribed to the room
                        self.pub.send_multipart([topic_bytes, message_bytes])
                        self._increment_stat('broadcast_count')
                    except Exception as e:
                        logger.error(f"Failed to send RPC to client {target_device_id[:8]}... in room {room_id}: {e}")
                else:
                    logger.warning(f"Target client {target_device_id[:8]}... has no identity in room {room_id}")
            else:
                logger.warning(f"Target client {target_device_id[:8]}... not found in room {room_id}")

    def _check_rate_limit(self, room_id: str, device_id: str) -> bool:
        """Check if client is within rate limit for Network Variables requests"""
        current_time = time.time()
        with self._rooms_lock:
            if room_id not in self.client_rate_limiter:
                self.client_rate_limiter[room_id] = {}

            client_times = self.client_rate_limiter[room_id].get(device_id, [])

            # Remove old timestamps
            cutoff_time = current_time - 1.0  # 1 second window
            client_times = [t for t in client_times if t > cutoff_time]

            # Check if under limit
            if len(client_times) < self.MAX_REQUESTS_PER_SECOND:
                client_times.append(current_time)
                self.client_rate_limiter[room_id][device_id] = client_times
                return True
            else:
                logger.warning(f"Rate limit exceeded for device {device_id[:8]}... in room {room_id}")
                return False

    def _handle_global_var_set(self, room_id: str, data: Dict[str, Any]):
        """Handle global variable set request"""
        sender_client_no = data.get('senderClientNo', 0)
        var_name = data.get('variableName', '')[:self.MAX_VAR_NAME_LENGTH]
        var_value = data.get('variableValue', '')[:self.MAX_VAR_VALUE_LENGTH]
        timestamp = data.get('timestamp', time.time())

        if not var_name:
            return

        with self._rooms_lock:
            self._initialize_room(room_id)

            # Check limits
            global_vars = self.global_variables[room_id]
            if var_name not in global_vars and len(global_vars) >= self.MAX_GLOBAL_VARS:
                logger.warning(f"Global variable limit reached in room {room_id}")
                return

            # Conflict resolution: last-writer-wins with timestamp comparison
            if var_name in global_vars:
                existing = global_vars[var_name]
                if timestamp < existing['timestamp'] or (timestamp == existing['timestamp'] and sender_client_no < existing['lastWriterClientNo']):
                    return  # Ignore older or lower priority update

            # Store old value for logging
            old_value = global_vars.get(var_name, {}).get('value', None)

            # Update variable
            global_vars[var_name] = {
                'value': var_value,
                'timestamp': timestamp,
                'lastWriterClientNo': sender_client_no
            }

            logger.info(f"Global Variable Changed: room={room_id}, client={sender_client_no}, name='{var_name}', old='{old_value}', new='{var_value}'")

            # Broadcast sync to all clients
            self._broadcast_global_var_sync(room_id)

    def _handle_client_var_set(self, room_id: str, data: Dict[str, Any]):
        """Handle client variable set request"""
        sender_client_no = data.get('senderClientNo', 0)
        target_client_no = data.get('targetClientNo', 0)
        var_name = data.get('variableName', '')[:self.MAX_VAR_NAME_LENGTH]
        var_value = data.get('variableValue', '')[:self.MAX_VAR_VALUE_LENGTH]
        timestamp = data.get('timestamp', time.time())

        if not var_name:
            return

        with self._rooms_lock:
            self._initialize_room(room_id)

            # Initialize client variables for target if needed
            if target_client_no not in self.client_variables[room_id]:
                self.client_variables[room_id][target_client_no] = {}

            client_vars = self.client_variables[room_id][target_client_no]

            # Check limits
            if var_name not in client_vars and len(client_vars) >= self.MAX_CLIENT_VARS:
                logger.warning(f"Client variable limit reached for client {target_client_no} in room {room_id}")
                return

            # Conflict resolution: last-writer-wins with timestamp comparison
            if var_name in client_vars:
                existing = client_vars[var_name]
                if timestamp < existing['timestamp'] or (timestamp == existing['timestamp'] and sender_client_no < existing['lastWriterClientNo']):
                    return  # Ignore older or lower priority update

            # Store old value for logging
            old_value = client_vars.get(var_name, {}).get('value', None)

            # Update variable
            client_vars[var_name] = {
                'value': var_value,
                'timestamp': timestamp,
                'lastWriterClientNo': sender_client_no
            }

            logger.info(f"Client Variable Changed: room={room_id}, target={target_client_no}, sender={sender_client_no}, name='{var_name}', old='{old_value}', new='{var_value}'")

            # Broadcast sync to all clients
            self._broadcast_client_var_sync(room_id)

    def _broadcast_global_var_sync(self, room_id: str):
        """Broadcast global variables sync to all clients in room"""
        with self._rooms_lock:
            if room_id not in self.global_variables:
                return

            variables = []
            for var_name, var_data in self.global_variables[room_id].items():
                variables.append({
                    'name': var_name,
                    'value': var_data['value'],
                    'timestamp': var_data['timestamp'],
                    'lastWriterClientNo': var_data['lastWriterClientNo']
                })

            if variables:
                try:
                    topic_bytes = room_id.encode('utf-8')
                    message_bytes = binary_serializer.serialize_global_var_sync({'variables': variables})
                    self.pub.send_multipart([topic_bytes, message_bytes])
                    self._increment_stat('broadcast_count')
                    logger.debug(f"Broadcasted {len(variables)} global variables to room {room_id}")
                except Exception as e:
                    logger.error(f"Failed to broadcast global variables to room {room_id}: {e}")

    def _broadcast_client_var_sync(self, room_id: str):
        """Broadcast client variables sync to all clients in room"""
        with self._rooms_lock:
            if room_id not in self.client_variables:
                return

            client_variables = {}
            for client_no, variables in self.client_variables[room_id].items():
                client_vars = []
                for var_name, var_data in variables.items():
                    client_vars.append({
                        'name': var_name,
                        'value': var_data['value'],
                        'timestamp': var_data['timestamp'],
                        'lastWriterClientNo': var_data['lastWriterClientNo']
                    })
                if client_vars:
                    client_variables[str(client_no)] = client_vars

            if client_variables:
                try:
                    topic_bytes = room_id.encode('utf-8')
                    message_bytes = binary_serializer.serialize_client_var_sync({'clientVariables': client_variables})
                    self.pub.send_multipart([topic_bytes, message_bytes])
                    self._increment_stat('broadcast_count')
                    logger.debug(f"Broadcasted client variables for {len(client_variables)} clients to room {room_id}")
                except Exception as e:
                    logger.error(f"Failed to broadcast client variables to room {room_id}: {e}")

    def _sync_network_variables_to_new_client(self, room_id: str):
        """Send current Network Variables state to a newly connected client"""
        self._broadcast_global_var_sync(room_id)
        self._broadcast_client_var_sync(room_id)

    def _broadcast_id_mappings(self, room_id: str):
        """Broadcast all device ID mappings for a room (including stealth clients with flag)"""
        with self._rooms_lock:
            if room_id not in self.room_device_id_to_client_no:
                return

            # Collect all mappings for the room (including stealth clients with their flag)
            mappings = []
            for device_id, client_no in self.room_device_id_to_client_no[room_id].items():
                # Get stealth status from client data
                client_data = self.rooms.get(room_id, {}).get(device_id, {})
                is_stealth = client_data.get("is_stealth", False)
                # Include all clients with their stealth flag
                mappings.append((client_no, device_id, is_stealth))

            if mappings:
                try:
                    # Serialize and broadcast the mappings
                    topic_bytes = room_id.encode('utf-8')
                    message_bytes = binary_serializer.serialize_device_id_mapping(mappings)
                    self.pub.send_multipart([topic_bytes, message_bytes])
                    logger.info(f"Broadcasted {len(mappings)} ID mappings to room {room_id}: {[(cno, did[:8], 'stealth' if stealth else 'normal') for cno, did, stealth in mappings]}")
                except Exception as e:
                    logger.error(f"Failed to broadcast ID mappings to room {room_id}: {e}")

    def _periodic_loop(self):
        """Combined broadcast and cleanup loop with adaptive rates"""
        logger.info("Periodic loop started")
        last_broadcast_check = 0
        last_cleanup = 0
        last_device_id_cleanup = 0
        last_log = time.time()
        DEVICE_ID_CLEANUP_INTERVAL = 60.0  # Clean up expired device IDs every minute

        while self.running:
            try:
                current_time = time.time()

                # Check for broadcasts at higher frequency but only broadcast when needed
                if current_time - last_broadcast_check >= self.BROADCAST_CHECK_INTERVAL:
                    self._adaptive_broadcast_all_rooms(current_time)
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

                    dirty_rooms = sum(1 for flag in self.room_dirty_flags.values() if flag)
                    total_device_ids = len(self.device_id_last_seen)
                    logger.info(f"Status: {len(self.rooms)} rooms, {normal_clients} normal clients, "
                            f"{stealth_clients} stealth clients, "
                            f"{dirty_rooms} dirty rooms, {total_device_ids} tracked device IDs")
                    last_log = current_time

                time.sleep(self.MAIN_LOOP_SLEEP)  # 50Hz loop for better responsiveness

            except Exception as e:
                logger.error(f"Error in periodic loop: {e}")

        logger.info("Periodic loop ended")

    def _adaptive_broadcast_all_rooms(self, current_time):
        """Broadcast room state with adaptive rates based on activity"""
        with self._rooms_lock:
            rooms_copy = list(self.rooms.items())  # Create copy to avoid holding lock too long

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
                    self._increment_stat('skipped_broadcasts')

    def _broadcast_room(self, room_id, clients):
        """Broadcast a specific room's state"""
        # Filter out stealth clients from broadcasts
        client_transforms = [
            client["transform_data"] for client in clients.values()
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
            self.pub.send_multipart([topic_bytes, message_bytes])
            self._increment_stat('broadcast_count')


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
                        logger.info(f"Client {device_id[:8]}... (client number: {client_no}) removed (timeout)")

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

                    logger.info(f"Removed empty room: {room_id}")

                except Exception as e:
                    logger.error(f"Error during room cleanup for {room_id}: {e}")
                    # Continue with other rooms even if one fails

    def _start_beacon(self):
        """Start server discovery service to respond to client requests"""
        try:
            self.beacon_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            self.beacon_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            self.beacon_socket.bind(('', self.beacon_port))
            self.beacon_socket.settimeout(1.0)  # Timeout for graceful shutdown

            self.beacon_running = True
            self.beacon_thread = threading.Thread(
                target=self._beacon_loop,
                name="BeaconThread",
                daemon=True
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
        response = f"STYLY-NETSYNC|{self.dealer_port}|{self.pub_port}|{self.server_name}"
        response_bytes = response.encode('utf-8')

        while self.beacon_running:
            try:
                # Wait for client discovery request
                data, client_addr = self.beacon_socket.recvfrom(1024)
                request = data.decode('utf-8')

                # Validate request format
                if request == "STYLY-NETSYNC-DISCOVER":
                    # Send response back to requesting client
                    self.beacon_socket.sendto(response_bytes, client_addr)
                    logger.debug(f"Responded to discovery request from {client_addr}")

            except socket.timeout:
                # Timeout is expected for graceful shutdown
                continue
            except Exception as e:
                if self.beacon_running:  # Only log if we're still supposed to be running
                    logger.error(f"Discovery service error: {e}")



def main():
    parser = argparse.ArgumentParser(description="STYLY NetSync Server")
    parser.add_argument('--dealer-port', type=int, default=5555,
                        help='Port for DEALER socket (default: 5555)')
    parser.add_argument('--pub-port', type=int, default=5556,
                        help='Port for PUB socket (default: 5556)')
    parser.add_argument('--beacon-port', type=int, default=9999,
                        help='Port for UDP beacon discovery (default: 9999)')
    parser.add_argument('--name', default='STYLY-NetSync-Server',
                        help='Server name for discovery (default: STYLY-NetSync-Server)')
    parser.add_argument('--no-beacon', action='store_true',
                        help='Disable beacon discovery')

    args = parser.parse_args()

    logger.info("=" * 80)
    logger.info("STYLY NetSync Server Starting")
    logger.info("=" * 80)
    logger.info(f"  DEALER port: {args.dealer_port}")
    logger.info(f"  PUB port: {args.pub_port}")
    if not args.no_beacon:
        logger.info(f"  Beacon port: {args.beacon_port}")
        logger.info(f"  Server name: {args.name}")
    else:
        logger.info("  Discovery: Disabled")
    logger.info("=" * 80)

    server = NetSyncServer(
        dealer_port=args.dealer_port,
        pub_port=args.pub_port,
        enable_beacon=not args.no_beacon,
        beacon_port=args.beacon_port,
        server_name=args.name
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


if __name__ == "__main__":
    main()
