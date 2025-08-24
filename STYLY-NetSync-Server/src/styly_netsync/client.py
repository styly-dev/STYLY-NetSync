from __future__ import annotations

import json
import socket
import threading
import time
from collections import defaultdict, deque
from collections.abc import Callable

import zmq

from . import binary_serializer
from .adapters import client_transform_to_wire, room_snapshot_from_wire
from .events import EventHook
from .types import client_transform, room_snapshot, transform


class net_sync_manager:
    """Python NetSync client manager using ZeroMQ."""

    def __init__(
        self,
        server: str,
        dealer_port: int,
        sub_port: int,
        room: str,
        *,
        auto_dispatch: bool = False,
        queue_max: int = 10000,
    ) -> None:
        self.server = server
        self.dealer_port = dealer_port
        self.sub_port = sub_port
        self.room = room
        self._auto_dispatch = auto_dispatch
        self._queue_max = queue_max

        self._context = zmq.Context()
        self._dealer: zmq.Socket | None = None
        self._sub: zmq.Socket | None = None
        self._recv_thread: threading.Thread | None = None
        self._running = False
        self._stop_event = threading.Event()

        # Snapshot storage
        self._latest_room: room_snapshot | None = None
        self._room_lock = threading.RLock()

        # Mapping tables
        self._client_to_device: dict[int, str] = {}
        self._device_to_client: dict[str, int] = {}
        self._stealth: dict[int, bool] = {}

        # Network variables caches
        self._global_vars: dict[str, str] = {}
        self._client_vars: dict[int, dict[str, str]] = defaultdict(dict)

        # Event management
        self._rpc_event = EventHook()
        self._global_var_event = EventHook()
        self._client_var_event = EventHook()

        # Queues for pull/dispatch
        self._rpc_queue: deque[tuple[int, str, list[str]]] = deque()
        self._global_queue: deque[tuple[str, str | None, str, dict[str, object]]] = (
            deque()
        )
        self._client_queue: deque[
            tuple[int, str, str | None, str, dict[str, object]]
        ] = deque()

        # Discovery
        self._discovery_thread: threading.Thread | None = None
        self._discovery_stop = threading.Event()

        # Stats
        self._stats: dict[str, int] = defaultdict(int)

    # ------------------------------------------------------------------
    # Lifecycle
    # ------------------------------------------------------------------
    def start(self) -> net_sync_manager:
        """Connect sockets and start receive loop."""
        if self._running:
            return self
        self._dealer = self._context.socket(zmq.DEALER)
        self._dealer.connect(f"{self.server}:{self.dealer_port}")
        self._sub = self._context.socket(zmq.SUB)
        self._sub.connect(f"{self.server}:{self.sub_port}")
        self._sub.setsockopt_string(zmq.SUBSCRIBE, self.room)
        self._stop_event.clear()
        self._recv_thread = threading.Thread(target=self._recv_loop, daemon=True)
        self._recv_thread.start()
        self._running = True
        return self

    def stop(self) -> None:
        """Stop threads and close sockets."""
        if not self._running:
            return
        self._stop_event.set()
        if self._recv_thread:
            self._recv_thread.join(timeout=1.0)
        if self._dealer:
            self._dealer.close(0)
        if self._sub:
            self._sub.close(0)
        self._context.term()
        self._running = False

    close = stop

    @property
    def is_running(self) -> bool:  # pragma: no cover - simple property
        return self._running

    # ------------------------------------------------------------------
    # Sending
    # ------------------------------------------------------------------
    def _send(self, payload: bytes) -> bool:
        if not self._dealer:
            return False
        try:
            self._dealer.send_multipart([self.room.encode("utf-8"), payload])
            return True
        except Exception:
            return False

    def send_transform(self, tx: client_transform) -> bool:
        data = client_transform_to_wire(tx)
        payload = binary_serializer.serialize_client_transform(data)
        return self._send(payload)

    def send_stealth_handshake(self) -> bool:
        nan = float("nan")
        t = transform(pos_x=nan, pos_y=nan, pos_z=nan, rot_x=nan, rot_y=nan, rot_z=nan)
        ct = client_transform(
            physical=t, head=t, right_hand=t, left_hand=t, virtuals=[]
        )
        return self.send_transform(ct)

    def rpc(self, function_name: str, args: list[str] | tuple[str, ...]) -> bool:
        data = {
            "senderClientNo": 0,
            "functionName": function_name,
            "argumentsJson": json.dumps(list(args)),
        }
        payload = binary_serializer.serialize_rpc_message(data)
        return self._send(payload)

    def set_global_variable(self, name: str, value: str) -> bool:
        data = {
            "senderClientNo": 0,
            "variableName": name,
            "variableValue": value,
            "timestamp": time.time(),
        }
        payload = binary_serializer.serialize_global_var_set(data)
        return self._send(payload)

    def set_client_variable(self, target_client_no: int, name: str, value: str) -> bool:
        data = {
            "senderClientNo": 0,
            "targetClientNo": target_client_no,
            "variableName": name,
            "variableValue": value,
            "timestamp": time.time(),
        }
        payload = binary_serializer.serialize_client_var_set(data)
        return self._send(payload)

    def get_global_variable(self, name: str, default: str | None = None) -> str | None:
        return self._global_vars.get(name, default)

    def get_client_variable(
        self, client_no: int, name: str, default: str | None = None
    ) -> str | None:
        return self._client_vars.get(client_no, {}).get(name, default)

    # ------------------------------------------------------------------
    # Transform consumption
    # ------------------------------------------------------------------
    def latest_room(self) -> room_snapshot | None:
        with self._room_lock:
            return self._latest_room

    def latest_client_transform(self, client_no: int) -> client_transform | None:
        snap = self.latest_room()
        if snap:
            return snap.clients.get(client_no)
        return None

    # ------------------------------------------------------------------
    # Callbacks registration
    # ------------------------------------------------------------------
    def on_rpc_received(
        self, cb: Callable[[int, str, list[str]], None]
    ) -> Callable[[], None]:
        return self._rpc_event.register(cb)

    def on_global_variable_changed(
        self, cb: Callable[[str, str | None, str, dict[str, object]], None]
    ) -> Callable[[], None]:
        return self._global_var_event.register(cb)

    def on_client_variable_changed(
        self, cb: Callable[[int, str, str | None, str, dict[str, object]], None]
    ) -> Callable[[], None]:
        return self._client_var_event.register(cb)

    # ------------------------------------------------------------------
    # Pull interfaces
    # ------------------------------------------------------------------
    def take_rpc(self) -> tuple[int, str, list[str]] | None:
        if self._rpc_queue:
            return self._rpc_queue.popleft()
        return None

    def take_global_variable_change(
        self,
    ) -> tuple[str, str | None, str, dict[str, object]] | None:
        if self._global_queue:
            return self._global_queue.popleft()
        return None

    def take_client_variable_change(
        self,
    ) -> tuple[int, str, str | None, str, dict[str, object]] | None:
        if self._client_queue:
            return self._client_queue.popleft()
        return None

    def dispatch_pending(self, max_items: int = 100) -> int:
        processed = 0
        while processed < max_items and self._rpc_queue:
            item = self._rpc_queue.popleft()
            self._rpc_event.fire(*item)
            processed += 1
        while processed < max_items and self._global_queue:
            item = self._global_queue.popleft()
            self._global_var_event.fire(*item)
            processed += 1
        while processed < max_items and self._client_queue:
            item = self._client_queue.popleft()
            self._client_var_event.fire(*item)
            processed += 1
        return processed

    # ------------------------------------------------------------------
    # Mapping
    # ------------------------------------------------------------------
    def get_client_no(self, device_id: str) -> int | None:
        return self._device_to_client.get(device_id)

    def get_device_id(self, client_no: int) -> str | None:
        return self._client_to_device.get(client_no)

    # ------------------------------------------------------------------
    # Discovery
    # ------------------------------------------------------------------
    def start_discovery(self, beacon_port: int = 9999) -> None:
        if self._discovery_thread and self._discovery_thread.is_alive():
            return
        self._discovery_stop.clear()
        self._discovery_thread = threading.Thread(
            target=self._discovery_loop, args=(beacon_port,), daemon=True
        )
        self._discovery_thread.start()

    def stop_discovery(self) -> None:
        self._discovery_stop.set()
        if self._discovery_thread:
            self._discovery_thread.join(timeout=1.0)
            self._discovery_thread = None

    def _discovery_loop(self, port: int) -> None:
        msg = b"STYLY-NETSYNC-DISCOVER"
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            sock.settimeout(1.0)
            while not self._discovery_stop.is_set():
                try:
                    sock.sendto(msg, ("255.255.255.255", port))
                    while True:
                        try:
                            data, addr = sock.recvfrom(1024)
                        except TimeoutError:
                            break
                        try:
                            dealer_str, pub_str, _name = data.decode().split("|")
                            self.server = f"tcp://{addr[0]}"
                            self.dealer_port = int(dealer_str)
                            self.sub_port = int(pub_str)
                        except Exception:
                            pass
                except Exception:
                    pass
                time.sleep(2.0)

    # ------------------------------------------------------------------
    # Diagnostics
    # ------------------------------------------------------------------
    def stats(self) -> dict[str, object]:
        return {
            "messages_received": dict(self._stats),
            "queue_depths": {
                "rpc": len(self._rpc_queue),
                "global": len(self._global_queue),
                "client": len(self._client_queue),
            },
            "last_snapshot_time": (
                self._latest_room.timestamp if self._latest_room else None
            ),
        }

    # ------------------------------------------------------------------
    # Receive loop
    # ------------------------------------------------------------------
    def _recv_loop(self) -> None:
        poller = zmq.Poller()
        assert self._sub is not None
        poller.register(self._sub, zmq.POLLIN)
        while not self._stop_event.is_set():
            events = dict(poller.poll(100))
            if self._sub in events:
                try:
                    topic, payload = self._sub.recv_multipart()
                except Exception:
                    continue
                self._handle_payload(payload)

    def _handle_payload(self, payload: bytes) -> None:
        msg_type, data, _ = binary_serializer.deserialize(payload)
        if data is None:
            return
        self._stats[str(msg_type)] += 1
        if msg_type == binary_serializer.MSG_ROOM_TRANSFORM:
            snap = room_snapshot_from_wire(data)
            with self._room_lock:
                self._latest_room = snap
        elif msg_type == binary_serializer.MSG_RPC:
            args = json.loads(data.get("argumentsJson", "[]"))
            item = (data.get("senderClientNo", 0), data.get("functionName", ""), args)
            if self._auto_dispatch:
                self._rpc_event.fire(*item)
            else:
                self._append_queue(self._rpc_queue, item)
        elif msg_type == binary_serializer.MSG_GLOBAL_VAR_SYNC:
            for var in data.get("variables", []):
                name = var.get("name", "")
                value = var.get("value", "")
                old = self._global_vars.get(name)
                self._global_vars[name] = value
                meta = {
                    "timestamp": var.get("timestamp"),
                    "last_writer_client_no": var.get("lastWriterClientNo"),
                }
                item = (name, old, value, meta)
                if self._auto_dispatch:
                    self._global_var_event.fire(*item)
                else:
                    self._append_queue(self._global_queue, item)
        elif msg_type == binary_serializer.MSG_CLIENT_VAR_SYNC:
            for client_no_str, vars in data.get("clientVariables", {}).items():
                client_no = int(client_no_str)
                cache = self._client_vars[client_no]
                for var in vars:
                    name = var.get("name", "")
                    value = var.get("value", "")
                    old = cache.get(name)
                    cache[name] = value
                    meta = {
                        "timestamp": var.get("timestamp"),
                        "last_writer_client_no": var.get("lastWriterClientNo"),
                    }
                    item = (client_no, name, old, value, meta)
                    if self._auto_dispatch:
                        self._client_var_event.fire(*item)
                    else:
                        self._append_queue(self._client_queue, item)
        elif msg_type == binary_serializer.MSG_DEVICE_ID_MAPPING:
            for m in data.get("mappings", []):
                client_no = m.get("clientNo")
                device_id = m.get("deviceId")
                if client_no is None or device_id is None:
                    continue
                self._client_to_device[client_no] = device_id
                self._device_to_client[device_id] = client_no
                self._stealth[client_no] = bool(m.get("isStealthMode"))

    def _append_queue(self, queue: deque, item: object) -> None:
        if len(queue) >= self._queue_max:
            queue.popleft()
        queue.append(item)


# ----------------------------------------------------------------------
# Factory
# ----------------------------------------------------------------------


def create_manager(
    *,
    server: str = "tcp://localhost",
    dealer_port: int = 5555,
    sub_port: int = 5556,
    room: str = "default_room",
    auto_dispatch: bool = False,
    queue_max: int = 10000,
) -> net_sync_manager:
    """Factory for :class:`net_sync_manager`."""
    return net_sync_manager(
        server=server,
        dealer_port=dealer_port,
        sub_port=sub_port,
        room=room,
        auto_dispatch=auto_dispatch,
        queue_max=queue_max,
    )
