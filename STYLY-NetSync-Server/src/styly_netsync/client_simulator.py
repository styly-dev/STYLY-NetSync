# client_simulator.py
"""
Client simulator for STYLY NetSync testing.
Simulates multiple clients sending transform data to the server.
"""

import argparse
import logging
import math
import random
import threading
import time
import uuid
from enum import Enum
from typing import Dict, List, Tuple

import zmq

# Import the binary serializer from the same package
try:
    from .binary_serializer import serialize_client_transform
except ImportError:
    # Fallback for direct script execution
    from binary_serializer import serialize_client_transform

class MovementPattern(Enum):
    """Movement patterns matching DebugMoveAvatar.cs"""

    CIRCLE = 0
    FIGURE8 = 1
    RANDOM_WALK = 2
    LINEAR_PING_PONG = 3
    SPIRAL = 4

class SimulatedClient:
    """Represents a single simulated client with movement patterns."""

    def __init__(
        self,
        device_id: str,
        movement_pattern: MovementPattern,
        start_position: Tuple[float, float, float],
    ):
        self.device_id = device_id
        self.movement_pattern = movement_pattern
        self.start_position = start_position
        self.current_position = list(start_position)
        self.start_time = time.time()

        # Movement parameters (matching DebugMoveAvatar.cs defaults)
        self.move_speed = 2.0
        self.movement_radius = 3.0
        self.hand_swing_amplitude = 0.5
        self.hand_swing_speed = 3.0
        self.virtual_item_orbit_speed = 1.0
        self.virtual_item_orbit_radius = 1.0
        self.virtual_item_vertical_range = 0.5

        # Random walk specific
        self.random_walk_change_interval = 2.0
        self.random_walk_range = 5.0
        self.random_walk_timer = 0.0
        self.current_target = list(start_position)
        self.last_update_time = time.time()

        # Virtual items setup (2-3 random virtual objects)
        self.virtual_count = random.randint(2, 3)
        self.virtual_orbit_phases = [
            i / self.virtual_count * 2 * math.pi for i in range(self.virtual_count)
        ]
        self.virtual_orbit_speeds = [
            self.virtual_item_orbit_speed * random.uniform(0.5, 1.5)
            for _ in range(self.virtual_count)
        ]
        self.virtual_orbit_radii = [
            self.virtual_item_orbit_radius * random.uniform(0.8, 1.2)
            for _ in range(self.virtual_count)
        ]

        # Logging (use only last 8 chars of UUID for readability)
        self.logger = logging.getLogger(f"Device-{device_id[-8:]}")

    def update(self) -> Dict:
        """Update client position and return transform data."""
        current_time = time.time()
        elapsed_time = current_time - self.start_time
        delta_time = current_time - self.last_update_time
        self.last_update_time = current_time

        # Update avatar movement
        new_position = self._update_movement(elapsed_time, delta_time)

        # Calculate head position (1.7m above avatar center)
        head_pos = [new_position[0], new_position[1] + 1.7, new_position[2]]

        # Calculate hand positions with swing motion
        right_hand_pos = self._calculate_hand_position(
            new_position, elapsed_time, is_right=True
        )
        left_hand_pos = self._calculate_hand_position(
            new_position, elapsed_time, is_right=False
        )

        # Calculate virtual object positions
        virtuals = self._calculate_virtual_positions(new_position, elapsed_time)

        # Build transform data matching the expected format
        transform_data = {
            "deviceId": self.device_id,
            "physical": {
                "posX": new_position[0],
                "posY": 0,  # Y is always 0 for physical transform
                "posZ": new_position[2],
                "rotX": 0,
                "rotY": 0,  # For simplicity, no rotation in simulation
                "rotZ": 0,
            },
            "head": {
                "posX": head_pos[0],
                "posY": head_pos[1],
                "posZ": head_pos[2],
                "rotX": 0,
                "rotY": 0,
                "rotZ": 0,
            },
            "rightHand": {
                "posX": right_hand_pos[0],
                "posY": right_hand_pos[1],
                "posZ": right_hand_pos[2],
                "rotX": 0,
                "rotY": 0,
                "rotZ": 0,
            },
            "leftHand": {
                "posX": left_hand_pos[0],
                "posY": left_hand_pos[1],
                "posZ": left_hand_pos[2],
                "rotX": 0,
                "rotY": 0,
                "rotZ": 0,
            },
            "virtuals": virtuals,
        }

        return transform_data

    def _update_movement(self, elapsed_time: float, delta_time: float) -> List[float]:
        """Update movement based on selected pattern."""
        if self.movement_pattern == MovementPattern.CIRCLE:
            return self._calculate_circle_movement(elapsed_time)
        elif self.movement_pattern == MovementPattern.FIGURE8:
            return self._calculate_figure8_movement(elapsed_time)
        elif self.movement_pattern == MovementPattern.RANDOM_WALK:
            return self._calculate_random_walk_movement(delta_time)
        elif self.movement_pattern == MovementPattern.LINEAR_PING_PONG:
            return self._calculate_linear_ping_pong_movement(elapsed_time)
        elif self.movement_pattern == MovementPattern.SPIRAL:
            return self._calculate_spiral_movement(elapsed_time)
        else:
            return list(self.start_position)

    def _calculate_circle_movement(self, elapsed_time: float) -> List[float]:
        """Circle movement pattern."""
        angle = elapsed_time * self.move_speed
        return [
            self.start_position[0] + math.cos(angle) * self.movement_radius,
            self.start_position[1],
            self.start_position[2] + math.sin(angle) * self.movement_radius,
        ]

    def _calculate_figure8_movement(self, elapsed_time: float) -> List[float]:
        """Figure-8 movement pattern."""
        t = elapsed_time * self.move_speed * 0.5
        return [
            self.start_position[0] + math.sin(t) * self.movement_radius,
            self.start_position[1],
            self.start_position[2] + math.sin(2 * t) * self.movement_radius * 0.5,
        ]

    def _calculate_random_walk_movement(self, delta_time: float) -> List[float]:
        """Random walk movement pattern."""
        self.random_walk_timer += delta_time

        if self.random_walk_timer >= self.random_walk_change_interval:
            self.random_walk_timer = 0.0
            # Generate new random target
            angle = random.uniform(0, 2 * math.pi)
            distance = random.uniform(1.0, self.random_walk_range)
            self.current_target = [
                self.start_position[0] + math.cos(angle) * distance,
                self.start_position[1],
                self.start_position[2] + math.sin(angle) * distance,
            ]

        # Move towards target
        direction = [
            self.current_target[0] - self.current_position[0],
            0,
            self.current_target[2] - self.current_position[2],
        ]

        # Normalize direction
        length = math.sqrt(direction[0] ** 2 + direction[2] ** 2)
        if length > 0:
            direction[0] /= length
            direction[2] /= length

            # Move towards target
            move_distance = self.move_speed * delta_time
            self.current_position[0] += direction[0] * move_distance
            self.current_position[2] += direction[2] * move_distance

        return self.current_position

    def _calculate_linear_ping_pong_movement(self, elapsed_time: float) -> List[float]:
        """Linear ping-pong movement pattern."""
        # Create a value that goes from -1 to 1 and back
        ping_pong = math.sin(elapsed_time * self.move_speed)
        return [
            self.start_position[0],
            self.start_position[1],
            self.start_position[2] + ping_pong * self.movement_radius,
        ]

    def _calculate_spiral_movement(self, elapsed_time: float) -> List[float]:
        """Spiral movement pattern."""
        angle = elapsed_time * self.move_speed
        radius = (self.movement_radius * 0.5) + math.sin(elapsed_time * 0.5) * (
            self.movement_radius * 0.5
        )
        return [
            self.start_position[0] + math.cos(angle) * radius,
            self.start_position[1]
            + math.sin(elapsed_time * self.move_speed * 0.3) * 0.5,
            self.start_position[2] + math.sin(angle) * radius,
        ]

    def _calculate_hand_position(
        self, base_position: List[float], elapsed_time: float, is_right: bool
    ) -> List[float]:
        """Calculate hand position with swing motion."""
        # Base offset from body center
        lateral_offset = 0.3 if is_right else -0.3
        vertical_offset = 1.2

        # Add phase offset for left hand
        phase_offset = 0 if is_right else math.pi

        # Calculate swing motion
        swing_x = (
            math.sin(elapsed_time * self.hand_swing_speed + phase_offset)
            * self.hand_swing_amplitude
        )
        swing_y = (
            math.cos(elapsed_time * self.hand_swing_speed * 0.7 + phase_offset)
            * self.hand_swing_amplitude
            * 0.5
        )
        swing_z = (
            math.sin(elapsed_time * self.hand_swing_speed * 1.3 + phase_offset)
            * self.hand_swing_amplitude
            * 0.3
        )

        return [
            base_position[0] + lateral_offset + swing_x,
            base_position[1] + vertical_offset + swing_y,
            base_position[2] + swing_z,
        ]

    def _calculate_virtual_positions(
        self, base_position: List[float], elapsed_time: float
    ) -> List[Dict]:
        """Calculate positions for virtual objects orbiting the avatar."""
        virtuals = []
        avatar_center = [
            base_position[0],
            base_position[1] + 1.0,
            base_position[2],
        ]  # Center at 1m height

        for i in range(self.virtual_count):
            current_phase = (
                self.virtual_orbit_phases[i]
                + elapsed_time * self.virtual_orbit_speeds[i]
            )
            radius = self.virtual_orbit_radii[i]

            # 3D orbital motion
            orbit_x = math.cos(current_phase) * radius
            orbit_y = math.sin(current_phase * 2) * self.virtual_item_vertical_range
            orbit_z = math.sin(current_phase) * radius * 0.7  # Elliptical orbit

            virtual_pos = {
                "posX": avatar_center[0] + orbit_x,
                "posY": avatar_center[1] + orbit_y,
                "posZ": avatar_center[2] + orbit_z,
                "rotX": 0,
                "rotY": current_phase,  # Rotate around Y axis
                "rotZ": 0,
            }
            virtuals.append(virtual_pos)

        return virtuals


class ClientSimulator:
    """Manages multiple simulated clients."""

    def __init__(
        self,
        server_addr: str,
        dealer_port: int,
        sub_port: int,
        room_id: str,
        num_clients: int,
    ):
        self.server_addr = server_addr
        self.dealer_port = dealer_port
        self.sub_port = sub_port
        self.room_id = room_id
        self.num_clients = num_clients
        self.clients: List[SimulatedClient] = []
        self.running = False
        self.threads: List[threading.Thread] = []
        self.logger = logging.getLogger("ClientSimulator")
        # Create a single shared ZMQ context for all clients
        self.context = zmq.Context()

    def start(self):
        """Start the simulation."""
        self.running = True
        self.logger.info(f"Starting simulation with {self.num_clients} clients")

        # Check system limits first
        import resource
        try:
            soft_limit, hard_limit = resource.getrlimit(resource.RLIMIT_NOFILE)
            self.logger.info(f"System file descriptor limits: soft={soft_limit}, hard={hard_limit}")

            # Warn if trying to create too many clients
            # Each client needs 1 socket + overhead for other system files
            estimated_fds = self.num_clients + 50  # Add buffer for system files
            if estimated_fds > soft_limit * 0.8:  # Warn at 80% of limit
                self.logger.warning(
                    f"WARNING: Attempting to create {self.num_clients} clients may exceed system limits.\n"
                    f"Estimated file descriptors needed: {estimated_fds}\n"
                    f"Current soft limit: {soft_limit}\n"
                    f"To increase limit, run: ulimit -n 4096"
                )
        except Exception as e:
            self.logger.debug(f"Could not check system limits: {e}")

        # Create simulated clients with random starting positions and patterns
        failed_clients = 0
        for i in range(self.num_clients):
            # Generate unique device ID (GUID) for each client
            device_id = str(uuid.uuid4())

            # Random starting position within a reasonable area
            start_x = random.uniform(-10, 10)
            start_z = random.uniform(-10, 10)
            start_position = (start_x, 0, start_z)

            # Random movement pattern
            movement_pattern = random.choice(list(MovementPattern))

            client = SimulatedClient(device_id, movement_pattern, start_position)
            self.clients.append(client)

            # Create thread for this client
            thread = threading.Thread(
                target=self._run_client, args=(client,), daemon=True
            )
            self.threads.append(thread)

            try:
                thread.start()
                self.logger.info(
                    f"Started device {device_id[-8:]} (full: {device_id}) with pattern {movement_pattern.name}"
                )
            except Exception as e:
                failed_clients += 1
                self.logger.error(f"Failed to start device {device_id}: {e}")
                if "Too many open files" in str(e) or failed_clients > 5:
                    self.logger.error(
                        f"Stopping client creation due to resource limits. "
                        f"Successfully started {i - failed_clients} clients."
                    )
                    break

        if failed_clients > 0:
            self.logger.warning(f"Failed to start {failed_clients} clients due to resource limits.")

        self.logger.info("All clients started. Press Ctrl+C to stop simulation.")

        # Wait for all threads with proper interrupt handling
        try:
            while self.running:
                time.sleep(0.1)
                # Check if any threads are still alive
                if not any(thread.is_alive() for thread in self.threads):
                    break
        except KeyboardInterrupt:
            self.logger.info("\nReceived interrupt signal (Ctrl+C)...")
            self.stop()

    def stop(self):
        """Stop the simulation."""
        self.logger.info("Stopping simulation...")
        self.running = False

        # Wait for threads to finish with timeout
        for i, thread in enumerate(self.threads):
            if thread.is_alive():
                self.logger.debug(f"Waiting for device thread {i} to finish...")
                thread.join(timeout=2.0)
                if thread.is_alive():
                    self.logger.warning(f"Device thread {i} did not finish within timeout")

        # Terminate the shared context after all threads are done
        try:
            self.context.term()
        except Exception as e:
            self.logger.error(f"Error terminating ZMQ context: {e}")

        self.logger.info("Simulation stopped.")

    def _run_client(self, client: SimulatedClient):
        """Run a single client simulation in its own thread."""
        dealer_socket = None

        try:
            # Use the shared context instead of creating a new one
            dealer_socket = self.context.socket(zmq.DEALER)

            # Set socket options to prevent hanging
            dealer_socket.setsockopt(zmq.LINGER, 0)
            dealer_socket.setsockopt(zmq.RCVTIMEO, 1000)  # 1 second timeout

            # Connect to server
            dealer_endpoint = f"{self.server_addr}:{self.dealer_port}"
            dealer_socket.connect(dealer_endpoint)
            client.logger.info(f"Connected to server at {dealer_endpoint}")

            # Send initial join room message (simplified - just send transform data)
            # In a real implementation, you would implement proper handshake

            # Main update loop - 10Hz (100ms interval)
            update_interval = 0.1  # 100ms
            last_update = time.time()

            while self.running:
                current_time = time.time()

                if current_time - last_update >= update_interval:
                    # Update client state and get transform data
                    transform_data = client.update()

                    # Serialize to binary format
                    binary_data = serialize_client_transform(transform_data)

                    # Send to server with room_id as separate frame
                    dealer_socket.send_multipart(
                        [
                            self.room_id.encode("utf-8"),  # room_id
                            binary_data,  # message
                        ]
                    )

                    client.logger.debug(
                        f"Sent transform update: pos=({transform_data['physical']['posX']:.2f}, {transform_data['physical']['posZ']:.2f})"
                    )

                    last_update = current_time

                # Small sleep to prevent busy waiting
                time.sleep(0.01)

        except zmq.error.ZMQError as e:
            if "Too many open files" in str(e):
                client.logger.error("Socket creation failed - too many open files. Consider reducing the number of clients or increasing system ulimit.")
            else:
                client.logger.error(f"ZMQ error in client simulation: {e}")
        except Exception as e:
            client.logger.error(f"Error in client simulation: {e}")
        finally:
            if dealer_socket:
                dealer_socket.close()
            # Don't terminate the context here - it's shared
            client.logger.info("Device disconnected")


def main():
    """Main entry point."""
    parser = argparse.ArgumentParser(
        description="STYLY NetSync Client Simulator"
    )
    parser.add_argument(
        "--clients",
        type=int,
        default=100,
        help="Number of clients to simulate (default: 100)",
    )
    parser.add_argument(
        "--server",
        type=str,
        default="tcp://localhost",
        help="Server address (default: tcp://localhost)",
    )
    parser.add_argument(
        "--dealer-port", type=int, default=5555, help="DEALER port (default: 5555)"
    )
    parser.add_argument(
        "--sub-port", type=int, default=5556, help="SUB port (default: 5556)"
    )
    parser.add_argument(
        "--room",
        type=str,
        default="default_room",
        help="Room ID (default: default_room)",
    )
    parser.add_argument(
        "--log-level",
        type=str,
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Log level (default: INFO)",
    )

    args = parser.parse_args()

    # Configure logging
    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
    )

    logger = logging.getLogger("main")
    logger.info("=" * 80)
    logger.info("STYLY NetSync Client Simulator")
    logger.info("=" * 80)
    logger.info(f"  Server: {args.server}")
    logger.info(f"  DEALER port: {args.dealer_port}")
    logger.info(f"  SUB port: {args.sub_port}")
    logger.info(f"  Room: {args.room}")
    logger.info(f"  Clients: {args.clients}")
    logger.info("=" * 80)

    # Create and run simulator
    simulator = ClientSimulator(
        server_addr=args.server,
        dealer_port=args.dealer_port,
        sub_port=args.sub_port,
        room_id=args.room,
        num_clients=args.clients,
    )

    try:
        simulator.start()
    except KeyboardInterrupt:
        logger.info("\nSimulation interrupted during startup...")
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
    finally:
        # Always try to stop the simulator cleanly
        try:
            if simulator.running:
                simulator.stop()
        except Exception as e:
            logger.error(f"Error during simulator shutdown: {e}")
        logger.info("Client simulator shutdown complete.")


if __name__ == "__main__":
    main()
