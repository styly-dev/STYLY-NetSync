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
import logging
import math
import random
import resource
import signal
import sys
import threading
import time
import uuid
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from enum import Enum
from typing import Dict, List, Optional, Tuple, Any

import zmq

# Import public APIs from styly_netsync module
from styly_netsync.binary_serializer import (
    MSG_CLIENT_TRANSFORM,
    MSG_DEVICE_ID_MAPPING,
    MSG_CLIENT_VAR_SET,
    deserialize,
    serialize_client_transform,
    serialize_client_var_set,
)


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

    def to_dict(self) -> Dict[str, float]:
        """Convert to dictionary format expected by serializer."""
        return {"posX": self.x, "posY": self.y, "posZ": self.z}

    def distance_to(self, other: 'Vector3') -> float:
        """Calculate distance to another vector."""
        dx = self.x - other.x
        dy = self.y - other.y
        dz = self.z - other.z
        return math.sqrt(dx*dx + dy*dy + dz*dz)


@dataclass
class Transform:
    """6DOF transform with position and rotation."""
    position: Vector3 = field(default_factory=Vector3)
    rotation: Vector3 = field(default_factory=Vector3)
    is_local_space: bool = False

    def to_dict(self) -> Dict[str, Any]:
        """Convert to dictionary format expected by serializer."""
        return {
            "posX": self.position.x,
            "posY": self.position.y,
            "posZ": self.position.z,
            "rotX": self.rotation.x,
            "rotY": self.rotation.y,
            "rotZ": self.rotation.z,
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


# ============================================================================
# Movement Strategy Pattern
# ============================================================================

class MovementStrategy(ABC):
    """Abstract base class for movement strategies."""
    
    def __init__(self, config: SimulationConfig):
        self.config = config
        self.start_time = time.monotonic()
        self.current_position = Vector3(
            config.start_position.x,
            config.start_position.y,
            config.start_position.z
        )
        self.previous_position = Vector3(
            config.start_position.x,
            config.start_position.y,
            config.start_position.z
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
            self.config.start_position.x + math.cos(angle) * self.config.movement_radius,
            self.config.start_position.y,
            self.config.start_position.z + math.sin(angle) * self.config.movement_radius,
        )


class Figure8Movement(MovementStrategy):
    """Figure-8 movement pattern."""
    
    def update_position(self, elapsed_time: float, delta_time: float) -> Vector3:
        t = elapsed_time * self.config.move_speed * 0.5
        return Vector3(
            self.config.start_position.x + math.sin(t) * self.config.movement_radius,
            self.config.start_position.y,
            self.config.start_position.z + math.sin(2 * t) * self.config.movement_radius * 0.5,
        )


class RandomWalkMovement(MovementStrategy):
    """Random walk movement pattern."""
    
    def __init__(self, config: SimulationConfig):
        super().__init__(config)
        self.change_interval = 2.0
        self.walk_range = 5.0
        self.timer = 0.0
        self.target = Vector3(
            config.start_position.x,
            config.start_position.y,
            config.start_position.z
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
        
        length = math.sqrt(direction.x ** 2 + direction.z ** 2)
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
            self.config.start_position.y + math.sin(elapsed_time * self.config.move_speed * 0.3) * 0.5,
            self.config.start_position.z + math.sin(angle) * radius,
        )


# ============================================================================
# Movement Strategy Factory
# ============================================================================

class MovementStrategyFactory:
    """Factory for creating movement strategies."""
    
    _strategies = {
        MovementPattern.CIRCLE: CircleMovement,
        MovementPattern.FIGURE8: Figure8Movement,
        MovementPattern.RANDOM_WALK: RandomWalkMovement,
        MovementPattern.LINEAR_PING_PONG: LinearPingPongMovement,
        MovementPattern.SPIRAL: SpiralMovement,
    }
    
    @classmethod
    def create(cls, pattern: MovementPattern, config: SimulationConfig) -> MovementStrategy:
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
            i / config.virtual_count * 2 * math.pi 
            for i in range(config.virtual_count)
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
        rotation_y: float
    ) -> Dict[str, Any]:
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
            "physical": {
                "posX": position.x,
                "posY": 0,  # Physical Y is always 0
                "posZ": position.z,
                "rotX": 0,
                "rotY": self.rotation_y,
                "rotZ": 0,
                "isLocalSpace": True,
            },
            "head": {
                "posX": head_position.x,
                "posY": head_position.y,
                "posZ": head_position.z,
                "rotX": 0,
                "rotY": self.rotation_y,
                "rotZ": 0,
                "isLocalSpace": False,
            },
            "rightHand": {
                "posX": right_hand.x,
                "posY": right_hand.y,
                "posZ": right_hand.z,
                "rotX": 0,
                "rotY": 0,
                "rotZ": 0,
                "isLocalSpace": False,
            },
            "leftHand": {
                "posX": left_hand.x,
                "posY": left_hand.y,
                "posZ": left_hand.z,
                "rotX": 0,
                "rotY": 0,
                "rotZ": 0,
                "isLocalSpace": False,
            },
            "virtuals": virtuals,
        }
    
    def _smooth_rotation(self, current: float, target: float, delta_time: float) -> float:
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
        self, 
        base_position: Vector3, 
        elapsed_time: float, 
        is_right: bool
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
            * self.config.hand_swing_amplitude * 0.5
        )
        swing_z = (
            math.sin(elapsed_time * self.config.hand_swing_speed * 1.3 + phase_offset)
            * self.config.hand_swing_amplitude * 0.3
        )
        
        return Vector3(
            base_position.x + lateral_offset + swing_x,
            base_position.y + vertical_offset + swing_y,
            base_position.z + swing_z,
        )
    
    def _calculate_virtual_positions(
        self, 
        base_position: Vector3, 
        elapsed_time: float
    ) -> List[Dict[str, Any]]:
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
            
            virtuals.append({
                "posX": avatar_center.x + orbit_x,
                "posY": avatar_center.y + orbit_y,
                "posZ": avatar_center.z + orbit_z,
                "rotX": 0,
                "rotY": phase,
                "rotZ": 0,
                "isLocalSpace": False,
            })
        
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
    ):
        self.context = context
        self.server_addr = server_addr
        self.dealer_port = dealer_port
        self.sub_port = sub_port
        self.room_id = room_id
        self.enable_sub = enable_sub
        self.socket: Optional[zmq.Socket] = None
        self.sub_socket: Optional[zmq.Socket] = None
        self.logger = logging.getLogger(self.__class__.__name__)

    def connect(self) -> bool:
        """Establish connection to server (both DEALER and SUB)."""
        try:
            # DEALER for sending
            self.socket = self.context.socket(zmq.DEALER)
            self.socket.setsockopt(zmq.LINGER, 0)
            self.socket.setsockopt(zmq.RCVTIMEO, 1000)

            dealer_endpoint = f"{self.server_addr}:{self.dealer_port}"
            self.socket.connect(dealer_endpoint)
            self.logger.debug(f"Connected DEALER to {dealer_endpoint}")

            # Optional SUB for receiving broadcasts (ID mappings, NV syncs, etc.)
            if self.enable_sub:
                try:
                    self.sub_socket = self.context.socket(zmq.SUB)
                    self.sub_socket.setsockopt(zmq.LINGER, 0)
                    self.sub_socket.setsockopt(zmq.RCVTIMEO, 10)  # non-blocking-ish
                    sub_endpoint = f"{self.server_addr}:{self.sub_port}"
                    self.sub_socket.connect(sub_endpoint)
                    # Subscribe to room topic
                    self.sub_socket.setsockopt(zmq.SUBSCRIBE, self.room_id.encode("utf-8"))
                    self.logger.debug(f"Connected SUB to {sub_endpoint} topic '{self.room_id}'")
                except Exception as se:
                    # SUB is optional; log and continue
                    self.logger.warning(f"Failed to setup SUB socket: {se}")

            return True
        except Exception as e:
            self.logger.error(f"Failed to connect: {e}")
            return False
    
    def send_transform(self, room_id: str, transform_data: Dict[str, Any]) -> bool:
        """Send transform data to server."""
        if not self.socket:
            return False
        
        try:
            binary_data = serialize_client_transform(transform_data)
            self.socket.send_multipart([
                room_id.encode("utf-8"),
                binary_data,
            ])
            return True
        except Exception as e:
            self.logger.error(f"Failed to send transform: {e}")
            return False
    
    def send_client_variable(self, room_id: str, sender_client_no: int, var_name: str, value: str) -> bool:
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
            self.socket.send_multipart([
                room_id.encode("utf-8"),
                binary_data,
            ])
            return True
        except Exception as e:
            self.logger.error(f"Failed to send client variable: {e}")
            return False

    def recv_broadcast(self) -> Optional[Tuple[int, Dict[str, Any]]]:
        """Scan incoming broadcasts quickly and return ID mapping if found.

        Returns (msg_type, data) for MSG_DEVICE_ID_MAPPING else None. Drains a
        limited number of messages per call to reduce backlog latency.
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
            # For other message types, drop and continue scanning
        return None
    
    def disconnect(self):
        """Close connection to server."""
        if self.socket:
            self.socket.close()
            self.socket = None
        if self.sub_socket:
            self.sub_socket.close()
            self.sub_socket = None


# ============================================================================
# Shared Subscriber (Single SUB socket for all clients)
# ============================================================================

class SharedSubscriber:
    """Single SUB socket that receives broadcasts and shares mappings."""

    def __init__(self, context: zmq.Context, server_addr: str, sub_port: int, room_id: str):
        self.context = context
        self.server_addr = server_addr
        self.sub_port = sub_port
        self.room_id = room_id
        self.logger = logging.getLogger(self.__class__.__name__)
        self.socket: Optional[zmq.Socket] = None
        self._thread: Optional[threading.Thread] = None
        self._running = False
        self._lock = threading.Lock()
        self._device_to_client: Dict[str, int] = {}

    def start(self) -> bool:
        try:
            self.socket = self.context.socket(zmq.SUB)
            self.socket.setsockopt(zmq.LINGER, 0)
            self.socket.setsockopt(zmq.RCVTIMEO, 10)
            endpoint = f"{self.server_addr}:{self.sub_port}"
            self.socket.connect(endpoint)
            self.socket.setsockopt(zmq.SUBSCRIBE, self.room_id.encode("utf-8"))
            self._running = True
            self._thread = threading.Thread(target=self._loop, name="SharedSubscriber", daemon=True)
            self._thread.start()
            self.logger.debug(f"Shared SUB connected to {endpoint} topic '{self.room_id}'")
            return True
        except Exception as e:
            self.logger.error(f"Failed to start SharedSubscriber: {e}")
            return False

    def stop(self):
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
            self.socket = None

    def get_client_no(self, device_id: str) -> int:
        with self._lock:
            return self._device_to_client.get(device_id, 0)

    def _loop(self):
        # Drain many messages each tick to avoid backlog
        while self._running:
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
                    if not data:
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
    BATTERY_INITIAL_MIN = 50.0        # %
    BATTERY_INITIAL_MAX = 100.0       # %
    BATTERY_DRAIN_RATE = 10.0 / 60.0  # %-points lost per second (10%/min)
    BATTERY_UPDATE_INTERVAL = 60.0     # Send battery updates every 60 seconds
    
    def __init__(
        self,
        config: SimulationConfig,
        transport: NetworkTransport,
        room_id: str,
        shared_subscriber: Optional["SharedSubscriber"] = None,
    ):
        self.config = config
        self.transport = transport
        self.room_id = room_id
        self.movement = MovementStrategyFactory.create(config.movement_pattern, config)
        self.transform_builder = TransformBuilder(config)
        self.logger = logging.getLogger(f"Client-{config.device_id[-8:]}")
        self.shared_subscriber = shared_subscriber
        
        self.start_time = time.monotonic()
        self.last_update_time = self.start_time
        self.running = False
        
        # Battery simulation state
        self.battery_level = random.uniform(self.BATTERY_INITIAL_MIN, self.BATTERY_INITIAL_MAX)
        self.last_battery_update = self.start_time
        # Force initial battery send as soon as client number is known
        self.last_battery_send = self.start_time - self.BATTERY_UPDATE_INTERVAL
        # Client number assigned by server after handshake (0 until assigned)
        self.client_number = 0

        self.logger.info(
            f"Initialized with battery level: {self.battery_level:.1f}%, awaiting client number assignment"
        )
    
    def run(self, stop_event: threading.Event):
        """Run the client simulation loop."""
        if not self.transport.connect():
            self.logger.error("Failed to connect to server")
            return
        
        self.running = True
        update_interval = 0.1  # 10Hz
        last_send_time = time.monotonic()
        
        try:
            while self.running and not stop_event.is_set():
                current_time = time.monotonic()
                
                if current_time - last_send_time >= update_interval:
                    elapsed_time = current_time - self.start_time
                    delta_time = current_time - self.last_update_time
                    
                    # Update movement
                    self.movement.previous_position = self.movement.current_position
                    new_position = self.movement.update_position(elapsed_time, delta_time)
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
            self.logger.info(f"Client stopped with final battery level: {self.battery_level:.1f}%")

    def _update_battery(self, current_time: float):
        """Update battery level and send updates when needed."""
        # Update battery drain
        elapsed_since_battery_update = current_time - self.last_battery_update
        if elapsed_since_battery_update > 0:
            self.battery_level = max(0.0, self.battery_level - (elapsed_since_battery_update * self.BATTERY_DRAIN_RATE))
            self.last_battery_update = current_time
        
        # Send battery update periodically
        elapsed_since_send = current_time - self.last_battery_send
        if elapsed_since_send >= self.BATTERY_UPDATE_INTERVAL and self.client_number > 0:
            battery_value = f"{self.battery_level:.1f}"
            if self.transport.send_client_variable(self.room_id, self.client_number, "BatteryLevel", battery_value):
                self.logger.debug(f"Sent battery level: {battery_value}%")
            else:
                self.logger.warning("Failed to send battery level update")
            self.last_battery_send = current_time

    def _poll_broadcasts(self):
        """Update local client number from shared subscriber or fallback SUB."""
        # Prefer shared subscriber for scalability
        if self.shared_subscriber is not None:
            assigned = self.shared_subscriber.get_client_no(self.config.device_id)
            if assigned and assigned != self.client_number:
                self.client_number = assigned
                self.logger.info(f"Assigned client number by server: {self.client_number}")
            return

        # Fallback: use own transport SUB if available
        msg = self.transport.recv_broadcast()
        if not msg:
            return
        msg_type, data = msg
        try:
            if msg_type == MSG_DEVICE_ID_MAPPING:
                mappings = data.get("mappings", [])
                for m in mappings:
                    if m.get("deviceId") == self.config.device_id:
                        assigned = int(m.get("clientNo", 0))
                        if assigned and assigned != self.client_number:
                            self.client_number = assigned
                            self.logger.info(f"Assigned client number by server: {self.client_number}")
                        break
        except Exception as e:
            self.logger.debug(f"Failed to process broadcast: {e}")


# ============================================================================
# Thread Pool Manager
# ============================================================================

class ClientThreadPool:
    """Manages a pool of client simulation threads."""
    
    def __init__(self, max_clients: int):
        self.max_clients = max_clients
        self.threads: List[threading.Thread] = []
        self.clients: List[SimulatedClient] = []
        self.stop_event = threading.Event()
        self.logger = logging.getLogger(self.__class__.__name__)
    
    def add_client(self, client: SimulatedClient) -> bool:
        """Add a client to the pool and start its thread."""
        if len(self.threads) >= self.max_clients:
            self.logger.warning("Thread pool is full")
            return False
        
        thread = threading.Thread(
            target=client.run,
            args=(self.stop_event,),
            daemon=True
        )
        
        try:
            thread.start()
            self.threads.append(thread)
            self.clients.append(client)
            return True
        except Exception as e:
            self.logger.error(f"Failed to start client thread: {e}")
            return False
    
    def stop_all(self, timeout: float = 2.0):
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
    def check_file_descriptor_limit(required_fds: int) -> Tuple[bool, str]:
        """Check if system can handle the required number of file descriptors."""
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
    def try_raise_fd_limit(min_required: int) -> Tuple[bool, str]:
        """Attempt to raise the soft RLIMIT_NOFILE to accommodate simulation.

        Returns (success, message). If unsuccessful, returns reason in message.
        """
        try:
            soft_limit, hard_limit = resource.getrlimit(resource.RLIMIT_NOFILE)
            if soft_limit >= min_required:
                return True, f"FD limit sufficient (soft={soft_limit}, hard={hard_limit})"

            # Aim for either hard limit or a sane default, whichever is smaller
            desired = max(min_required, ResourceManager.DEFAULT_FD_LIMIT)
            new_soft = min(int(hard_limit), int(desired))
            if new_soft <= soft_limit:
                return False, (
                    "Cannot raise FD limit beyond hard limit. "
                    f"soft={soft_limit}, hard={hard_limit}, required={min_required}"
                )

            resource.setrlimit(resource.RLIMIT_NOFILE, (new_soft, hard_limit))
            return True, f"Raised soft FD limit: {soft_limit} -> {new_soft} (hard={hard_limit})"

        except Exception as e:
            return False, f"Failed to raise FD limit: {e}"
    
    @staticmethod
    def setup_signal_handlers(handler):
        """Setup signal handlers for clean shutdown."""
        signal.signal(signal.SIGINT, handler)
        signal.signal(signal.SIGTERM, handler)


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
    ):
        self.server_addr = server_addr
        self.dealer_port = dealer_port
        self.sub_port = sub_port  # Future use
        self.room_id = room_id
        self.num_clients = num_clients
        self.spawn_batch_size = spawn_batch_size if spawn_batch_size is not None else 0
        self.spawn_batch_interval = spawn_batch_interval if spawn_batch_interval is not None else 0.0
        
        self.context = zmq.Context()
        self.thread_pool = ClientThreadPool(num_clients)
        self.running = False
        self.logger = logging.getLogger(self.__class__.__name__)
        self.shared_subscriber: Optional[SharedSubscriber] = None
        
        # Setup signal handlers
        ResourceManager.setup_signal_handlers(self._signal_handler)
    
    def _signal_handler(self, signum, frame):
        """Handle interrupt signals."""
        self.logger.info(f"\nReceived signal {signum}, stopping simulation...")
        self.stop()
    
    def start(self):
        """Start the simulation."""
        self.running = True
        
        # Estimate FD usage and try to ensure limits
        estimated_fds = ResourceManager.estimate_fd_need(self.num_clients)
        ok_before, msg_before = ResourceManager.check_file_descriptor_limit(estimated_fds)
        if not ok_before:
            self.logger.warning(msg_before)
            ok_raise, msg_raise = ResourceManager.try_raise_fd_limit(estimated_fds)
            if ok_raise:
                self.logger.info(msg_raise)
                # Re-check to inform final status
                _, msg_after = ResourceManager.check_file_descriptor_limit(estimated_fds)
                self.logger.info(msg_after)
            else:
                self.logger.warning(msg_raise)
        else:
            self.logger.info(msg_before)
        
        self.logger.info(f"Starting simulation with {self.num_clients} clients")
        self.logger.info(f"Server: {self.server_addr}:{self.dealer_port}")
        self.logger.info(f"Room: {self.room_id}")
        if self.spawn_batch_size > 0 and self.spawn_batch_interval > 0:
            self.logger.info(
                f"Spawning clients in batches of {self.spawn_batch_size} "
                f"with {self.spawn_batch_interval:.3f}s interval"
            )
        
        # Start shared subscriber for ID mappings (single SUB for all clients)
        self.shared_subscriber = SharedSubscriber(self.context, self.server_addr, self.sub_port, self.room_id)
        if not self.shared_subscriber.start():
            self.logger.warning("SharedSubscriber failed to start; falling back to per-client SUBs")

        # Create and start clients
        successful_clients = 0
        failed_clients = 0
        
        for i in range(self.num_clients):
            # Create configuration
            config = SimulationConfig(
                device_id=str(uuid.uuid4()),
                movement_pattern=random.choice(list(MovementPattern)),
                start_position=Vector3(
                    random.uniform(-10, 10),
                    0,
                    random.uniform(-10, 10)
                )
            )
            
            # Create transport
            transport = NetworkTransport(
                self.context,
                self.server_addr,
                self.dealer_port,
                self.sub_port,
                self.room_id,
                enable_sub=(self.shared_subscriber is None),  # disable per-client SUB if shared is running
            )
            
            # Create client
            client = SimulatedClient(config, transport, self.room_id, shared_subscriber=self.shared_subscriber)
            
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
            f"Started {successful_clients} clients, "
            f"{failed_clients} failed"
        )
        
        # Run until interrupted
        try:
            while self.running:
                active = self.thread_pool.get_active_count()
                if active == 0:
                    self.logger.info("All clients stopped")
                    break
                time.sleep(1)
        except KeyboardInterrupt:
            self.logger.info("\nInterrupted by user")
        finally:
            self.stop()
    
    def stop(self):
        """Stop the simulation."""
        if not self.running:
            return
        
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
        
        # Cleanup ZMQ context
        try:
            self.context.term()
        except Exception as e:
            self.logger.error(f"Error terminating ZMQ context: {e}")
        
        self.logger.info("Simulation stopped")


# ============================================================================
# CLI Entry Point
# ============================================================================

def main():
    """Main entry point for the client simulator."""
    parser = argparse.ArgumentParser(
        description="STYLY NetSync Client Simulator - Load testing tool",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  %(prog)s --clients 100
  %(prog)s --clients 500 --server tcp://192.168.1.100 --room test_room
  %(prog)s --clients 50 --log-level DEBUG
        """
    )
    
    parser.add_argument(
        "--clients",
        type=int,
        default=100,
        help="Number of clients to simulate (default: 100)"
    )
    parser.add_argument(
        "--server",
        type=str,
        default="tcp://localhost",
        help="Server address (default: tcp://localhost)"
    )
    parser.add_argument(
        "--dealer-port",
        type=int,
        default=5555,
        help="Server DEALER port (default: 5555)"
    )
    parser.add_argument(
        "--sub-port",
        type=int,
        default=5556,
        help="Server PUB port (default: 5556, reserved for future use)"
    )
    parser.add_argument(
        "--room",
        type=str,
        default="default_room",
        help="Room ID to join (default: default_room)"
    )
    parser.add_argument(
        "--log-level",
        type=str,
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging level (default: INFO)"
    )
    parser.add_argument(
        "--spawn-batch-size",
        type=int,
        default=0,
        help="Spawn clients in batches of N (0 disables)"
    )
    parser.add_argument(
        "--spawn-batch-interval",
        type=float,
        default=0.0,
        help="Delay in seconds between batches (requires --spawn-batch-size > 0)"
    )
    
    args = parser.parse_args()
    
    # Configure logging
    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S"
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
    )
    
    try:
        simulator.start()
    except Exception as e:
        logger.error(f"Unexpected error: {e}", exc_info=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
