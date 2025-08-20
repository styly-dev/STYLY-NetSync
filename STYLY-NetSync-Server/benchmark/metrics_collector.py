"""
Metrics collection and analysis for STYLY NetSync benchmarks.
"""

import time
import threading
from collections import defaultdict, deque
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple
import statistics
import logging

logger = logging.getLogger(__name__)


@dataclass
class LatencyMetric:
    """Latency measurement data."""
    timestamp: float
    latency_ms: float
    message_type: str


@dataclass
class ThroughputMetric:
    """Throughput measurement data."""
    timestamp: float
    messages_sent: int
    messages_received: int
    bytes_sent: int
    bytes_received: int


@dataclass
class PacketLossMetric:
    """Packet loss measurement data."""
    timestamp: float
    sent_count: int
    received_count: int
    loss_rate: float


@dataclass
class MessageResult:
    """Result of recording a received message."""
    message_id: Optional[str]
    message_type: str
    message_size: int
    timestamp: float
    latency_ms: Optional[float] = None  # None if no latency could be calculated
    had_pending_measurement: bool = False  # True if we found a matching sent message


@dataclass
class BenchmarkStats:
    """Aggregated benchmark statistics."""
    # Latency statistics
    avg_latency_ms: float = 0.0
    min_latency_ms: float = 0.0
    max_latency_ms: float = 0.0
    p95_latency_ms: float = 0.0
    p99_latency_ms: float = 0.0
    
    # Throughput statistics
    avg_messages_per_sec: float = 0.0
    avg_bytes_per_sec: float = 0.0
    peak_messages_per_sec: float = 0.0
    peak_bytes_per_sec: float = 0.0
    
    # Packet loss statistics
    overall_packet_loss_rate: float = 0.0
    total_messages_sent: int = 0
    total_messages_received: int = 0
    
    # Connection statistics
    connection_errors: int = 0
    reconnection_count: int = 0
    
    # Test duration
    test_duration_seconds: float = 0.0


class MetricsCollector:
    """Collects and analyzes performance metrics for STYLY NetSync benchmarks."""
    
    def __init__(self, window_size_seconds: int = 60):
        self.window_size_seconds = window_size_seconds
        self.start_time = time.time()
        
        # Thread-safe collections
        self._lock = threading.RLock()
        
        # Metrics storage with time-based windows
        self.latency_metrics: deque = deque(maxlen=10000)
        self.throughput_metrics: deque = deque(maxlen=1000)
        self.packet_loss_metrics: deque = deque(maxlen=1000)
        
        # Real-time counters
        self.messages_sent_counter = 0
        self.messages_received_counter = 0
        self.bytes_sent_counter = 0
        self.bytes_received_counter = 0
        self.connection_errors_counter = 0
        self.reconnection_counter = 0
        
        # Pending latency measurements (message_id -> timestamp)
        self.pending_latency_measurements: Dict[str, Tuple[float, str]] = {}
        
        # Per-message-type statistics
        self.message_type_stats: Dict[str, List[float]] = defaultdict(list)
        
        logger.info(f"MetricsCollector initialized with {window_size_seconds}s window")
    
    def record_message_sent(self, message_id: str, message_type: str, message_size: int):
        """Record a message being sent."""
        with self._lock:
            current_time = time.time()
            self.messages_sent_counter += 1
            self.bytes_sent_counter += message_size
            
            # Store for latency measurement
            self.pending_latency_measurements[message_id] = (current_time, message_type)
            
            # Clean up old pending measurements (older than 10 seconds)
            cutoff_time = current_time - 10.0
            expired_ids = [
                msg_id for msg_id, (timestamp, _) in self.pending_latency_measurements.items()
                if timestamp < cutoff_time
            ]
            for msg_id in expired_ids:
                del self.pending_latency_measurements[msg_id]
    
    def record_message_received(self, message_id: Optional[str], message_type: str, message_size: int) -> MessageResult:
        """Record a message being received and return the result."""
        with self._lock:
            current_time = time.time()
            self.messages_received_counter += 1
            self.bytes_received_counter += message_size
            
            latency_ms = None
            had_pending_measurement = False
            
            # Calculate latency if we have the sent timestamp
            if message_id and message_id in self.pending_latency_measurements:
                sent_time, sent_type = self.pending_latency_measurements.pop(message_id)
                latency_ms = (current_time - sent_time) * 1000
                had_pending_measurement = True
                
                # Record latency metric
                latency_metric = LatencyMetric(
                    timestamp=current_time,
                    latency_ms=latency_ms,
                    message_type=sent_type
                )
                self.latency_metrics.append(latency_metric)
                self.message_type_stats[sent_type].append(latency_ms)
            
            # Return structured result
            return MessageResult(
                message_id=message_id,
                message_type=message_type,
                message_size=message_size,
                timestamp=current_time,
                latency_ms=latency_ms,
                had_pending_measurement=had_pending_measurement
            )
    
    def record_connection_error(self):
        """Record a connection error."""
        with self._lock:
            self.connection_errors_counter += 1
    
    def record_reconnection(self):
        """Record a reconnection event."""
        with self._lock:
            self.reconnection_counter += 1
    
    def calculate_throughput_snapshot(self) -> ThroughputMetric:
        """Calculate current throughput snapshot."""
        with self._lock:
            current_time = time.time()
            return ThroughputMetric(
                timestamp=current_time,
                messages_sent=self.messages_sent_counter,
                messages_received=self.messages_received_counter,
                bytes_sent=self.bytes_sent_counter,
                bytes_received=self.bytes_received_counter
            )
    
    def calculate_packet_loss_snapshot(self) -> PacketLossMetric:
        """Calculate current packet loss snapshot."""
        with self._lock:
            current_time = time.time()
            sent = self.messages_sent_counter
            received = self.messages_received_counter
            loss_rate = (sent - received) / sent if sent > 0 else 0.0
            
            return PacketLossMetric(
                timestamp=current_time,
                sent_count=sent,
                received_count=received,
                loss_rate=max(0.0, loss_rate)  # Ensure non-negative
            )
    
    def get_recent_latencies(self, seconds: int = 60) -> List[float]:
        """Get latency measurements from the last N seconds."""
        with self._lock:
            cutoff_time = time.time() - seconds
            return [
                metric.latency_ms for metric in self.latency_metrics
                if metric.timestamp >= cutoff_time
            ]
    
    def get_stats(self) -> BenchmarkStats:
        """Calculate comprehensive benchmark statistics."""
        with self._lock:
            current_time = time.time()
            test_duration = current_time - self.start_time
            
            # Latency statistics
            recent_latencies = self.get_recent_latencies(self.window_size_seconds)
            if recent_latencies:
                avg_latency = statistics.mean(recent_latencies)
                min_latency = min(recent_latencies)
                max_latency = max(recent_latencies)
                p95_latency = statistics.quantiles(recent_latencies, n=20)[18]  # 95th percentile
                p99_latency = statistics.quantiles(recent_latencies, n=100)[98]  # 99th percentile
            else:
                avg_latency = min_latency = max_latency = p95_latency = p99_latency = 0.0
            
            # Throughput statistics
            if test_duration > 0:
                avg_messages_per_sec = self.messages_received_counter / test_duration
                avg_bytes_per_sec = self.bytes_received_counter / test_duration
            else:
                avg_messages_per_sec = avg_bytes_per_sec = 0.0
            
            # Calculate peak throughput from recent measurements
            recent_throughput = [
                metric for metric in self.throughput_metrics
                if metric.timestamp >= current_time - self.window_size_seconds
            ]
            
            if len(recent_throughput) >= 2:
                # Calculate rate between consecutive measurements
                rates = []
                for i in range(1, len(recent_throughput)):
                    prev = recent_throughput[i-1]
                    curr = recent_throughput[i]
                    time_diff = curr.timestamp - prev.timestamp
                    if time_diff > 0:
                        msg_rate = (curr.messages_received - prev.messages_received) / time_diff
                        byte_rate = (curr.bytes_received - prev.bytes_received) / time_diff
                        rates.append((msg_rate, byte_rate))
                
                if rates:
                    peak_messages_per_sec = max(rate[0] for rate in rates)
                    peak_bytes_per_sec = max(rate[1] for rate in rates)
                else:
                    peak_messages_per_sec = peak_bytes_per_sec = 0.0
            else:
                peak_messages_per_sec = avg_messages_per_sec
                peak_bytes_per_sec = avg_bytes_per_sec
            
            # Packet loss statistics
            packet_loss_snapshot = self.calculate_packet_loss_snapshot()
            
            return BenchmarkStats(
                avg_latency_ms=avg_latency,
                min_latency_ms=min_latency,
                max_latency_ms=max_latency,
                p95_latency_ms=p95_latency,
                p99_latency_ms=p99_latency,
                avg_messages_per_sec=avg_messages_per_sec,
                avg_bytes_per_sec=avg_bytes_per_sec,
                peak_messages_per_sec=peak_messages_per_sec,
                peak_bytes_per_sec=peak_bytes_per_sec,
                overall_packet_loss_rate=packet_loss_snapshot.loss_rate,
                total_messages_sent=self.messages_sent_counter,
                total_messages_received=self.messages_received_counter,
                connection_errors=self.connection_errors_counter,
                reconnection_count=self.reconnection_counter,
                test_duration_seconds=test_duration
            )
    
    def log_stats_summary(self):
        """Log a summary of current statistics."""
        stats = self.get_stats()
        logger.info("=== STYLY NetSync Benchmark Statistics ===")
        logger.info(f"Test Duration: {stats.test_duration_seconds:.1f}s")
        logger.info(f"Messages: {stats.total_messages_sent} sent, {stats.total_messages_received} received")
        logger.info(f"Packet Loss: {stats.overall_packet_loss_rate:.2%}")
        logger.info(f"Latency: avg={stats.avg_latency_ms:.1f}ms, p95={stats.p95_latency_ms:.1f}ms, p99={stats.p99_latency_ms:.1f}ms")
        logger.info(f"Throughput: avg={stats.avg_messages_per_sec:.1f} msg/s, peak={stats.peak_messages_per_sec:.1f} msg/s")
        logger.info(f"Bandwidth: avg={stats.avg_bytes_per_sec/1024:.1f} KB/s, peak={stats.peak_bytes_per_sec/1024:.1f} KB/s")
        logger.info(f"Errors: {stats.connection_errors} connection errors, {stats.reconnection_count} reconnections")
        
        # Log RPC latency details
        self.log_rpc_latency_summary()
        
        logger.info("==========================================")
    
    def log_rpc_latency_summary(self):
        """Log detailed RPC latency statistics."""
        with self._lock:
            # Get RPC latencies from the last 60 seconds
            current_time = time.time()
            cutoff_time = current_time - 60.0
            
            rpc_latencies = [
                metric.latency_ms for metric in self.latency_metrics
                if metric.timestamp >= cutoff_time and metric.message_type == "rpc"
            ]
            
            if rpc_latencies:
                avg_rpc_latency = statistics.mean(rpc_latencies)
                min_rpc_latency = min(rpc_latencies)
                max_rpc_latency = max(rpc_latencies)
                
                if len(rpc_latencies) >= 2:
                    p95_rpc_latency = statistics.quantiles(rpc_latencies, n=20)[18] if len(rpc_latencies) >= 20 else max_rpc_latency
                    p99_rpc_latency = statistics.quantiles(rpc_latencies, n=100)[98] if len(rpc_latencies) >= 100 else max_rpc_latency
                else:
                    p95_rpc_latency = p99_rpc_latency = avg_rpc_latency
                
                logger.info(f"RPC Latency (last 60s): count={len(rpc_latencies)}, "
                          f"avg={avg_rpc_latency:.1f}ms, min={min_rpc_latency:.1f}ms, max={max_rpc_latency:.1f}ms, "
                          f"p95={p95_rpc_latency:.1f}ms, p99={p99_rpc_latency:.1f}ms")
            else:
                logger.info("RPC Latency: No RPC latency measurements available")
            
            # Log pending measurement count
            logger.info(f"Pending measurements: {len(self.pending_latency_measurements)} messages awaiting response")
    
    def export_to_dict(self) -> Dict:
        """Export all metrics to a dictionary for external analysis."""
        with self._lock:
            return {
                'stats': self.get_stats().__dict__,
                'latency_metrics': [
                    {
                        'timestamp': m.timestamp,
                        'latency_ms': m.latency_ms,
                        'message_type': m.message_type
                    }
                    for m in self.latency_metrics
                ],
                'throughput_metrics': [
                    {
                        'timestamp': m.timestamp,
                        'messages_sent': m.messages_sent,
                        'messages_received': m.messages_received,
                        'bytes_sent': m.bytes_sent,
                        'bytes_received': m.bytes_received
                    }
                    for m in self.throughput_metrics
                ],
                'packet_loss_metrics': [
                    {
                        'timestamp': m.timestamp,
                        'sent_count': m.sent_count,
                        'received_count': m.received_count,
                        'loss_rate': m.loss_rate
                    }
                    for m in self.packet_loss_metrics
                ],
                'message_type_stats': {
                    msg_type: {
                        'count': len(latencies),
                        'avg_latency_ms': statistics.mean(latencies) if latencies else 0,
                        'min_latency_ms': min(latencies) if latencies else 0,
                        'max_latency_ms': max(latencies) if latencies else 0
                    }
                    for msg_type, latencies in self.message_type_stats.items()
                }
            }
