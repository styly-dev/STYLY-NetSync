"""
Locust load testing configuration for STYLY NetSync server.

This file defines the Locust user behavior for benchmarking the STYLY NetSync server.
It simulates VR/MR users performing typical actions like transform updates and RPC calls.

Usage:
    # Basic run with Web UI
    locust -f locustfile.py --host=tcp://localhost:5555

    # Headless run for CI/CD
    locust -f locustfile.py --host=tcp://localhost:5555 --headless -u 50 -r 5 -t 300s

    # With custom configuration
    STYLY_SERVER_ADDRESS=192.168.1.100 STYLY_ROOM_ID=load_test locust -f locustfile.py

Environment Variables:
    STYLY_SERVER_ADDRESS: Server address (default: localhost)
    STYLY_DEALER_PORT: DEALER port (default: 5555)
    STYLY_SUB_PORT: SUB port (default: 5556)
    STYLY_ROOM_ID: Room ID for testing (default: benchmark_room)
    STYLY_TRANSFORM_RATE: Transform update rate in Hz (default: 50.0)
    STYLY_RPC_INTERVAL: RPC send interval in seconds (default: 10.0)
"""

import logging
import time
import random
import os
from typing import Any, Dict

from locust import User, task, events, constant_pacing
from locust.exception import StopUser

from styly_client import STYLYNetSyncClient
from metrics_collector import MetricsCollector
from benchmark_config import config

logger = logging.getLogger(__name__)

# Global metrics collector for aggregating data across all users
global_metrics = MetricsCollector()

# Locust event handlers for custom metrics
@events.init.add_listener
def on_locust_init(environment, **kwargs):
    """Initialize benchmark environment."""
    logger.info("=== STYLY NetSync Benchmark Starting ===")
    logger.info(f"Server: {config.server_address}:{config.dealer_port}")
    logger.info(f"Room: {config.room_id}")
    logger.info(f"Transform rate: {config.transform_update_rate} Hz")
    logger.info(f"RPC interval: {config.rpc_send_interval}s")
    logger.info("=" * 50)

@events.test_stop.add_listener
def on_test_stop(environment, **kwargs):
    """Handle test completion."""
    logger.info("=== STYLY NetSync Benchmark Completed ===")
    global_metrics.log_stats_summary()
    
    # Export detailed metrics for analysis
    metrics_data = global_metrics.export_to_dict()
    
    # Save metrics to file if needed (non-blocking)
    try:
        import json
        import threading
        
        def save_metrics():
            try:
                # Ensure results directory exists
                results_dir = "results"
                os.makedirs(results_dir, exist_ok=True)
                
                # Save metrics to results directory
                filename = f"benchmark_results_{int(time.time())}.json"
                filepath = os.path.join(results_dir, filename)
                
                with open(filepath, "w") as f:
                    json.dump(metrics_data, f, indent=2)
                logger.info(f"Detailed metrics saved to {filepath}")
            except OSError as e:
                logger.error(f"Could not create results directory or save metrics file: {e}")
            except (IOError, json.JSONEncodeError) as e:
                logger.error(f"Could not save metrics file: {e}")
            except Exception as e:
                logger.error(f"Unexpected error saving metrics: {e}")
        
        # Save in background to avoid blocking shutdown
        threading.Thread(target=save_metrics, daemon=True).start()
        
    except Exception as e:
        logger.warning(f"Could not start metrics export: {e}")

@events.user_error.add_listener
def on_user_error(user_instance, exception, tb, **kwargs):
    """Handle user errors."""
    logger.error(f"User error: {exception}")
    global_metrics.record_connection_error()


class STYLYNetSyncUser(User):
    """
    Locust user that simulates a STYLY NetSync client.
    
    This user performs the following tasks:
    - Connects to the STYLY NetSync server
    - Sends transform updates at the configured rate
    - Sends RPC messages periodically
    - Receives and processes server responses
    """
    
    # Locust configuration
    wait_time = lambda self: random.uniform(config.min_wait_time / 1000.0, config.max_wait_time / 1000.0)
    
    def __init__(self, environment):
        super().__init__(environment)
        
        # Parse --host option to override server configuration
        if environment.host:
            self._parse_host_option(environment.host)
        
        self.client = None
        self.user_id = f"locust_user_{random.randint(1000, 9999)}"
        self.connected = False
        self.last_stats_log = 0
        
        # Task timing
        self.last_transform_time = 0
        self.transform_interval = 1.0 / config.transform_update_rate  # Convert Hz to seconds
        
        logger.info(f"STYLYNetSyncUser created: {self.user_id}")
    
    def _parse_host_option(self, host_string: str):
        """Parse Locust --host option and update config."""
        try:
            # Handle tcp://host:port format
            if host_string.startswith('tcp://'):
                host_string = host_string[6:]  # Remove 'tcp://'
            
            # Split host:port
            if ':' in host_string:
                host, port = host_string.split(':', 1)
                config.server_address = host
                config.dealer_port = int(port)
                # Assume SUB port is dealer_port + 1 (following server convention)
                config.sub_port = int(port) + 1
                logger.info(f"Updated server config from --host: {host}:{port}")
            else:
                config.server_address = host_string
                logger.info(f"Updated server address from --host: {host_string}")
                
        except Exception as e:
            logger.warning(f"Failed to parse --host option '{host_string}': {e}")
            logger.info("Using default server configuration")
    
    def on_start(self):
        """Called when a user starts."""
        try:
            # Create STYLY client with shared metrics collector
            self.client = STYLYNetSyncClient(
                user_id=self.user_id,
                metrics_collector=global_metrics
            )
            
            # Set callbacks for metrics recording
            self.client.on_transform_received = self._record_transform_received
            self.client.on_rpc_response_received = self._record_rpc_response_received
            
            # Connect to server
            if self.client.connect():
                self.connected = True
                logger.info(f"User {self.user_id} connected successfully")
            else:
                logger.error(f"User {self.user_id} failed to connect")
                raise StopUser("Failed to connect to server")
                
        except Exception as e:
            logger.error(f"Error starting user {self.user_id}: {e}")
            raise StopUser(f"Startup error: {e}")
    
    def _record_transform_received(self, client_count: int):
        """Record transform receive metrics in Locust."""
        try:
            self.environment.events.request.fire(
                request_type="STYLY",
                name="transform_receive",
                response_time=0,  # Receive operations don't have response time
                response_length=client_count,
                exception=None,
                context={}
            )
            
            if config.detailed_logging:
                logger.debug(f"Recorded transform receive: {client_count} clients")
                
        except Exception as e:
            logger.warning(f"Failed to record transform receive metrics: {e}")
    
    def _record_rpc_response_received(self, latency_ms: float, function_name: str, message_id: str):
        """Record RPC response latency in Locust metrics."""
        try:
            if latency_ms:
                self.environment.events.request.fire(
                    request_type="STYLY",
                    name="rpc_response",
                    response_time=latency_ms,
                    response_length=0,  # RPC responses don't have meaningful length
                    exception=None,
                    context={
                        "function_name": function_name,
                        "message_id": message_id,
                        "user_id": self.user_id
                    }
                )
                if config.detailed_logging:
                    logger.debug(f"Recorded RPC response latency: {latency_ms:.1f}ms for {function_name} (message_id: {message_id})")
            else:
                logger.info(f"no matching rpc data for message_id: {message_id}")
            
                
        except Exception as e:
            logger.warning(f"Failed to record RPC response metrics: {e}")
    
    def on_stop(self):
        """Called when a user stops."""
        try:
            if self.client:
                self.client.disconnect()
                logger.info(f"User {self.user_id} disconnected")
            self.connected = False
        except Exception as e:
            logger.error(f"Error stopping user {self.user_id}: {e}")
        finally:
            self.client = None
    
    wait_time = constant_pacing(1.0 / config.transform_update_rate)

    @task(100)  # High weight for frequent transform updates
    def send_transform_update(self):
        """Send transform update to server."""
        if not self.connected or not self.client:
            return
        
        start_time = time.time()
        
        try:
            success = self.client.send_transform_update()
            
            # Record Locust metrics
            response_time = (time.time() - start_time) * 1000  # Convert to milliseconds
            
            if success:
                self.environment.events.request.fire(
                    request_type="STYLY",
                    name="transform_update",
                    response_time=response_time,
                    response_length=0,  # We don't track response length for transforms
                    exception=None,
                    context={}
                )
            else:
                self.environment.events.request.fire(
                    request_type="STYLY",
                    name="transform_update",
                    response_time=response_time,
                    response_length=0,
                    exception=Exception("Transform update failed"),
                    context={}
                )
            
        except Exception as e:
            self.environment.events.request.fire(
                request_type="STYLY",
                name="transform_update",
                response_time=(time.time() - start_time) * 1000,
                response_length=0,
                exception=e,
                context={}
            )
    
    @task(5)  # Lower weight for RPC calls
    def send_rpc_message(self):
        """Send RPC message."""
        if not self.connected or not self.client:
            return
        
        start_time = time.time()
        
        try:
            # Single RPC type as per server implementation
            function_name = "benchmark_rpc_function"
            args = [f"arg_{i}" for i in range(random.randint(1, 3))]
            
            success = self.client.send_rpc(function_name, args)
            logger.debug(f"send_rpc success:{success}")
            
            response_time = (time.time() - start_time) * 1000
            
            if success:
                self.environment.events.request.fire(
                    request_type="STYLY",
                    name="rpc",
                    response_time=response_time,
                    response_length=0,
                    exception=None,
                    context={}
                )
            else:
                self.environment.events.request.fire(
                    request_type="STYLY",
                    name="rpc",
                    response_time=response_time,
                    response_length=0,
                    exception=Exception("RPC failed"),
                    context={}
                )
                
        except Exception as e:
            logger.error(f"send rpc exception: {e}")
            self.environment.events.request.fire(
                request_type="STYLY",
                name="rpc",
                response_time=(time.time() - start_time) * 1000,
                response_length=0,
                exception=e,
                context={}
            )
    
    @task(2)  # Low weight for periodic updates
    def perform_periodic_updates(self):
        """Perform periodic RPC updates."""
        if not self.connected or not self.client:
            return
        
        try:
            self.client.perform_periodic_updates()
        except Exception as e:
            logger.error(f"Error in periodic updates for {self.user_id}: {e}")
    
    @task(1)  # Very low weight for status logging
    def log_status(self):
        """Periodically log user status and metrics."""
        current_time = time.time()
        
        # Log status every 30 seconds
        if current_time - self.last_stats_log > 30:
            if self.client:
                summary = self.client.get_received_data_summary()
                logger.info(f"User {self.user_id} status: {summary}")
            
            self.last_stats_log = current_time



# Additional utility functions for custom metrics
def get_benchmark_stats() -> Dict[str, Any]:
    """Get current benchmark statistics."""
    return global_metrics.get_stats().__dict__


def export_benchmark_data() -> Dict[str, Any]:
    """Export all benchmark data for analysis."""
    return global_metrics.export_to_dict()


# Example of custom Locust web UI extension (optional)
@events.init_command_line_parser.add_listener
def _(parser):
    """Add custom command line arguments."""
    parser.add_argument("--styly-detailed-logging", action="store_true",
                       help="Enable detailed STYLY logging")
    parser.add_argument("--styly-export-metrics", type=str,
                       help="Export metrics to specified file")


@events.init.add_listener
def _(environment, **kwargs):
    """Handle custom command line arguments."""
    if environment.parsed_options and environment.parsed_options.styly_detailed_logging:
        config.detailed_logging = True
        logger.setLevel(logging.DEBUG)
        logger.info("Detailed logging enabled")
