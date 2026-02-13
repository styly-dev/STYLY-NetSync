#!/usr/bin/env python3
"""
STYLY NetSync Client Simulator - Load testing and behavior simulation tool.

This module simulates multiple Unity clients connecting to a STYLY NetSync server,
sending transform updates at 10Hz using the binary protocol. It's designed for
server stress testing and validation without requiring actual Unity clients.

Architecture:
    - Movement strategies: Pluggable movement patterns (Circle, Figure8, etc.)
    - Transport layer: Abstracted ZeroMQ DEALER socket communication
    - Transform generation: Separated transform data construction
    - Thread pool: Managed concurrent client simulations
    - Resource management: Proper cleanup and file descriptor limit checking
"""

import argparse
import atexit
import logging
import math
import os
import random
import subprocess

# The 'resource' module is POSIX-only (Linux/macOS). On Windows it is unavailable.
# We make it optional and skip FD-limit checks when it's not present.
try:
    import resource
except Exception:
    resource = None  # type: ignore[assignment]

import signal
import sys
import threading
import time
import types
import uuid
from abc import ABC, abstractmethod
from collections.abc import Callable
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Optional

import zmq

# Import public APIs from styly_netsync module
from styly_netsync.binary_serializer import (
    MSG_CLIENT_POSE_V2,
    MSG_CLIENT_VAR_SET,
    MSG_CLIENT_VAR_SYNC,
    MSG_DEVICE_ID_MAPPING,
    MSG_GLOBAL_VAR_SET,
    MSG_GLOBAL_VAR_SYNC,
    MSG_ROOM_POSE_V2,
    MSG_RPC,
    deserialize,
    serialize_client_transform,
    serialize_client_var_set,
)


def euler_to_quaternion(
    x_rad: float, y_rad: float, z_rad: float
) -> tuple[float, float, float, float]:
    """Convert Euler angles (radians) to a quaternion (x, y, z, w)."""
    x = x_rad
    y = y_rad
    z = z_rad

    cx = math.cos(x * 0.5)
    sx = math.sin(x * 0.5)
    cy = math.cos(y * 0.5)
    sy = math.sin(y * 0.5)
    cz = math.cos(z * 0.5)
    sz = math.sin(z * 0.5)

    qw = cx * cy * cz + sx * sy * sz
    qx = sx * cy * cz - cx * sy * sz
    qy = cx * sy * cz + sx * cy * sz
    qz = cx * cy * sz - sx * sy * cz
    return qx, qy, qz, qw


MESSAGE_TYPE_NAMES: dict[int, str] = {
    MSG_CLIENT_POSE_V2: "CLIENT_POSE_V2",
    MSG_ROOM_POSE_V2: "ROOM_POSE_V2",
    MSG_RPC: "RPC",
    MSG_DEVICE_ID_MAPPING: "DEVICE_ID_MAPPING",
    MSG_GLOBAL_VAR_SET: "GLOBAL_VAR_SET",
    MSG_GLOBAL_VAR_SYNC: "GLOBAL_VAR_SYNC",
    MSG_CLIENT_VAR_SET: "CLIENT_VAR_SET",
    MSG_CLIENT_VAR_SYNC: "CLIENT_VAR_SYNC",
}

# ============================================================================
# Data Structures and Enums
# ============================================================================


class MovementPattern(Enum):
    """Available movement patterns for simulated clients."""

    CIRCLE = "circle"
    FIGURE8 = "figure8"
    RANDOM_WALK = "random_walk"
    LINEAR_PING_PONG = "linear_ping_pong"
    SPIRAL = "spiral"


@dataclass
class Vector3:
    """3D vector for position and rotation."""

    x: float = 0.0
    y: float = 0.0
    z: float = 0.0

    def to_dict(self) -> dict[str, float]:
        """Convert to dictionary format expected by serializer."""
        return {"posX": self.x, "posY": self.y, "posZ": self.z}

    def distance_to(self, other: "Vector3") -> float:
        """Calculate distance to another vector."""
        dx = self.x - other.x
        dy = self.y - other.y
        dz = self.z - other.z
        return math.sqrt(dx * dx + dy * dy + dz * dz)


@dataclass
class Transform:
    """6DOF transform with position and rotation (Euler -> quaternion on wire)."""

    position: Vector3 = field(default_factory=Vector3)
    rotation: Vector3 = field(default_factory=Vector3)
    is_local_space: bool = False

    def to_dict(self) -> dict[str, Any]:
        """Convert to dictionary format expected by serializer."""
        qx, qy, qz, qw = euler_to_quaternion(
            self.rotation.x, self.rotation.y, self.rotation.z
        )
        return {
            "posX": self.position.x,
            "posY": self.position.y,
            "posZ": self.position.z,
            "rotX": qx,
            "rotY": qy,
            "rotZ": qz,
            "rotW": qw,
            "isLocalSpace": self.is_local_space,
        }


@dataclass
class SimulationConfig:
    """Configuration for client simulation."""

    device_id: str
    movement_pattern: MovementPattern
    start_position: Vector3
    move_speed: float = 2.0
    movement_radius: float = 3.0
    hand_swing_amplitude: float = 0.5
    hand_swing_speed: float = 3.0
    virtual_orbit_speed: float = 1.0
    virtual_orbit_radius: float = 1.0
    virtual_count: int = field(default_factory=lambda: random.randint(2, 3))


@dataclass
class ReceiveStats:
    """Lightweight bookkeeping for received broadcast messages."""

    total_messages: int = 0
    last_received_at: float = 0.0
    type_counts: dict[int, int] = field(default_factory=dict)


# ============================================================================
# Movement Strategy Pattern
# ============================================================================


class MovementStrategy(ABC):
    """Abstract base class for movement strategies."""

    def __init__(self, config: SimulationConfig):
        self.config = config
        self.start_time = time.monotonic()
        self.current_position = Vector3(
            config.start_position.x, config.start_position.y, config.start_position.z
        )
        self.previous_position = Vector3(
            config.start_position.x, config.start_position.y, config.start_position.z
        )

    @abstractmethod
    def update_position(self, elapsed_time: float, delta_time: float) -> Vector3:
        """Update and return new position based on movement pattern."""
        pass

    def calculate_rotation(self, delta_time: float) -> float:
        """Calculate Y rotation to face movement direction."""
        dx = self.current_position.x - self.previous_position.x
        dz = self.current_position.z - self.previous_position.z

        movement_magnitude = math.sqrt(dx * dx + dz * dz)
        if movement_magnitude > 0.01:
            return math.atan2(dx, dz)
        return 0.0


class CircleMovement(MovementStrategy):
    """Circular movement pattern."""

    def update_position(self, elapsed_time: float, delta_time: float) -> Vector3:
        angle = elapsed_time * self.config.move_speed
        return Vector3(
            self.config.start_position.x
            + math.cos(angle) * self.config.movement_radius,
            self.config.start_position.y,
            self.config.start_position.z
            + math.sin(angle) * self.config.movement_radius,
        )


class Figure8Movement(MovementStrategy):
    """Figure-8 movement pattern."""

    def update_position(self, elapsed_time: float, delta_time: float) -> Vector3:
        t = elapsed_time * self.config.move_speed * 0.5
        return Vector3(
            self.config.start_position.x + math.sin(t) * self.config.movement_radius,
            self.config.start_position.y,
            self.config.start_position.z
            + math.sin(2 * t) * self.config.movement_radius * 0.5,
        )


class RandomWalkMovement(MovementStrategy):
    """Random walk movement pattern."""

    def __init__(self, config: SimulationConfig):
        super().__init__(config)
        self.change_interval = 2.0
        self.walk_range = 5.0
        self.timer = 0.0
        self.target = Vector3(
            config.start_position.x, config.start_position.y, config.start_position.z
        )

    def update_position(self, elapsed_time: float, delta_time: float) -> Vector3:
        self.timer += delta_time

        if self.timer >= self.change_interval:
            self.timer = 0.0
            # Generate new random target
            angle = random.uniform(0, 2 * math.pi)
            distance = random.uniform(1.0, self.walk_range)
            self.target = Vector3(
                self.config.start_position.x + math.cos(angle) * distance,
                self.config.start_position.y,
                self.config.start_position.z + math.sin(angle) * distance,
            )

        # Move towards target
        direction = Vector3(
            self.target.x - self.current_position.x,
            0,
            self.target.z - self.current_position.z,
        )

        length = math.sqrt(direction.x**2 + direction.z**2)
        if length > 0:
            direction.x /= length
            direction.z /= length

            move_distance = self.config.move_speed * delta_time
            self.current_position.x += direction.x * move_distance
            self.current_position.z += direction.z * move_distance

        return self.current_position


class LinearPingPongMovement(MovementStrategy):
    """Linear back-and-forth movement pattern."""

    def update_position(self, elapsed_time: float, delta_time: float) -> Vector3:
        ping_pong = math.sin(elapsed_time * self.config.move_speed)
        return Vector3(
            self.config.start_position.x,
            self.config.start_position.y,
            self.config.start_position.z + ping_pong * self.config.movement_radius,
        )


class SpiralMovement(MovementStrategy):
    """Spiral movement pattern."""

    def update_position(self, elapsed_time: float, delta_time: float) -> Vector3:
        angle = elapsed_time * self.config.move_speed
        radius = (self.config.movement_radius * 0.5) + math.sin(elapsed_time * 0.5) * (
            self.config.movement_radius * 0.5
        )
        return Vector3(
            self.config.start_position.x + math.cos(angle) * radius,
            self.config.start_position.y
            + math.sin(elapsed_time * self.config.move_speed * 0.3) * 0.5,
            self.config.start_position.z + math.sin(angle) * radius,
        )


# ============================================================================
# Movement Strategy Factory
# ============================================================================


class MovementStrategyFactory:
    """Factory for creating movement strategies."""

    _strategies: dict[MovementPattern, type[MovementStrategy]] = {
        MovementPattern.CIRCLE: CircleMovement,
        MovementPattern.FIGURE8: Figure8Movement,
        MovementPattern.RANDOM_WALK: RandomWalkMovement,
        MovementPattern.LINEAR_PING_PONG: LinearPingPongMovement,
        MovementPattern.SPIRAL: SpiralMovement,
    }

    @classmethod
    def create(
        cls, pattern: MovementPattern, config: SimulationConfig
    ) -> MovementStrategy:
        """Create a movement strategy for the given pattern."""
        strategy_class = cls._strategies.get(pattern)
        if not strategy_class:
            raise ValueError(f"Unknown movement pattern: {pattern}")
        return strategy_class(config)


# ============================================================================
# Transform Builder
# ============================================================================


class TransformBuilder:
    """Builds transform data for network transmission."""

    def __init__(self, config: SimulationConfig):
        self.config = config
        self.rotation_y = 0.0
        self.rotation_speed = 5.0

        # Virtual object parameters
        self.virtual_phases = [
            i / config.virtual_count * 2 * math.pi for i in range(config.virtual_count)
        ]
        self.virtual_speeds = [
            config.virtual_orbit_speed * random.uniform(0.5, 1.5)
            for _ in range(config.virtual_count)
        ]
        self.virtual_radii = [
            config.virtual_orbit_radius * random.uniform(0.8, 1.2)
            for _ in range(config.virtual_count)
        ]

    def build_transform_data(
        self,
        position: Vector3,
        elapsed_time: float,
        delta_time: float,
        rotation_y: float,
    ) -> dict[str, Any]:
        """Build complete transform data for serialization."""

        # Update rotation smoothly
        self.rotation_y = self._smooth_rotation(self.rotation_y, rotation_y, delta_time)

        # Calculate head position (1.7m above avatar center)
        head_position = Vector3(position.x, position.y + 1.7, position.z)

        # Calculate hand positions with swing motion
        right_hand = self._calculate_hand_position(position, elapsed_time, True)
        left_hand = self._calculate_hand_position(position, elapsed_time, False)

        # Calculate virtual object positions
        virtuals = self._calculate_virtual_positions(position, elapsed_time)

        return {
            "deviceId": self.config.device_id,
            "poseSeq": 0,
            "flags": 0x3E,
            "physical": self._pose_dict(position, Vector3(0, self.rotation_y, 0), True),
            "head": self._pose_dict(
                head_position, Vector3(0, self.rotation_y, 0), False
            ),
            "rightHand": self._pose_dict(right_hand, Vector3(0, 0, 0), False),
            "leftHand": self._pose_dict(left_hand, Vector3(0, 0, 0), False),
            "virtuals": virtuals,
        }

    @staticmethod
    def _pose_dict(
        position: Vector3, rotation: Vector3, is_local_space: bool
    ) -> dict[str, Any]:
        qx, qy, qz, qw = euler_to_quaternion(rotation.x, rotation.y, rotation.z)
        return {
            "posX": position.x,
            "posY": position.y,
            "posZ": position.z,
            "rotX": qx,
            "rotY": qy,
            "rotZ": qz,
            "rotW": qw,
            "isLocalSpace": is_local_space,
        }

    def _smooth_rotation(
        self, current: float, target: float, delta_time: float
    ) -> float:
        """Smoothly interpolate rotation to avoid sudden snaps."""
        max_change = self.rotation_speed * delta_time

        # Calculate shortest angular distance
        diff = target - current

        # Normalize to [-π, π]
        while diff > math.pi:
            diff -= 2 * math.pi
        while diff < -math.pi:
            diff += 2 * math.pi

        # Apply limited rotation change
        if abs(diff) <= max_change:
            return target
        else:
            return current + max_change if diff > 0 else current - max_change

    def _calculate_hand_position(
        self, base_position: Vector3, elapsed_time: float, is_right: bool
    ) -> Vector3:
        """Calculate hand position with swing motion."""
        lateral_offset = 0.3 if is_right else -0.3
        vertical_offset = 1.2
        phase_offset = 0 if is_right else math.pi

        # Calculate swing motion
        swing_x = (
            math.sin(elapsed_time * self.config.hand_swing_speed + phase_offset)
            * self.config.hand_swing_amplitude
        )
        swing_y = (
            math.cos(elapsed_time * self.config.hand_swing_speed * 0.7 + phase_offset)
            * self.config.hand_swing_amplitude
            * 0.5
        )
        swing_z = (
            math.sin(elapsed_time * self.config.hand_swing_speed * 1.3 + phase_offset)
            * self.config.hand_swing_amplitude
            * 0.3
        )

        return Vector3(
            base_position.x + lateral_offset + swing_x,
            base_position.y + vertical_offset + swing_y,
            base_position.z + swing_z,
        )

    def _calculate_virtual_positions(
        self, base_position: Vector3, elapsed_time: float
    ) -> list[dict[str, Any]]:
        """Calculate positions for virtual objects orbiting the avatar."""
        virtuals = []
        avatar_center = Vector3(base_position.x, base_position.y + 1.0, base_position.z)

        for i in range(self.config.virtual_count):
            phase = self.virtual_phases[i] + elapsed_time * self.virtual_speeds[i]
            radius = self.virtual_radii[i]

            # 3D orbital motion
            orbit_x = math.cos(phase) * radius
            orbit_y = math.sin(phase * 2) * 0.5
            orbit_z = math.sin(phase) * radius * 0.7

            virtuals.append(
                self._pose_dict(
                    Vector3(
                        avatar_center.x + orbit_x,
                        avatar_center.y + orbit_y,
                        avatar_center.z + orbit_z,
                    ),
                    Vector3(0, phase, 0),
                    False,
                )
            )

        return virtuals


# ============================================================================
# Transport Layer
# ============================================================================


class NetworkTransport:
    """Handles ZeroMQ network communication (DEALER for send, optional SUB for recv)."""

    def __init__(
        self,
        context: zmq.Context,
        server_addr: str,
        dealer_port: int,
        sub_port: int,
        room_id: str,
        enable_sub: bool = True,
        socket_register: Callable[[zmq.Socket], None] | None = None,
        socket_unregister: Callable[[zmq.Socket], None] | None = None,
    ):
        self.context = context
        self.server_addr = server_addr
        self.dealer_port = dealer_port
        self.sub_port = sub_port
        self.room_id = room_id
        self.enable_sub = enable_sub
        self.socket: zmq.Socket | None = None
        self.sub_socket: zmq.Socket[Any] | None = None
        self.logger = logging.getLogger(self.__class__.__name__)
        self._socket_register = socket_register
        self._socket_unregister = socket_unregister

    def _register_socket(self, socket: zmq.Socket[Any]) -> None:
        if self._socket_register:
            try:
                self._socket_register(socket)
            except Exception:
                pass

    def _unregister_socket(self, socket: zmq.Socket[Any]) -> None:
        if self._socket_unregister:
            try:
                self._socket_unregister(socket)
            except Exception:
                pass

    def connect(self) -> bool:
        """Establish connection to server (both DEALER and SUB)."""
        try:
            # DEALER for sending and control message receive
            # Low HWM (10) for backpressure detection matching Unity client
            self.socket = self.context.socket(zmq.DEALER)
            self._register_socket(self.socket)
            self.socket.setsockopt(zmq.LINGER, 0)
            self.socket.setsockopt(zmq.IMMEDIATE, 1)
            self.socket.setsockopt(zmq.RCVTIMEO, 0)  # Non-blocking receive
            self.socket.setsockopt(zmq.SNDTIMEO, 1000)
            self.socket.setsockopt(zmq.SNDHWM, 10)  # Low HWM for backpressure

            dealer_endpoint = f"{self.server_addr}:{self.dealer_port}"
            self.socket.connect(dealer_endpoint)
            self.logger.debug(f"Connected DEALER to {dealer_endpoint}")

            # Optional SUB for receiving broadcasts (ID mappings, NV syncs, etc.)
            # Low RCVHWM (2) to prefer recent updates and drop stale messages
            if self.enable_sub:
                try:
                    self.sub_socket = self.context.socket(zmq.SUB)
                    self._register_socket(self.sub_socket)
                    self.sub_socket.setsockopt(zmq.LINGER, 0)
                    self.sub_socket.setsockopt(zmq.RCVTIMEO, 10)  # non-blocking-ish
                    self.sub_socket.setsockopt(
                        zmq.RCVHWM, 2
                    )  # Low HWM to prefer recent
                    sub_endpoint = f"{self.server_addr}:{self.sub_port}"
                    self.sub_socket.connect(sub_endpoint)
                    # Subscribe to room topic
                    self.sub_socket.setsockopt(
                        zmq.SUBSCRIBE, self.room_id.encode("utf-8")
                    )
                    self.logger.debug(
                        f"Connected SUB to {sub_endpoint} topic '{self.room_id}'"
                    )
                except Exception as se:
                    # SUB is optional; ensure any partially created socket is closed
                    try:
                        if self.sub_socket:
                            self.sub_socket.close()
                            self._unregister_socket(self.sub_socket)
                    except Exception:
                        pass
                    finally:
                        self.sub_socket = None
                    self.logger.warning(f"Failed to setup SUB socket: {se}")

            return True
        except Exception as e:
            # Ensure any partially created sockets are closed to avoid FD leaks
            try:
                self.disconnect()
            except Exception:
                pass
            self.logger.error(f"Failed to connect: {e}")
            return False

    def send_transform(self, room_id: str, transform_data: dict[str, Any]) -> bool:
        """Send transform data to server."""
        if not self.socket:
            return False

        try:
            binary_data = serialize_client_transform(transform_data)
            self.socket.send_multipart(
                [
                    room_id.encode("utf-8"),
                    binary_data,
                ],
                flags=zmq.NOBLOCK,
            )
            return True
        except zmq.Again:
            # Drop when socket is not ready to avoid blocking under congestion.
            return False
        except Exception as e:
            self.logger.error(f"Failed to send transform: {e}")
            return False

    def send_client_variable(
        self, room_id: str, sender_client_no: int, var_name: str, value: str
    ) -> bool:
        """Send client variable data to server."""
        if not self.socket:
            return False

        try:
            var_data = {
                "senderClientNo": sender_client_no,
                "targetClientNo": sender_client_no,  # Set variable for ourselves
                "variableName": var_name,
                "variableValue": value,
                "timestamp": time.time(),
            }
            binary_data = serialize_client_var_set(var_data)
            self.socket.send_multipart(
                [
                    room_id.encode("utf-8"),
                    binary_data,
                ],
                flags=zmq.NOBLOCK,
            )
            return True
        except zmq.Again:
            # Drop when socket is not ready to avoid blocking under congestion.
            return False
        except Exception as e:
            self.logger.error(f"Failed to send client variable: {e}")
            return False

    def recv_broadcast(
        self, allow_any: bool = False
    ) -> tuple[int, dict[str, Any] | None] | None:
        """Drain incoming broadcasts and optionally surface non-mapping payloads.

        Args:
            allow_any: When True, return the first successfully deserialized
                message regardless of type. When False, only device-ID mapping
                messages are returned.

        Returns:
            Tuple of (message_type, data) when a relevant payload is available,
            otherwise None. Data may be None when deserialization fails.
        """
        if not self.sub_socket:
            return None
        scans = 0
        MAX_SCANS = 50  # drain up to 50 messages per tick
        while scans < MAX_SCANS:
            scans += 1
            try:
                parts = self.sub_socket.recv_multipart(flags=zmq.NOBLOCK)
            except zmq.Again:
                return None
            except Exception as e:
                self.logger.debug(f"SUB receive error: {e}")
                return None

            if len(parts) != 2:
                continue
            topic, payload = parts
            if topic.decode("utf-8", errors="ignore") != self.room_id:
                continue

            # Fast-path: inspect message type byte without full deserialize
            try:
                if not payload:
                    continue
                msg_type = payload[0]
            except Exception:
                continue
            if msg_type == MSG_DEVICE_ID_MAPPING:
                try:
                    _, data, _ = deserialize(payload)
                    if data is not None:
                        return msg_type, data
                except Exception:
                    # ignore malformed
                    continue
                if allow_any:
                    return msg_type, None

            if allow_any:
                # When measuring receive throughput we only need message type,
                # not the full payload. Returning None keeps CPU usage low.
                return msg_type, None
            # For other message types, drop and continue scanning
        return None

    def recv_dealer_control(
        self, max_drain: int = 10
    ) -> tuple[int, dict[str, Any] | None] | None:
        """Receive control messages from DEALER socket (sent via ROUTER unicast).

        Server sends RPC, NV sync, and ID mappings via ROUTER->DEALER for reliable
        delivery instead of PUB/SUB. This method drains incoming control messages.

        Args:
            max_drain: Maximum number of messages to drain per call.

        Returns:
            Tuple of (message_type, data) for the first relevant message, or None.
            Returns immediately on first valid message or after draining max_drain
            messages without finding a valid one.
        """
        if not self.socket:
            return None

        for _ in range(max_drain):
            try:
                parts = self.socket.recv_multipart(flags=zmq.NOBLOCK)
            except zmq.Again:
                # No more messages available
                return None
            except zmq.ZMQError as e:
                # Terminal errors: stop draining
                if e.errno in (zmq.ETERM, zmq.ENOTSOCK):
                    self.logger.debug(f"DEALER socket error: {e}")
                    return None
                self.logger.debug(f"Non-fatal DEALER receive error: {e}")
                continue
            except Exception as e:
                self.logger.debug(f"DEALER receive error: {e}")
                # Continue to next iteration rather than returning early
                continue

            if len(parts) != 2:
                continue

            dealer_room_id, payload = parts
            room_id_str = dealer_room_id.decode("utf-8", errors="ignore")

            # Only process messages for our room
            if room_id_str != self.room_id:
                continue

            try:
                if not payload:
                    continue
                msg_type = payload[0]
                _, data, _ = deserialize(payload)
                return msg_type, data
            except Exception:
                # Malformed payload; skip and continue draining
                continue

        # Drained max_drain messages without finding a valid one
        return None

    def disconnect(self) -> None:
        """Close connection to server."""
        if self.socket:
            try:
                self.socket.close()
            finally:
                self._unregister_socket(self.socket)
            self.socket = None
        if self.sub_socket:
            try:
                self.sub_socket.close()
            finally:
                self._unregister_socket(self.sub_socket)
            self.sub_socket = None


# ============================================================================
# Shared Subscriber (Single SUB socket for all clients)
# ============================================================================


class SharedSubscriber:
    """Single SUB socket that receives broadcasts and shares mappings.

    IMPORTANT: As of PR #316, the intended primary path for ID mappings is
    ROUTER/DEALER unicast instead of PUB/SUB. Each SimulatedClient must poll
    its DEALER socket for control messages (including ID mappings) via
    _poll_dealer_control().

    However, for backwards compatibility with older servers and mixed
    deployments, ID mapping messages (MSG_DEVICE_ID_MAPPING) may still be
    delivered via PUB/SUB, and this simulator continues to support mappings
    received on the shared SUB socket. In practice, ID mappings can therefore
    arrive via DEALER, SUB, or both, depending on server behavior.

    SharedSubscriber is kept for backward compatibility and may still receive
    transform broadcasts via SUB; new features should prefer the DEALER-based
    control/mapping channel.
    """

    def __init__(
        self,
        context: zmq.Context[Any],
        server_addr: str,
        sub_port: int,
        room_id: str,
        socket_register: Callable[[zmq.Socket[Any]], None] | None = None,
        socket_unregister: Callable[[zmq.Socket[Any]], None] | None = None,
    ):
        self.context = context
        self.server_addr = server_addr
        self.sub_port = sub_port
        self.room_id = room_id
        self.logger = logging.getLogger(self.__class__.__name__)
        self.socket: zmq.Socket[Any] | None = None
        self._thread: threading.Thread | None = None
        self._running = False
        self._lock = threading.Lock()
        self._device_to_client: dict[str, int] = {}
        self._socket_register = socket_register
        self._socket_unregister = socket_unregister

    def _register_socket(self, socket: zmq.Socket[Any]) -> None:
        if self._socket_register:
            try:
                self._socket_register(socket)
            except Exception:
                pass

    def _unregister_socket(self, socket: zmq.Socket[Any]) -> None:
        if self._socket_unregister:
            try:
                self._socket_unregister(socket)
            except Exception:
                pass

    def start(self) -> bool:
        try:
            self.socket = self.context.socket(zmq.SUB)
            self._register_socket(self.socket)
            self.socket.setsockopt(zmq.LINGER, 0)
            self.socket.setsockopt(zmq.RCVTIMEO, 10)
            endpoint = f"{self.server_addr}:{self.sub_port}"
            self.socket.connect(endpoint)
            self.socket.setsockopt(zmq.SUBSCRIBE, self.room_id.encode("utf-8"))
            self._running = True
            self._thread = threading.Thread(
                target=self._loop, name="SharedSubscriber", daemon=True
            )
            self._thread.start()
            self.logger.debug(
                f"Shared SUB connected to {endpoint} topic '{self.room_id}'"
            )
            return True
        except Exception as e:
            # Ensure any partially created socket is closed
            if self.socket is not None:
                try:
                    self.socket.close()
                    self._unregister_socket(self.socket)
                except Exception:
                    pass
                finally:
                    self.socket = None
            self.logger.error(f"Failed to start SharedSubscriber: {e}")
            return False

    def stop(self) -> None:
        self._running = False
        if self._thread and self._thread.is_alive():
            try:
                self._thread.join(timeout=1.0)
            except Exception:
                pass
        if self.socket:
            try:
                self.socket.close()
            except Exception:
                pass
            finally:
                self._unregister_socket(self.socket)
            self.socket = None

    def is_running(self) -> bool:
        return self._running

    def get_client_no(self, device_id: str) -> int:
        with self._lock:
            return self._device_to_client.get(device_id, 0)

    def _loop(self) -> None:
        # Drain many messages each tick to avoid backlog
        while self._running:
            if self.socket is None:
                break
            drained_any = False
            for _ in range(200):  # aggressive drain
                try:
                    parts = self.socket.recv_multipart(flags=zmq.NOBLOCK)
                except zmq.Again:
                    break
                except Exception as e:
                    self.logger.debug(f"Shared SUB recv error: {e}")
                    break

                drained_any = True
                if len(parts) != 2:
                    continue
                topic, payload = parts
                if topic.decode("utf-8", errors="ignore") != self.room_id:
                    continue
                try:
                    if not payload:
                        continue
                    msg_type = payload[0]
                    if msg_type != MSG_DEVICE_ID_MAPPING:
                        continue
                    _, data, _ = deserialize(payload)
                    if data is None:
                        continue
                    mappings = data.get("mappings", [])
                    if mappings:
                        with self._lock:
                            for m in mappings:
                                did = m.get("deviceId")
                                cno = int(m.get("clientNo", 0))
                                if did and cno:
                                    self._device_to_client[did] = cno
                except Exception:
                    # Ignore malformed
                    continue

            if not drained_any:
                # Small sleep when idle
                time.sleep(0.005)


# ============================================================================
# Simulated Client
# ============================================================================


class SimulatedClient:
    """Represents a single simulated client."""

    # Battery simulation constants
    BATTERY_INITIAL_MIN = 0.5  # Normalized (0.0-1.0)
    BATTERY_INITIAL_MAX = 1.0  # Normalized (0.0-1.0)
    BATTERY_DRAIN_RATE = 0.1 / 60.0  # Normalized units lost per second (0.1/min)
    BATTERY_UPDATE_INTERVAL = 60.0  # Send battery updates every 60 seconds

    def __init__(
        self,
        config: SimulationConfig,
        transport: NetworkTransport,
        room_id: str,
        shared_subscriber: Optional["SharedSubscriber"] = None,
        simulate_battery: bool = True,
        enable_receive: bool = True,
        transform_send_rate: float = 10.0,
    ):
        self.config = config
        self.transport = transport
        self.room_id = room_id
        self.movement = MovementStrategyFactory.create(config.movement_pattern, config)
        self.transform_builder = TransformBuilder(config)
        self.logger = logging.getLogger(f"Client-{config.device_id[-8:]}")
        self.shared_subscriber = shared_subscriber
        self.simulate_battery = simulate_battery
        self.enable_receive = enable_receive
        self.transform_send_rate = transform_send_rate
        self.recv_stats: ReceiveStats | None = (
            ReceiveStats() if enable_receive else None
        )

        self.start_time = time.monotonic()
        self.last_update_time = self.start_time
        self.running = False

        # Battery simulation state (only initialized if enabled)
        if self.simulate_battery:
            self.battery_level = random.uniform(
                self.BATTERY_INITIAL_MIN, self.BATTERY_INITIAL_MAX
            )
            self.last_battery_update = self.start_time
            # Force initial battery send as soon as client number is known
            self.last_battery_send = self.start_time - self.BATTERY_UPDATE_INTERVAL
            # Client number assigned by server after handshake (0 until assigned)
            self.client_number = 0

            self.logger.info(
                f"Initialized with battery level: {self.battery_level:.2f} (normalized), awaiting client number assignment"
            )
        else:
            self.battery_level = 0.0
            self.last_battery_update = self.start_time
            self.last_battery_send = self.start_time
            self.client_number = 0
            self.logger.info("Battery simulation disabled")

    def run(self, stop_event: threading.Event) -> None:
        """Run the client simulation loop."""
        if not self.transport.connect():
            self.logger.error("Failed to connect to server")
            return

        self.running = True
        update_interval = 1.0 / self.transform_send_rate  # Calculate interval from rate
        last_send_time = time.monotonic()

        try:
            while self.running and not stop_event.is_set():
                current_time = time.monotonic()

                if current_time - last_send_time >= update_interval:
                    elapsed_time = current_time - self.start_time
                    delta_time = current_time - self.last_update_time

                    # Update movement
                    self.movement.previous_position = self.movement.current_position
                    new_position = self.movement.update_position(
                        elapsed_time, delta_time
                    )
                    self.movement.current_position = new_position

                    # Calculate rotation
                    rotation_y = self.movement.calculate_rotation(delta_time)

                    # Build transform data
                    transform_data = self.transform_builder.build_transform_data(
                        new_position, elapsed_time, delta_time, rotation_y
                    )

                    # Send to server
                    if self.transport.send_transform(self.room_id, transform_data):
                        self.logger.debug(
                            f"Sent update: pos=({new_position.x:.2f}, {new_position.z:.2f})"
                        )

                    # Poll incoming broadcasts (ID mappings, etc.) to learn our client number first
                    self._poll_broadcasts()

                    # Update battery simulation (may send immediately after client no assigned)
                    if self.simulate_battery:
                        self._update_battery(current_time)

                    self.last_update_time = current_time
                    last_send_time = current_time

                # Opportunistically drain broadcasts more frequently than send rate
                # to minimize assignment latency and queue buildup
                self._poll_broadcasts()

                # Small sleep to prevent busy waiting
                time.sleep(0.01)

        except Exception as e:
            self.logger.error(f"Error in simulation loop: {e}")
        finally:
            self.running = False
            self.transport.disconnect()
            if self.simulate_battery:
                self.logger.info(
                    f"Client stopped with final battery level: {self.battery_level:.2f} (normalized)"
                )
            else:
                self.logger.info("Client stopped")
            if self.enable_receive and self.recv_stats:
                if self.recv_stats.total_messages > 0:
                    breakdown = ", ".join(
                        f"{MESSAGE_TYPE_NAMES.get(t, t)}:{c}"
                        for t, c in sorted(self.recv_stats.type_counts.items())
                    )
                    self.logger.info(
                        "Received %d broadcasts (%s)",
                        self.recv_stats.total_messages,
                        breakdown,
                    )
                else:
                    self.logger.info("No broadcasts received")

    def _update_battery(self, current_time: float) -> None:
        """Update battery level and send updates when needed."""
        # Update battery drain
        elapsed_since_battery_update = current_time - self.last_battery_update
        if elapsed_since_battery_update > 0:
            self.battery_level = max(
                0.0,
                self.battery_level
                - (elapsed_since_battery_update * self.BATTERY_DRAIN_RATE),
            )
            self.last_battery_update = current_time

        # Send battery update periodically
        elapsed_since_send = current_time - self.last_battery_send
        if (
            elapsed_since_send >= self.BATTERY_UPDATE_INTERVAL
            and self.client_number > 0
        ):
            battery_value = f"{self.battery_level:.2f}"
            if self.transport.send_client_variable(
                self.room_id, self.client_number, "BatteryLevel", battery_value
            ):
                self.logger.debug(f"Sent battery level: {battery_value} (normalized)")
            else:
                self.logger.warning("Failed to send battery level update")
            self.last_battery_send = current_time

    def _poll_broadcasts(self) -> None:
        """Update local client number from shared subscriber, DEALER, or fallback SUB."""
        # Prefer shared subscriber for scalability
        if self.shared_subscriber:
            assigned = self.shared_subscriber.get_client_no(self.config.device_id)
            if assigned and assigned != self.client_number:
                self.client_number = assigned
                self.logger.info(
                    f"Assigned client number by server: {self.client_number}"
                )
            # Still check DEALER for control messages even when using shared subscriber
            self._poll_dealer_control()
            return

        # Fallback: use own transport SUB and DEALER if available
        if not self.transport:
            return

        # Drain multiple messages per poll, but respect a strict time budget to avoid starvation.
        max_drains = 100 if self.enable_receive else 10
        drained = 0
        assigned_this_poll = False

        # Time budget (nanoseconds)
        # Higher budget during initial client number assignment, lower during normal operation
        budget_ns = 5_000_000 if self.client_number == 0 else 1_000_000  # 5ms / 1ms
        t0 = time.perf_counter_ns()

        # First, poll DEALER for control messages (RPC, NV, ID mapping via ROUTER unicast)
        while drained < max_drains and (time.perf_counter_ns() - t0) < budget_ns:
            msg = self.transport.recv_dealer_control(max_drain=1)
            if not msg:
                break

            drained += 1
            msg_type, data = msg
            assigned_this_poll = self._process_mapping_message(
                msg_type, data, assigned_this_poll
            )

            if self.enable_receive:
                self._handle_broadcast_payload(msg_type, data)

            if assigned_this_poll and not self.enable_receive:
                break

        # Then poll SUB for transform broadcasts
        while drained < max_drains and (time.perf_counter_ns() - t0) < budget_ns:
            msg = self.transport.recv_broadcast(allow_any=self.enable_receive)
            if not msg:
                break

            drained += 1
            msg_type, data = msg
            assigned_this_poll = self._process_mapping_message(
                msg_type, data, assigned_this_poll
            )

            if self.enable_receive:
                self._handle_broadcast_payload(msg_type, data)

            # Once assignment is confirmed we can stop early if we're not
            # actively measuring receive throughput.
            if assigned_this_poll and not self.enable_receive:
                break

        # NOTE: Time budget may interrupt draining, but subsequent ticks will continue
        # processing messages. This prevents send loop starvation.

    def _poll_dealer_control(self) -> None:
        """Poll DEALER socket for control messages when using shared subscriber."""
        if not self.transport:
            return

        for _ in range(10):  # Drain up to 10 control messages
            msg = self.transport.recv_dealer_control(max_drain=1)
            if not msg:
                break

            msg_type, data = msg
            self._process_mapping_message(msg_type, data, False)

            if self.enable_receive:
                self._handle_broadcast_payload(msg_type, data)

    def _process_mapping_message(
        self, msg_type: int, data: dict[str, Any] | None, already_assigned: bool
    ) -> bool:
        """Process device ID mapping message and return True if assignment occurred."""
        if msg_type != MSG_DEVICE_ID_MAPPING:
            return already_assigned

        try:
            mappings = data.get("mappings", []) if data else []
            for mapping in mappings:
                if mapping.get("deviceId") == self.config.device_id:
                    assigned = int(mapping.get("clientNo", 0))
                    if assigned and assigned != self.client_number:
                        self.client_number = assigned
                        self.logger.info(
                            f"Assigned client number by server: {self.client_number}"
                        )
                        return True
                    break
        except Exception as exc:
            self.logger.debug(f"Failed to process mapping: {exc}")

        return already_assigned

    def _handle_broadcast_payload(
        self, msg_type: int, data: dict[str, Any] | None
    ) -> None:
        """Record broadcast metadata for receive-mode load tests."""
        if not self.recv_stats:
            return

        now = time.monotonic()
        self.recv_stats.total_messages += 1
        self.recv_stats.last_received_at = now
        self.recv_stats.type_counts[msg_type] = (
            self.recv_stats.type_counts.get(msg_type, 0) + 1
        )

        if self.logger.isEnabledFor(logging.DEBUG):
            message_name = MESSAGE_TYPE_NAMES.get(msg_type, f"UNKNOWN_{msg_type}")
            summary = "payload"
            if msg_type == MSG_CLIENT_POSE_V2 and data:
                summary = f"transform from {data.get('deviceId', 'unknown')}"
            elif msg_type == MSG_ROOM_POSE_V2 and data:
                summary = f"room transform with {len(data.get('clients', []))} clients"
            elif msg_type in (MSG_GLOBAL_VAR_SET, MSG_CLIENT_VAR_SET) and data:
                summary = (
                    f"var '{data.get('variableName', 'unknown')}'"
                    if "variableName" in data
                    else summary
                )
            elif data is None:
                summary = "payload omitted"
            self.logger.debug("Received broadcast type %s (%s)", message_name, summary)


# ============================================================================
# Thread Pool Manager
# ============================================================================


class ClientThreadPool:
    """Manages a pool of client simulation threads."""

    def __init__(self, max_clients: int):
        self.max_clients = max_clients
        self.threads: list[threading.Thread] = []
        self.clients: list[SimulatedClient] = []
        self.stop_event = threading.Event()
        self.logger = logging.getLogger(self.__class__.__name__)

    def add_client(self, client: SimulatedClient) -> bool:
        """Add a client to the pool and start its thread."""
        if len(self.threads) >= self.max_clients:
            self.logger.warning("Thread pool is full")
            return False

        thread = threading.Thread(
            target=client.run, args=(self.stop_event,), daemon=False
        )

        try:
            thread.start()
            self.threads.append(thread)
            self.clients.append(client)
            return True
        except Exception as e:
            self.logger.error(f"Failed to start client thread: {e}")
            return False

    def stop_all(self, timeout: float = 2.0) -> None:
        """Stop all client threads."""
        self.logger.info("Stopping all client threads...")
        self.stop_event.set()

        for i, thread in enumerate(self.threads):
            if thread.is_alive():
                thread.join(timeout)
                if thread.is_alive():
                    self.logger.warning(f"Thread {i} did not stop within timeout")

        self.threads.clear()
        self.clients.clear()
        self.logger.info("All threads stopped")

    def get_active_count(self) -> int:
        """Get the number of active threads."""
        return sum(1 for t in self.threads if t.is_alive())


# ============================================================================
# Resource Manager
# ============================================================================


class ResourceManager:
    """Manages system resources and limits."""

    # File descriptor overhead for ZMQ context, internal signaling, logs, timers, pollers, etc.
    FD_BASE_OVERHEAD = 64

    # Default recommended file descriptor limit for most systems
    DEFAULT_FD_LIMIT = 4096

    @staticmethod
    def _has_resource() -> bool:
        return resource is not None

    @staticmethod
    def estimate_fd_need(num_clients: int) -> int:
        """Estimate file descriptors needed for given clients.

        Notes:
        - Each client uses a DEALER socket (~1-2 FDs depending on platform/libzmq)
        - We use a shared SUB socket for broadcasts (+1 FD)
        - ZMQ context and internal signaling use a few FDs as well
        - Leave generous headroom for logs, timers, pollers, etc.
        """
        # Assume roughly 2 FDs per client for safety, then add overhead
        return num_clients * 2 + ResourceManager.FD_BASE_OVERHEAD

    @staticmethod
    def check_file_descriptor_limit(required_fds: int) -> tuple[bool, str]:
        """Check if system can handle the required number of file descriptors."""
        if not ResourceManager._has_resource():
            return (
                True,
                "Skipping FD limit check: 'resource' module not available on this platform.",
            )

        try:
            soft_limit, hard_limit = resource.getrlimit(resource.RLIMIT_NOFILE)

            if required_fds > int(soft_limit * 0.8):  # Warn at 80% of limit
                message = (
                    "WARNING: Simulation may exceed open file limits.\n"
                    f"Estimated FDs needed: {required_fds}, Soft limit: {soft_limit}, Hard limit: {hard_limit}\n"
                    "Consider increasing with: ulimit -n 4096 (shell)"
                )
                return False, message

            return True, f"System FD limits OK (soft={soft_limit}, hard={hard_limit})"

        except Exception as e:
            return True, f"Could not check system limits: {e}"

    @staticmethod
    def try_raise_fd_limit(min_required: int) -> tuple[bool, str]:
        """Attempt to raise the soft RLIMIT_NOFILE to accommodate simulation.

        Returns (success, message). If unsuccessful, returns reason in message.
        """
        if not ResourceManager._has_resource():
            return (
                False,
                "Cannot raise FD limit: 'resource' module not available on this platform.",
            )

        try:
            soft_limit, hard_limit = resource.getrlimit(resource.RLIMIT_NOFILE)
            if soft_limit >= min_required:
                return (
                    True,
                    f"FD limit sufficient (soft={soft_limit}, hard={hard_limit})",
                )

            # Aim for either hard limit or a sane default, whichever is smaller
            desired = max(min_required, ResourceManager.DEFAULT_FD_LIMIT)

            new_soft = min(desired, hard_limit)

            # If the new soft limit still doesn't meet minimum requirements, we can't help
            if new_soft < min_required:
                return (
                    False,
                    f"Cannot raise FD limit to meet requirement {min_required} (max possible={new_soft}, hard={hard_limit})",
                )

            # Only raise the soft limit, never lower it
            if new_soft > soft_limit:
                resource.setrlimit(resource.RLIMIT_NOFILE, (new_soft, hard_limit))
                return (
                    True,
                    f"Raised soft FD limit: {soft_limit} -> {new_soft} (hard={hard_limit})",
                )
            else:
                return (
                    True,
                    f"FD limit sufficient (soft={soft_limit}, hard={hard_limit})",
                )

        except Exception as e:
            return False, f"Failed to raise FD limit: {e}"

    @staticmethod
    def bump_fd_soft_limit(target: int, logger: logging.Logger) -> None:
        """Best-effort bump of RLIMIT_NOFILE to target within hard limit."""
        if not ResourceManager._has_resource():
            logger.info(
                "Skipping FD soft limit bump: 'resource' module not available on this platform."
            )
            return

        try:
            soft, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
            new_soft = min(max(soft, target), hard)
            if new_soft != soft:
                resource.setrlimit(resource.RLIMIT_NOFILE, (new_soft, hard))
                logger.info(
                    "Raised RLIMIT_NOFILE: %d -> %d (hard=%d)", soft, new_soft, hard
                )
        except Exception as exc:
            logger.warning("Failed to raise RLIMIT_NOFILE: %s", exc)

    @staticmethod
    def cleanup_ipc_socket(server_addr: str, logger: logging.Logger) -> None:
        """Remove stale ipc:// socket files if present."""
        if not server_addr.startswith("ipc://"):
            return

        path = server_addr[len("ipc://") :]
        try:
            if os.path.exists(path):
                os.unlink(path)
                logger.info("Removed stale ipc socket: %s", path)
        except Exception as exc:
            logger.warning("Failed to remove ipc socket %s: %s", path, exc)

    @staticmethod
    def log_port_occupancy(
        server_addr: str, dealer_port: int, logger: logging.Logger
    ) -> None:
        """Log processes listening on the dealer port to surface conflicts early."""
        if not server_addr.startswith("tcp://"):
            return

        try:
            result = subprocess.run(
                ["lsof", "-nP", f"-iTCP:{dealer_port}", "-sTCP:LISTEN"],
                capture_output=True,
                text=True,
                timeout=5,
            )
            if result.returncode == 0 and result.stdout.strip():
                logger.info(
                    "Port %d is already in LISTEN state:\n%s",
                    dealer_port,
                    result.stdout,
                )
        except Exception as exc:
            # Best-effort check only; log at debug to avoid noise when lsof is missing.
            logger.debug("Port occupancy check failed: %s", exc)

    @staticmethod
    def compute_max_clients_for_soft_limit(soft_limit: int) -> int:
        """Compute a conservative maximum client count for a given soft FD limit.

        Uses the same headroom heuristic as checks (80%) and assumes ~2 FDs/client.
        """
        try:
            budget = int(soft_limit * 0.8) - ResourceManager.FD_BASE_OVERHEAD
            if budget <= 0:
                return 0
            return max(0, budget // 2)
        except Exception:
            # If anything goes wrong, be conservative
            return 0

    @staticmethod
    def setup_signal_handlers(handler: Callable[[int, Any], None]) -> None:
        """Setup signal handlers for clean shutdown."""
        try:
            signal.signal(signal.SIGINT, handler)
        except Exception:
            pass
        if hasattr(signal, "SIGTERM"):
            try:
                signal.signal(signal.SIGTERM, handler)
            except Exception:
                pass
        if hasattr(signal, "SIGBREAK"):
            try:
                signal.signal(signal.SIGBREAK, handler)
            except Exception:
                pass


# ============================================================================
# Main Simulator Orchestrator
# ============================================================================


class ClientSimulator:
    """Main orchestrator for client simulation."""

    def __init__(
        self,
        server_addr: str,
        dealer_port: int,
        sub_port: int,  # Kept for future use
        room_id: str,
        num_clients: int,
        spawn_batch_size: int | None = None,
        spawn_batch_interval: float | None = None,
        simulate_battery: bool = True,
        enable_receive: bool = True,
        transform_send_rate: float = 10.0,
    ):
        self.server_addr = server_addr
        self.dealer_port = dealer_port
        self.sub_port = sub_port  # Future use
        self.room_id = room_id
        self.num_clients = num_clients
        self.spawn_batch_size = spawn_batch_size if spawn_batch_size is not None else 0
        self.spawn_batch_interval = (
            spawn_batch_interval if spawn_batch_interval is not None else 0.0
        )
        self.simulate_battery = simulate_battery
        self.enable_receive = enable_receive
        self.transform_send_rate = transform_send_rate

        self.context = zmq.Context(io_threads=1)
        self.thread_pool = ClientThreadPool(num_clients)
        self.running = False
        self._stop_requested = threading.Event()
        self._stop_lock = threading.Lock()
        self._stopped = False
        self.logger = logging.getLogger(self.__class__.__name__)
        self.shared_subscriber: SharedSubscriber | None = None
        self._sockets: set[zmq.Socket[Any]] = set()
        self._sockets_lock = threading.Lock()

        # Startup preflight checks and cleanup
        self._preflight_checks()

        # Setup signal handlers
        ResourceManager.setup_signal_handlers(self._signal_handler)

        # Best-effort cleanup on interpreter exit
        atexit.register(self._atexit_cleanup)

    def _preflight_checks(self) -> None:
        """Run startup checks to avoid stale resources and port conflicts."""
        ResourceManager.bump_fd_soft_limit(
            ResourceManager.DEFAULT_FD_LIMIT, self.logger
        )
        ResourceManager.cleanup_ipc_socket(self.server_addr, self.logger)
        ResourceManager.log_port_occupancy(
            self.server_addr, self.dealer_port, self.logger
        )

    def _register_socket(self, socket: zmq.Socket[Any]) -> None:
        with self._sockets_lock:
            self._sockets.add(socket)

    def _unregister_socket(self, socket: zmq.Socket[Any]) -> None:
        with self._sockets_lock:
            self._sockets.discard(socket)

    def _cleanup_tracked_sockets(self) -> None:
        with self._sockets_lock:
            for socket in list(self._sockets):
                try:
                    socket.close()
                except Exception:
                    pass
            self._sockets.clear()

    def _signal_handler(self, signum: int, frame: types.FrameType | None) -> None:
        """Handle interrupt signals."""
        self.logger.info(f"\nReceived signal {signum}, requesting stop...")
        self.request_stop()
        self.stop()

    def request_stop(self) -> None:
        """Request shutdown from non-blocking contexts (signal handlers, etc.)."""
        self._stop_requested.set()
        self.running = False

    def _atexit_cleanup(self) -> None:
        """Best-effort cleanup at process exit."""
        try:
            self.request_stop()
            self.stop()
        except Exception:
            pass
        finally:
            try:
                self._cleanup_tracked_sockets()
            except Exception:
                pass
            try:
                if not getattr(self.context, "closed", False):
                    self.context.term()
            except Exception:
                pass

    def start(self) -> None:
        """Start the simulation."""
        self.running = True

        # Estimate FD usage and try to ensure limits
        estimated_fds = ResourceManager.estimate_fd_need(self.num_clients)
        ok_before, msg_before = ResourceManager.check_file_descriptor_limit(
            estimated_fds
        )
        if not ok_before:
            self.logger.warning(msg_before)
            ok_raise, msg_raise = ResourceManager.try_raise_fd_limit(estimated_fds)
            if ok_raise:
                self.logger.info(msg_raise)
                # Re-check to inform final status
                _, msg_after = ResourceManager.check_file_descriptor_limit(
                    estimated_fds
                )
                self.logger.info(msg_after)
            else:
                self.logger.warning(msg_raise)
        else:
            self.logger.info(msg_before)

        # Determine safe maximum clients based on current soft limit (POSIX only)
        if ResourceManager._has_resource():
            try:
                soft_limit, hard_limit = resource.getrlimit(resource.RLIMIT_NOFILE)
                safe_max_clients = ResourceManager.compute_max_clients_for_soft_limit(
                    soft_limit
                )
                if self.num_clients > safe_max_clients:
                    self.logger.warning(
                        "Reducing client count to %d based on FD limit (soft=%d, hard=%d)",
                        safe_max_clients,
                        soft_limit,
                        hard_limit,
                    )
                    self.num_clients = safe_max_clients
                    # Keep thread pool cap in sync
                    self.thread_pool.max_clients = self.num_clients
            except Exception as e:
                self.logger.warning("FD limit check failed: %s", e)
        else:
            self.logger.info(
                "Skipping FD-based client capping: 'resource' module not available on this platform."
            )

        self.logger.info(f"Starting simulation with {self.num_clients} clients")
        self.logger.info(f"Server: {self.server_addr}:{self.dealer_port}")
        self.logger.info(f"Room: {self.room_id}")
        self.logger.info(
            f"Battery simulation: {'enabled' if self.simulate_battery else 'disabled'}"
        )
        self.logger.info(
            f"Broadcast receive mode: {'enabled' if self.enable_receive else 'disabled'}"
        )
        if self.spawn_batch_size > 0 and self.spawn_batch_interval > 0:
            self.logger.info(
                f"Spawning clients in batches of {self.spawn_batch_size} "
                f"with {self.spawn_batch_interval:.3f}s interval"
            )

        # Start shared subscriber for ID mappings (single SUB for all clients)
        if not self.enable_receive:
            self.shared_subscriber = SharedSubscriber(
                self.context,
                self.server_addr,
                self.sub_port,
                self.room_id,
                socket_register=self._register_socket,
                socket_unregister=self._unregister_socket,
            )
            if not self.shared_subscriber.start():
                self.logger.warning(
                    "SharedSubscriber failed to start; falling back to per-client SUBs"
                )
                self.shared_subscriber = None
        else:
            self.shared_subscriber = None
            self.logger.debug(
                "SharedSubscriber disabled to keep per-client SUB sockets active"
            )

        # Create and start clients
        successful_clients = 0
        failed_clients = 0

        for i in range(self.num_clients):
            # Create configuration
            config = SimulationConfig(
                device_id=str(uuid.uuid4()),
                movement_pattern=random.choice(list(MovementPattern)),
                start_position=Vector3(
                    random.uniform(-10, 10), 0, random.uniform(-10, 10)
                ),
            )

            # Create transport (enable per-client SUB only if shared subscriber isn't running)
            shared_sub = (
                self.shared_subscriber
                if (self.shared_subscriber and self.shared_subscriber.is_running())
                else None
            )
            transport = NetworkTransport(
                self.context,
                self.server_addr,
                self.dealer_port,
                self.sub_port,
                self.room_id,
                enable_sub=(
                    shared_sub is None or self.enable_receive
                ),  # disable per-client SUB if shared is running and recv disabled
                socket_register=self._register_socket,
                socket_unregister=self._unregister_socket,
            )

            # Create client
            client = SimulatedClient(
                config,
                transport,
                self.room_id,
                shared_subscriber=shared_sub,
                simulate_battery=self.simulate_battery,
                enable_receive=self.enable_receive,
                transform_send_rate=self.transform_send_rate,
            )

            # Add to thread pool
            if self.thread_pool.add_client(client):
                successful_clients += 1
                self.logger.info(
                    f"Started client {config.device_id[-8:]} "
                    f"with pattern {config.movement_pattern.value}"
                )
            else:
                failed_clients += 1
                self.logger.error(f"Failed to start client {i}")

                if failed_clients > 5:
                    self.logger.error(
                        f"Too many failures, stopping. "
                        f"Started {successful_clients}/{self.num_clients} clients"
                    )
                    break

            # Optional throttle to avoid connection stampede
            if (
                self.spawn_batch_size > 0
                and self.spawn_batch_interval > 0
                and (i + 1) % self.spawn_batch_size == 0
                and (i + 1) < self.num_clients
            ):
                time.sleep(self.spawn_batch_interval)

        self.logger.info(
            f"Started {successful_clients} clients, " f"{failed_clients} failed"
        )

        # Run until interrupted
        try:
            while self.running:
                if self._stop_requested.is_set():
                    break
                active = self.thread_pool.get_active_count()
                if active == 0:
                    self.logger.info("All clients stopped")
                    break
                time.sleep(1)
        except KeyboardInterrupt:
            self.logger.info("\nInterrupted by user")
        finally:
            self.stop()

    def stop(self) -> None:
        """Stop the simulation."""
        with self._stop_lock:
            if self._stopped:
                return
            self._stopped = True

        self.running = False
        self.logger.info("Stopping simulation...")

        # Stop all threads
        self.thread_pool.stop_all()

        # Stop shared subscriber
        try:
            if self.shared_subscriber:
                self.shared_subscriber.stop()
        except Exception as e:
            self.logger.error(f"Error stopping SharedSubscriber: {e}")

        # Ensure every tracked socket is closed before terminating context
        try:
            self._cleanup_tracked_sockets()
        except Exception as e:
            self.logger.debug(f"Socket cleanup encountered an error: {e}")

        # Cleanup ZMQ context
        try:
            if not getattr(self.context, "closed", False):
                self.context.term()
        except Exception as e:
            self.logger.error(f"Error terminating ZMQ context: {e}")

        self.logger.info("Simulation stopped")


# ============================================================================
# CLI Entry Point
# ============================================================================


def main() -> None:
    """Main entry point for the client simulator."""
    parser = argparse.ArgumentParser(
        description="STYLY NetSync Client Simulator - Load testing tool",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  %(prog)s --clients 100
  %(prog)s --clients 500 --server tcp://192.168.1.100 --room test_room
  %(prog)s --clients 50 --log-level DEBUG
        """,
    )

    parser.add_argument(
        "--clients",
        type=int,
        default=50,
        help="Number of clients to simulate (default: 50)",
    )
    parser.add_argument(
        "--server",
        type=str,
        default="localhost",
        help="Server address (default: localhost)",
    )
    parser.add_argument(
        "--dealer-port",
        type=int,
        default=5555,
        help="Server DEALER port (default: 5555)",
    )
    parser.add_argument(
        "--sub-port",
        type=int,
        default=5556,
        help="Server PUB port (default: 5556, reserved for future use)",
    )
    parser.add_argument(
        "--room",
        type=str,
        default="default_room",
        help="Room ID to join (default: default_room)",
    )
    parser.add_argument(
        "--log-level",
        type=str,
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging level (default: INFO)",
    )
    parser.add_argument(
        "--spawn-batch-size",
        type=int,
        default=0,
        help="Spawn clients in batches of N (0 disables)",
    )
    parser.add_argument(
        "--spawn-batch-interval",
        type=float,
        default=0.0,
        help="Delay in seconds between batches (requires --spawn-batch-size > 0)",
    )
    parser.add_argument(
        "--no-sync-battery",
        action="store_true",
        help="Disable battery level synchronization",
    )
    parser.add_argument(
        "--transform-send-rate",
        type=float,
        default=10.0,
        help="Transform update send rate in Hz (default: 10.0)",
    )

    args = parser.parse_args()

    # Normalize server address format: accept both "localhost" and "tcp://localhost"
    if not args.server.startswith("tcp://") and not args.server.startswith("ipc://"):
        args.server = f"tcp://{args.server}"

    # Configure logging
    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    # Print banner
    logger = logging.getLogger("main")
    logger.info("=" * 60)
    logger.info("STYLY NetSync Client Simulator")
    logger.info("=" * 60)

    # Create and run simulator
    simulator = ClientSimulator(
        server_addr=args.server,
        dealer_port=args.dealer_port,
        sub_port=args.sub_port,
        room_id=args.room,
        num_clients=args.clients,
        spawn_batch_size=args.spawn_batch_size,
        spawn_batch_interval=args.spawn_batch_interval,
        simulate_battery=not args.no_sync_battery,
        enable_receive=True,  # Always enable receive (was args.enable_recv)
        transform_send_rate=args.transform_send_rate,
    )

    try:
        simulator.start()
    except Exception as e:
        logger.error(f"Unexpected error: {e}", exc_info=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
