"""
Interface definition for STYLY NetSync benchmark clients.

This module provides a common interface that allows switching between
different client implementations (raw ZeroMQ vs package-based).
"""

from abc import ABC, abstractmethod
from typing import Any, Dict, List, Optional, Tuple


class INetSyncClient(ABC):
    """
    Abstract interface for STYLY NetSync benchmark clients.
    
    All client implementations must provide these methods to ensure
    compatibility with the Locust benchmarking framework.
    """
    
    @abstractmethod
    def __init__(self, user_id: Optional[str] = None, metrics_collector: Optional[Any] = None):
        """
        Initialize the client.
        
        Args:
            user_id: Unique identifier for this client
            metrics_collector: Optional metrics collector instance
        """
        pass
    
    @abstractmethod
    def connect(self) -> bool:
        """
        Connect to the STYLY NetSync server.
        
        Returns:
            True if connection successful, False otherwise
        """
        pass
    
    @abstractmethod
    def disconnect(self) -> None:
        """
        Disconnect from the server and cleanup resources.
        """
        pass
    
    @abstractmethod
    def send_transform_update(self) -> bool:
        """
        Send a transform update to the server.
        
        Returns:
            True if send successful, False otherwise
        """
        pass
    
    @abstractmethod
    def send_rpc(self, function_name: str, args: List[Any]) -> bool:
        """
        Send an RPC message to the server.
        
        Args:
            function_name: Name of the RPC function
            args: Arguments for the RPC call
            
        Returns:
            True if send successful, False otherwise
        """
        pass
    
    @abstractmethod
    def get_received_data_summary(self) -> Dict[str, Any]:
        """
        Get a summary of received data for analysis.
        
        Returns:
            Dictionary containing received data statistics
        """
        pass
    
    # Note: These attributes should be present in implementations:
    # - user_id: str
    # - device_id: str  
    # - client_no: Optional[int]
    # - connected: bool
    
    # Optional callbacks for metrics recording
    on_transform_received: Optional[Any] = None
    on_rpc_response_received: Optional[Any] = None


class ClientMetrics:
    """
    Standard metrics structure for client performance tracking.
    """
    
    def __init__(self):
        self.transforms_sent = 0
        self.transforms_received = 0
        self.rpcs_sent = 0
        self.rpcs_received = 0
        self.connection_errors = 0
        self.last_latency_ms = 0.0
        
    def to_dict(self) -> Dict[str, Any]:
        """Convert metrics to dictionary format."""
        return {
            'transforms_sent': self.transforms_sent,
            'transforms_received': self.transforms_received,
            'rpcs_sent': self.rpcs_sent,
            'rpcs_received': self.rpcs_received,
            'connection_errors': self.connection_errors,
            'last_latency_ms': self.last_latency_ms,
        }