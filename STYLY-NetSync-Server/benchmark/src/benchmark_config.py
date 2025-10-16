"""
Benchmark configuration settings for STYLY NetSync load testing.
"""

import os
from dataclasses import dataclass
from typing import Optional


@dataclass
class BenchmarkConfig:
    """Configuration for STYLY NetSync benchmark tests."""
    
    # Client implementation type
    client_type: str = "netsync_manager"  # "raw_zmq" or "netsync_manager"
    
    # Server connection settings
    server_address: str = "localhost"
    dealer_port: int = 5555
    sub_port: int = 5556
    room_id: str = "benchmark_room"
    
    # Test parameters
    transform_update_rate: float = 30.0  # Hz
    rpc_per_transform_sends: int = 10  # Send RPC every N transform sends (30Hz / 10 = 3Hz)
    
    # Movement simulation
    movement_radius: float = 5.0
    movement_speed: float = 1.0
    
    # Performance monitoring
    latency_measurement_enabled: bool = True
    throughput_measurement_enabled: bool = True
    packet_loss_measurement_enabled: bool = True
    
    # Locust settings
    min_wait_time: int = 10  # milliseconds
    max_wait_time: int = 50  # milliseconds
    
    # Logging
    detailed_logging: bool = True
    
    @classmethod
    def from_env(cls) -> "BenchmarkConfig":
        """Create configuration from environment variables."""
        return cls(
            client_type=os.getenv("STYLY_CLIENT_TYPE", "netsync_manager"),
            server_address=os.getenv("STYLY_SERVER_ADDRESS", "localhost"),
            dealer_port=int(os.getenv("STYLY_DEALER_PORT", "5555")),
            sub_port=int(os.getenv("STYLY_SUB_PORT", "5556")),
            room_id=os.getenv("STYLY_ROOM_ID", "benchmark_room"),
            transform_update_rate=float(os.getenv("STYLY_TRANSFORM_RATE", "30.0")),
            rpc_per_transform_sends=int(os.getenv("STYLY_RPC_PER_TRANSFORMS", "10")),
            movement_radius=float(os.getenv("STYLY_MOVEMENT_RADIUS", "5.0")),
            movement_speed=float(os.getenv("STYLY_MOVEMENT_SPEED", "1.0")),
            latency_measurement_enabled=os.getenv("STYLY_MEASURE_LATENCY", "true").lower() == "true",
            throughput_measurement_enabled=os.getenv("STYLY_MEASURE_THROUGHPUT", "true").lower() == "true",
            packet_loss_measurement_enabled=os.getenv("STYLY_MEASURE_PACKET_LOSS", "true").lower() == "true",
            min_wait_time=int(os.getenv("STYLY_MIN_WAIT", "10")),
            max_wait_time=int(os.getenv("STYLY_MAX_WAIT", "50")),
            detailed_logging=os.getenv("STYLY_DETAILED_LOGGING", "false").lower() == "true",
        )


# Global configuration instance
config = BenchmarkConfig.from_env()