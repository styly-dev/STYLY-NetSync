import json
import logging
import queue
import socket
import threading
import time
import uuid
from typing import Any, Callable, Dict, List, Optional, Tuple, Union

import zmq

from . import binary_serializer
from .adapters import (
    client_transform_to_dict,
    dict_to_client_transform,
)
from .events import EventHandler
from .types import client_transform, room_snapshot, transform

logger = logging.getLogger(__name__)

def create_manager(
    server: str = "tcp://localhost",
    dealer_port: int = 5555,
    sub_port: int = 5556,
    room: str = "default_room",
    auto_dispatch: bool = False,
    queue_max: int = 10000,
    device_id: Optional[str] = None,
) -> "net_sync_manager":
    """
    Factory function to create a net_sync_manager instance.
    """
    return net_sync_manager(
        server=server,
        dealer_port=dealer_port,
        sub_port=sub_port,
        room=room,
        auto_dispatch=auto_dispatch,
        queue_max=queue_max,
        device_id=device_id,
    )


class net_sync_manager:
    """
    Manages the connection to the NetSync server, handles sending and receiving data.
    """

    def __init__(
        self,
        server: str,
        dealer_port: int,
        sub_port: int,
        room: str,
        auto_dispatch: bool,
        queue_max: int,
        device_id: Optional[str] = None,
    ):
        self._server = server
        self._dealer_port = dealer_port
        self._sub_port = sub_port
        self._room = room
        self._auto_dispatch = auto_dispatch
        self._queue_max = queue_max
        self._device_id = device_id or str(uuid.uuid4())

        self._context = zmq.Context()
        self._dealer_socket: Optional[zmq.Socket] = None
        self._sub_socket: Optional[zmq.Socket] = None

        self._is_running = False
        self._receive_thread: Optional[threading.Thread] = None
        self._discovery_thread: Optional[threading.Thread] = None
        self._discovery_running = False

        # State
        self._latest_room_snapshot_lock = threading.Lock()
        self._latest_room_snapshot: Optional[room_snapshot] = None

        self._mapping_lock = threading.Lock()
        self._client_no_to_device_id: Dict[int, Tuple[str, bool]] = {}
        self._device_id_to_client_no: Dict[str, int] = {}

        self._nv_lock = threading.Lock()
        self._global_variables: Dict[str, str] = {}
        self._client_variables: Dict[int, Dict[str, str]] = {}

        # Event Handlers
        self.on_rpc_received = EventHandler()
        self.on_global_variable_changed = EventHandler()
        self.on_client_variable_changed = EventHandler()

        # Queues for manual dispatch
        self._event_queue: queue.Queue = queue.Queue(maxsize=self._queue_max)

        # Stats
        self._stats_lock = threading.Lock()
        self._stats: Dict[str, Any] = {
            "msgs_received": 0,
            "transforms_received": 0,
            "rpc_received": 0,
            "nv_syncs_received": 0,
            "mappings_received": 0,
            "event_queue_dropped": 0,
        }

    @property
    def is_running(self) -> bool:
        return self._is_running

    @property
    def room(self) -> str:
        return self._room

    @property
    def server(self) -> str:
        return self._server

    @property
    def dealer_port(self) -> int:
        return self._dealer_port

    @property
    def sub_port(self) -> int:
        return self._sub_port

    def start(self) -> "net_sync_manager":
        if self._is_running:
            return self

        self._is_running = True

        # Setup sockets
        self._dealer_socket = self._context.socket(zmq.DEALER)
        self._dealer_socket.connect(f"{self._server}:{self._dealer_port}")

        self._sub_socket = self._context.socket(zmq.SUB)
        self._sub_socket.connect(f"{self._server}:{self._sub_port}")
        self._sub_socket.setsockopt_string(zmq.SUBSCRIBE, self._room)

        # Start receive loop
        self._receive_thread = threading.Thread(target=self._receive_loop, daemon=True)
        self._receive_thread.start()

        logger.info("NetSync manager started.")
        return self

    def stop(self):
        if not self._is_running:
            return

        self._is_running = False

        if self._discovery_running:
            self.stop_discovery()

        if self._receive_thread:
            self._receive_thread.join()

        if self._dealer_socket:
            self._dealer_socket.close(linger=0)
        if self._sub_socket:
            self._sub_socket.close(linger=0)

        # self._context.term() # This can cause issues if another client is started

        logger.info("NetSync manager stopped.")

    def close(self):
        self.stop()

    def _receive_loop(self):
        while self._is_running:
            try:
                if self._sub_socket.poll(100):
                    _, message_bytes = self._sub_socket.recv_multipart()
                    with self._stats_lock:
                        self._stats["msgs_received"] += 1

                    msg_type, data, _ = binary_serializer.deserialize(message_bytes)

                    if data is None:
                        continue

                    if msg_type == binary_serializer.MSG_ROOM_TRANSFORM:
                        self._handle_room_transform(data)
                    elif msg_type == binary_serializer.MSG_RPC:
                        self._handle_rpc(data)
                    elif msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING:
                        self._handle_device_id_mapping(data)
                    elif msg_type == binary_serializer.MSG_GLOBAL_VAR_SYNC:
                        self._handle_global_var_sync(data)
                    elif msg_type == binary_serializer.MSG_CLIENT_VAR_SYNC:
                        self._handle_client_var_sync(data)

            except zmq.ZMQError as e:
                if e.errno == zmq.ETERM:
                    break  # Context terminated
                logger.error(f"ZMQ error in receive loop: {e}")
            except Exception as e:
                logger.error(f"Error in receive loop: {e}")

    def _handle_room_transform(self, data: Dict[str, Any]):
        with self._stats_lock:
            self._stats["transforms_received"] += 1

        clients = {}
        for client_data in data.get("clients", []):
            ct = dict_to_client_transform(client_data)
            clients[ct.client_no] = ct

        new_snapshot = room_snapshot(
            room_id=data.get("roomId", self._room),
            clients=clients,
            timestamp=time.monotonic()
        )

        with self._latest_room_snapshot_lock:
            self._latest_room_snapshot = new_snapshot

    def _handle_rpc(self, data: Dict[str, Any]):
        with self._stats_lock:
            self._stats["rpc_received"] += 1

        sender_client_no = data.get("senderClientNo", 0)
        function_name = data.get("functionName", "")
        try:
            args = json.loads(data.get("argumentsJson", "[]"))
        except json.JSONDecodeError:
            args = []

        if self._auto_dispatch:
            self.on_rpc_received.fire(sender_client_no, function_name, args)
        else:
            try:
                self._event_queue.put_nowait(("rpc", (sender_client_no, function_name, args)))
            except queue.Full:
                with self._stats_lock:
                    self._stats["event_queue_dropped"] += 1

    def _handle_device_id_mapping(self, data: Dict[str, Any]):
        with self._stats_lock:
            self._stats["mappings_received"] += 1

        with self._mapping_lock:
            self._client_no_to_device_id.clear()
            self._device_id_to_client_no.clear()
            for mapping in data.get("mappings", []):
                client_no = mapping.get("clientNo")
                device_id = mapping.get("deviceId")
                is_stealth = mapping.get("isStealthMode", False)
                if client_no is not None and device_id:
                    self._client_no_to_device_id[client_no] = (device_id, is_stealth)
                    self._device_id_to_client_no[device_id] = client_no

    def _handle_global_var_sync(self, data: Dict[str, Any]):
        with self._stats_lock:
            self._stats["nv_syncs_received"] += 1

        for var in data.get("variables", []):
            name = var.get("name")
            value = var.get("value")
            if name is None or value is None:
                continue

            with self._nv_lock:
                old_value = self._global_variables.get(name)
                if old_value != value:
                    self._global_variables[name] = value
                    meta = {"timestamp": var.get("timestamp"), "lastWriterClientNo": var.get("lastWriterClientNo")}
                    if self._auto_dispatch:
                        self.on_global_variable_changed.fire(name, old_value, value, meta)
                    else:
                        try:
                            self._event_queue.put_nowait(("global_var", (name, old_value, value, meta)))
                        except queue.Full:
                            with self._stats_lock:
                                self._stats["event_queue_dropped"] += 1

    def _handle_client_var_sync(self, data: Dict[str, Any]):
        with self._stats_lock:
            self._stats["nv_syncs_received"] += 1

        for client_no_str, variables in data.get("clientVariables", {}).items():
            try:
                client_no = int(client_no_str)
                with self._nv_lock:
                    if client_no not in self._client_variables:
                        self._client_variables[client_no] = {}

                    for var in variables:
                        name = var.get("name")
                        value = var.get("value")
                        if name is None or value is None:
                            continue

                        old_value = self._client_variables[client_no].get(name)
                        if old_value != value:
                            self._client_variables[client_no][name] = value
                            meta = {"timestamp": var.get("timestamp"), "lastWriterClientNo": var.get("lastWriterClientNo")}
                            if self._auto_dispatch:
                                self.on_client_variable_changed.fire(client_no, name, old_value, value, meta)
                            else:
                                try:
                                    self._event_queue.put_nowait(("client_var", (client_no, name, old_value, value, meta)))
                                except queue.Full:
                                    with self._stats_lock:
                                        self._stats["event_queue_dropped"] += 1
            except (ValueError, TypeError):
                continue

    def send_transform(self, tx: client_transform) -> bool:
        if not self._is_running or not self._dealer_socket:
            return False

        tx.device_id = self._device_id
        data = client_transform_to_dict(tx)
        payload = binary_serializer.serialize_client_transform(data)

        try:
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload])
            return True
        except zmq.ZMQError as e:
            logger.error(f"Failed to send transform: {e}")
            return False

    def send_stealth_handshake(self) -> bool:
        nan_transform = transform(pos_x=float('nan'), pos_y=float('nan'), pos_z=float('nan'), rot_x=float('nan'), rot_y=float('nan'), rot_z=float('nan'))
        stealth_tx = client_transform(
            physical=nan_transform,
            head=nan_transform,
            right_hand=nan_transform,
            left_hand=nan_transform,
            virtuals=[]
        )
        return self.send_transform(stealth_tx)

    def rpc(self, function_name: str, args: Union[List[str], Tuple[str, ...]]) -> bool:
        if not self._is_running or not self._dealer_socket:
            return False

        data = {
            "functionName": function_name,
            "argumentsJson": json.dumps(args)
        }
        payload = binary_serializer.serialize_rpc_message(data)
        try:
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload])
            return True
        except zmq.ZMQError as e:
            logger.error(f"Failed to send RPC: {e}")
            return False

    def set_global_variable(self, name: str, value: str) -> bool:
        if not self._is_running or not self._dealer_socket:
            return False

        data = {
            "variableName": name,
            "variableValue": value,
            "timestamp": time.time()
        }
        payload = binary_serializer.serialize_global_var_set(data)
        try:
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload])
            return True
        except zmq.ZMQError as e:
            logger.error(f"Failed to set global variable: {e}")
            return False

    def set_client_variable(self, target_client_no: int, name: str, value: str) -> bool:
        if not self._is_running or not self._dealer_socket:
            return False

        data = {
            "targetClientNo": target_client_no,
            "variableName": name,
            "variableValue": value,
            "timestamp": time.time()
        }
        payload = binary_serializer.serialize_client_var_set(data)
        try:
            self._dealer_socket.send_multipart([self._room.encode('utf-8'), payload])
            return True
        except zmq.ZMQError as e:
            logger.error(f"Failed to set client variable: {e}")
            return False

    def get_global_variable(self, name: str, default: Optional[str] = None) -> Optional[str]:
        with self._nv_lock:
            return self._global_variables.get(name, default)

    def get_client_variable(self, client_no: int, name: str, default: Optional[str] = None) -> Optional[str]:
        with self._nv_lock:
            return self._client_variables.get(client_no, {}).get(name, default)

    def latest_room(self) -> Optional[room_snapshot]:
        with self._latest_room_snapshot_lock:
            return self._latest_room_snapshot

    def latest_client_transform(self, client_no: int) -> Optional[client_transform]:
        with self._latest_room_snapshot_lock:
            if self._latest_room_snapshot:
                return self._latest_room_snapshot.clients.get(client_no)
        return None

    def dispatch_pending(self, max_items: int = 100) -> int:
        count = 0
        for _ in range(max_items):
            try:
                event_type, args = self._event_queue.get_nowait()
                if event_type == "rpc":
                    self.on_rpc_received.fire(*args)
                elif event_type == "global_var":
                    self.on_global_variable_changed.fire(*args)
                elif event_type == "client_var":
                    self.on_client_variable_changed.fire(*args)
                count += 1
            except queue.Empty:
                break
        return count

    def get_client_no(self, device_id: str) -> Optional[int]:
        with self._mapping_lock:
            return self._device_id_to_client_no.get(device_id)

    def get_device_id(self, client_no: int) -> Optional[str]:
        with self._mapping_lock:
            mapping = self._client_no_to_device_id.get(client_no)
            return mapping[0] if mapping else None

    def start_discovery(self, beacon_port: int = 9999):
        if self._discovery_running:
            return
        self._discovery_running = True
        self._discovery_thread = threading.Thread(
            target=self._discovery_loop, args=(beacon_port,), daemon=True
        )
        self._discovery_thread.start()
        logger.info(f"Discovery started on port {beacon_port}.")

    def stop_discovery(self):
        self._discovery_running = False
        if self._discovery_thread:
            self._discovery_thread.join()
        logger.info("Discovery stopped.")

    def _discovery_loop(self, beacon_port: int):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.settimeout(1.0)

        message = b"STYLY-NETSYNC-DISCOVER"

        while self._discovery_running:
            try:
                sock.sendto(message, ('<broadcast>', beacon_port))
                data, _ = sock.recvfrom(1024)
                response = data.decode('utf-8')
                parts = response.split('|')
                if parts[0] == "STYLY-NETSYNC" and len(parts) >= 4:
                    _, dealer_port, sub_port, name = parts[:4]
                    logger.info(f"Discovered server '{name}' at {self._server}:{dealer_port}/{sub_port}")
                    # Here one could implement auto-reconnect logic
            except socket.timeout:
                continue
            except Exception as e:
                logger.error(f"Error in discovery loop: {e}")

            # Wait a bit before next discovery attempt
            if self._discovery_running:
                time.sleep(2)

    def stats(self) -> Dict[str, Any]:
        with self._stats_lock:
            stats_copy = self._stats.copy()
        stats_copy["event_queue_size"] = self._event_queue.qsize()
        with self._latest_room_snapshot_lock:
            if self._latest_room_snapshot:
                stats_copy["last_snapshot_time"] = self._latest_room_snapshot.timestamp
        return stats_copy
