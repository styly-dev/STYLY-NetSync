"""
Factory for creating NetSync client instances based on configuration.

This module provides a factory pattern for selecting between different
client implementations (raw ZeroMQ vs net_sync_manager).
"""

import logging
from enum import Enum
from typing import Optional, Any

from client_interface import INetSyncClient
from styly_client import RawZMQClient
from netsync_manager_client import NetSyncManagerClient
from metrics_collector import MetricsCollector

logger = logging.getLogger(__name__)


class ClientType(Enum):
    """Available client implementation types."""
    RAW_ZMQ = "raw_zmq"
    NETSYNC_MANAGER = "netsync_manager"
    
    @classmethod
    def from_string(cls, value: str) -> "ClientType":
        """Convert string to ClientType enum."""
        value = value.lower().strip()
        if value in ["raw", "raw_zmq", "rawzmq"]:
            return cls.RAW_ZMQ
        elif value in ["manager", "netsync_manager", "netsyncmanager", "package", "sdk"]:
            return cls.NETSYNC_MANAGER
        else:
            logger.warning(f"Unknown client type '{value}', defaulting to RAW_ZMQ")
            return cls.RAW_ZMQ


class ClientFactory:
    """Factory for creating NetSync client instances."""
    
    @staticmethod
    def create_client(
        client_type: ClientType,
        user_id: Optional[str] = None,
        metrics_collector: Optional[MetricsCollector] = None
    ) -> INetSyncClient:
        """
        Create a client instance based on the specified type.
        
        Args:
            client_type: Type of client to create
            user_id: Optional user ID for the client
            metrics_collector: Optional metrics collector instance
            
        Returns:
            INetSyncClient: Client instance implementing the INetSyncClient interface
        """
        if client_type == ClientType.RAW_ZMQ:
            logger.info("Creating RawZMQClient (direct ZeroMQ implementation)")
            return RawZMQClient(user_id=user_id, metrics_collector=metrics_collector)
        
        elif client_type == ClientType.NETSYNC_MANAGER:
            logger.info("Creating NetSyncManagerClient (net_sync_manager-based implementation)")
            return NetSyncManagerClient(user_id=user_id, metrics_collector=metrics_collector)
        
        else:
            raise ValueError(f"Unknown client type: {client_type}")
    
    @staticmethod
    def get_available_types() -> list[str]:
        """Get list of available client type strings."""
        return [ct.value for ct in ClientType]