"""Simulator transport following Unity's ConnectionManager scheduling.

Sockets belong to one network thread; application updates exchange queued
messages with it. libzmq is still not NetMQ: this models application-level
queues and socket options, not identical transport error timing.
"""

from __future__ import annotations

import logging
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass
from queue import Empty, Full, Queue
from typing import Any

import zmq

from .binary_serializer import (
    MSG_DEVICE_ID_MAPPING,
    deserialize,
    serialize_client_hello,
    serialize_client_transform,
    serialize_client_var_set,
)

ReceivedMessage = tuple[int, dict[str, Any] | None]


@dataclass(frozen=True)
class OutboundPacket:
    room: bytes
    payload: bytes
    enqueued_at: float


class NetworkTransport:
    """Unity-compatible outbox, network pump, and application receive queues."""

    CONTROL_OUTBOX_MAX = 256
    CONTROL_TTL = 5.0
    CONTROL_DRAIN_BATCH = 64

    def __init__(
        self,
        context: zmq.Context[Any],
        server_addr: str,
        dealer_port: int,
        transform_port: int | None,
        sub_port: int,
        room_id: str,
        enable_sub: bool = True,
        socket_register: Callable[[zmq.Socket[Any]], None] | None = None,
        socket_unregister: Callable[[zmq.Socket[Any]], None] | None = None,
    ) -> None:
        self.context = context
        self.server_addr = server_addr
        self.control_port = dealer_port
        self.dealer_port = dealer_port
        self.transform_port = (
            transform_port if transform_port is not None else dealer_port + 2
        )
        self.sub_port = sub_port
        self.room_id = room_id
        self.enable_sub = enable_sub
        self.socket: zmq.Socket[Any] | None = None
        self.transform_socket: zmq.Socket[Any] | None = None
        self.sub_socket: zmq.Socket[Any] | None = None
        self.logger = logging.getLogger(type(self).__name__)
        self._socket_register = socket_register
        self._socket_unregister = socket_unregister
        self._control_outbox: Queue[OutboundPacket] = Queue(self.CONTROL_OUTBOX_MAX)
        self._pending_control: OutboundPacket | None = None
        self._latest_transform: OutboundPacket | None = None
        self._transform_lock = threading.Lock()
        self._incoming_control: Queue[ReceivedMessage] = Queue()
        self._incoming_broadcast: dict[bytes, ReceivedMessage] = {}
        self._broadcast_lock = threading.Lock()
        self._stop = threading.Event()
        self._started = threading.Event()
        self._thread: threading.Thread | None = None
        self.connected = False
        self.connection_error: str | None = None
        self.control_queue_drops = 0
        self.control_expired = 0
        self.would_block = 0
        self.dropped_transform_frames = 0
        self.sent_control = 0
        self.sent_transform = 0

    def connect(self) -> bool:
        if self._thread is not None:
            return self.connected
        self.connection_error = None
        self._stop.clear()
        self._started.clear()
        self._thread = threading.Thread(
            target=self._network_loop, daemon=True, name="SimulatorNetwork"
        )
        self._thread.start()
        if not self._started.wait(5):
            self.disconnect()
            return False
        return self.connected

    def _make_socket(
        self,
        kind: int,
        port: int,
        send_hwm: int | None = None,
        receive_hwm: int | None = None,
    ) -> zmq.Socket[Any]:
        sock: zmq.Socket[Any] = self.context.socket(kind)
        # Track immediately so partial startup is also cleaned up by the owner.
        self._owned_sockets.append(sock)
        if self._socket_register:
            self._socket_register(sock)
        sock.setsockopt(zmq.LINGER, 0)
        if send_hwm is not None:
            sock.setsockopt(zmq.SNDHWM, send_hwm)
        if receive_hwm is not None:
            sock.setsockopt(zmq.RCVHWM, receive_hwm)
        # Unity leaves DelayAttachOnConnect=false: do not force IMMEDIATE=1.
        sock.connect(f"{self.server_addr}:{port}")
        return sock

    def _network_loop(self) -> None:
        self._owned_sockets: list[zmq.Socket[Any]] = []
        try:
            self.socket = self._make_socket(zmq.DEALER, self.control_port, 1024, 1024)
            self.transform_socket = self._make_socket(
                zmq.DEALER, self.transform_port, 2
            )
            if self.enable_sub:
                self.sub_socket = self._make_socket(
                    zmq.SUB, self.sub_port, receive_hwm=2
                )
                self.sub_socket.setsockopt(zmq.SUBSCRIBE, self.room_id.encode())
            self.connected = True
            self._started.set()
            while not self._stop.is_set():
                sent = self._flush_outgoing()
                received = self._receive_broadcasts()
                received = self._receive_control() or received
                if not sent and not received:
                    self._stop.wait(0.001)
        except Exception as exc:
            if not self._stop.is_set():
                self.connection_error = str(exc)
                self.logger.warning("Network thread failed: %s", exc)
        finally:
            self.connected = False
            self._started.set()
            for sock in self._owned_sockets:
                sock.close(linger=0)
                if self._socket_unregister:
                    self._socket_unregister(sock)
            self.socket = self.transform_socket = self.sub_socket = None

    def enqueue_control(self, room_id: str, payload: bytes) -> bool:
        if not self.connected or self._stop.is_set():
            return False
        try:
            self._control_outbox.put_nowait(
                OutboundPacket(room_id.encode(), payload, time.time())
            )
            return True
        except Full:
            self.control_queue_drops += 1
            return False

    def send_client_hello(
        self, room_id: str, device_id: str, is_stealth: bool = False
    ) -> bool:
        return self.enqueue_control(
            room_id, serialize_client_hello(device_id, is_stealth)
        )

    def send_client_variable(
        self,
        room_id: str,
        sender_client_no: int,
        device_id: str,
        var_name: str,
        value: str,
    ) -> bool:
        return self.enqueue_control(
            room_id,
            serialize_client_var_set(
                {
                    "senderClientNo": sender_client_no,
                    "deviceId": device_id,
                    "targetClientNo": sender_client_no,
                    "variableName": var_name,
                    "variableValue": value,
                }
            ),
        )

    def send_transform(self, room_id: str, transform_data: dict[str, Any]) -> bool:
        if not self.connected or self._stop.is_set():
            return False
        packet = OutboundPacket(
            room_id.encode(), serialize_client_transform(transform_data), time.time()
        )
        with self._transform_lock:
            self._latest_transform = packet
        return True

    def _try_send(self, sock: zmq.Socket[Any] | None, packet: OutboundPacket) -> bool:
        if sock is None:
            return False
        try:
            sock.send_multipart([packet.room, packet.payload], flags=zmq.DONTWAIT)
            return True
        except zmq.Again:
            self.would_block += 1
            return False

    def _flush_outgoing(self) -> bool:
        sent = 0
        now = time.time()
        while sent < self.CONTROL_DRAIN_BATCH and not self._stop.is_set():
            if self._pending_control is None:
                try:
                    self._pending_control = self._control_outbox.get_nowait()
                except Empty:
                    break
            packet = self._pending_control
            if now - packet.enqueued_at > self.CONTROL_TTL:
                self.control_expired += 1
                self._pending_control = None
                continue
            if not self._try_send(self.socket, packet):
                break
            self._pending_control = None
            sent += 1
            self.sent_control += 1
        with self._transform_lock:
            latest = self._latest_transform
        if latest is not None and self._try_send(self.transform_socket, latest):
            with self._transform_lock:
                if self._latest_transform is latest:
                    self._latest_transform = None
            self.sent_transform += 1
            return True
        return sent > 0

    def _decode(self, payload: bytes) -> ReceivedMessage | None:
        try:
            kind, data, _ = deserialize(payload)
            if data is not None:
                return kind, data
        except Exception as exc:
            # Unity isolates parse errors from socket failures.
            self.logger.debug("Skipping invalid payload: %s", exc)
        return None

    def _receive_broadcasts(self) -> bool:
        if self.sub_socket is None:
            return False
        latest: dict[bytes, bytes] = {}
        topics = (self.room_id.encode(), self.room_id.encode() + b"\0obj")
        while not self._stop.is_set():
            try:
                parts = self.sub_socket.recv_multipart(flags=zmq.DONTWAIT)
            except zmq.Again:
                break
            if len(parts) != 2 or parts[0] not in topics:
                continue
            topic, payload = parts
            if topic in latest:
                self.dropped_transform_frames += 1
            latest[topic] = payload
        for topic, payload in latest.items():
            message = self._decode(payload)
            if message is not None:
                with self._broadcast_lock:
                    self._incoming_broadcast[topic] = message
        return bool(latest)

    def _receive_control(self) -> bool:
        if self.socket is None:
            return False
        received = False
        while not self._stop.is_set():
            try:
                parts = self.socket.recv_multipart(flags=zmq.DONTWAIT)
            except zmq.Again:
                break
            received = True
            if len(parts) == 2 and parts[0] == self.room_id.encode():
                message = self._decode(parts[1])
                if message is not None:
                    self._incoming_control.put_nowait(message)
        return received

    def recv_dealer_control(self, max_drain: int = 10) -> ReceivedMessage | None:
        try:
            return self._incoming_control.get_nowait()
        except Empty:
            return None

    def recv_broadcast(self, allow_any: bool = False) -> ReceivedMessage | None:
        with self._broadcast_lock:
            for topic in list(self._incoming_broadcast):
                message = self._incoming_broadcast.pop(topic)
                if allow_any or message[0] == MSG_DEVICE_ID_MAPPING:
                    return message
        return None

    def disconnect(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=5)
            if self._thread.is_alive():
                raise RuntimeError("Simulator network thread did not stop")
            self._thread = None
        self.connected = False
        self._pending_control = None
        with self._transform_lock:
            self._latest_transform = None
        while not self._control_outbox.empty():
            self._control_outbox.get_nowait()
