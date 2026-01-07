"""Configuration management for STYLY NetSync Server.

This module provides TOML-based configuration support with CLI override capability.
"""

from __future__ import annotations

import argparse
import tomllib
from dataclasses import dataclass
from dataclasses import replace as dataclass_replace
from pathlib import Path
from typing import Any


@dataclass
class ServerConfig:
    """Server configuration with all settings.

    Configuration priority: CLI args > config file > defaults
    """

    # Network settings
    dealer_port: int = 5555
    pub_port: int = 5556
    server_discovery_port: int = 9999
    server_name: str = "STYLY-NetSync-Server"
    enable_server_discovery: bool = True

    # Timing settings
    base_broadcast_interval: float = 0.1  # 10Hz base rate
    idle_broadcast_interval: float = 0.5  # 2Hz when idle
    dirty_threshold: float = 0.05  # 20Hz max rate when very active
    client_timeout: float = 1.0  # 1 second timeout for client disconnect
    cleanup_interval: float = 1.0  # Cleanup every 1 second
    device_id_expiry_time: float = 300.0  # 5 minutes for device ID mapping expiry
    status_log_interval: float = 10.0  # Log status every 10 seconds
    main_loop_sleep: float = 0.02  # 50Hz main loop sleep
    poll_timeout: int = 100  # ZMQ poll timeout in ms

    # Network Variable settings
    max_global_vars: int = 100  # Maximum global variables per room
    max_client_vars: int = 100  # Maximum client variables per client
    max_var_name_length: int = 64  # Maximum variable name length in bytes
    max_var_value_length: int = 1024  # Maximum variable value length in bytes
    nv_flush_interval: float = 0.05  # NV flush cadence (50ms)
    nv_monitor_window_size: float = 1.0  # NV monitoring window (1 second)
    nv_monitor_threshold: int = 200  # NV requests/s before warning

    # Internal limits
    max_virtual_transforms: int = 50  # Maximum virtual transforms per client
    pub_queue_maxsize: int = 10000  # PUB queue maximum size
    delta_ring_size: int = 10000  # Delta ring buffer size for NV sync


# TOML section to config field mapping
_SECTION_MAPPING: dict[str, list[str]] = {
    "network": [
        "dealer_port",
        "pub_port",
        "server_discovery_port",
        "server_name",
        "enable_server_discovery",
    ],
    "timing": [
        "base_broadcast_interval",
        "idle_broadcast_interval",
        "dirty_threshold",
        "client_timeout",
        "cleanup_interval",
        "device_id_expiry_time",
        "status_log_interval",
        "main_loop_sleep",
        "poll_timeout",
    ],
    "network_variables": [
        "max_global_vars",
        "max_client_vars",
        "max_var_name_length",
        "max_var_value_length",
        "nv_flush_interval",
        "nv_monitor_window_size",
        "nv_monitor_threshold",
    ],
    "limits": [
        "max_virtual_transforms",
        "pub_queue_maxsize",
        "delta_ring_size",
    ],
}


def load_config_from_toml(path: Path) -> dict[str, Any]:
    """Load configuration from TOML file.

    Args:
        path: Path to the TOML configuration file.

    Returns:
        Parsed TOML data as a dictionary.

    Raises:
        FileNotFoundError: If the configuration file does not exist.
        tomllib.TOMLDecodeError: If the TOML syntax is invalid.
    """
    with open(path, "rb") as f:
        return tomllib.load(f)


def flatten_toml_config(toml_data: dict[str, Any]) -> dict[str, Any]:
    """Flatten nested TOML sections into a flat dictionary.

    Args:
        toml_data: Parsed TOML data with nested sections.

    Returns:
        Flat dictionary with config field names as keys.
    """
    flat: dict[str, Any] = {}

    for section, keys in _SECTION_MAPPING.items():
        if section in toml_data:
            for key in keys:
                if key in toml_data[section]:
                    flat[key] = toml_data[section][key]

    return flat


def validate_config(config: ServerConfig) -> list[str]:
    """Validate configuration values.

    Args:
        config: ServerConfig instance to validate.

    Returns:
        List of error messages. Empty list if configuration is valid.
    """
    errors: list[str] = []

    # Port validation (1-65535)
    port_fields = ["dealer_port", "pub_port", "server_discovery_port"]
    for field_name in port_fields:
        port = getattr(config, field_name)
        if not 1 <= port <= 65535:
            errors.append(f"{field_name} must be between 1 and 65535, got {port}")

    # Timing validation (must be positive)
    timing_fields = [
        "base_broadcast_interval",
        "idle_broadcast_interval",
        "dirty_threshold",
        "client_timeout",
        "cleanup_interval",
        "device_id_expiry_time",
        "status_log_interval",
        "main_loop_sleep",
    ]
    for field_name in timing_fields:
        value = getattr(config, field_name)
        if value <= 0:
            errors.append(f"{field_name} must be positive, got {value}")

    # poll_timeout must be positive integer
    if config.poll_timeout <= 0:
        errors.append(f"poll_timeout must be positive, got {config.poll_timeout}")

    # Network Variable limits validation (must be positive integers)
    nv_int_fields = [
        "max_global_vars",
        "max_client_vars",
        "max_var_name_length",
        "max_var_value_length",
        "nv_monitor_threshold",
    ]
    for field_name in nv_int_fields:
        value = getattr(config, field_name)
        if value <= 0:
            errors.append(f"{field_name} must be positive, got {value}")

    # NV timing validation (must be positive floats)
    nv_float_fields = ["nv_flush_interval", "nv_monitor_window_size"]
    for field_name in nv_float_fields:
        value = getattr(config, field_name)
        if value <= 0:
            errors.append(f"{field_name} must be positive, got {value}")

    # Internal limits validation (must be positive integers)
    limits_fields = ["max_virtual_transforms", "pub_queue_maxsize", "delta_ring_size"]
    for field_name in limits_fields:
        value = getattr(config, field_name)
        if value <= 0:
            errors.append(f"{field_name} must be positive, got {value}")

    return errors


def merge_cli_args(config: ServerConfig, args: argparse.Namespace) -> ServerConfig:
    """Merge CLI arguments into config (CLI takes precedence).

    Only overrides config values when CLI args are explicitly provided.

    Args:
        config: Base ServerConfig instance.
        args: Parsed CLI arguments.

    Returns:
        New ServerConfig with CLI overrides applied.
    """
    updates: dict[str, Any] = {}

    # Network settings from CLI
    if (
        hasattr(args, "server_discovery_port")
        and args.server_discovery_port is not None
    ):
        updates["server_discovery_port"] = args.server_discovery_port

    # Special handling for --no-server-discovery flag
    if hasattr(args, "no_server_discovery") and args.no_server_discovery:
        updates["enable_server_discovery"] = False

    if not updates:
        return config

    return dataclass_replace(config, **updates)


def create_config_from_args(
    args: argparse.Namespace,
) -> ServerConfig:
    """Create ServerConfig from CLI arguments, loading config file if specified.

    Args:
        args: Parsed CLI arguments (must have 'config' attribute for config file path).

    Returns:
        Configured ServerConfig instance.

    Raises:
        FileNotFoundError: If specified config file does not exist.
        tomllib.TOMLDecodeError: If config file has invalid TOML syntax.
        SystemExit: If configuration validation fails.
    """
    from loguru import logger

    config = ServerConfig()

    # Load from config file if specified
    if hasattr(args, "config") and args.config is not None:
        config_path = Path(args.config)
        toml_data = load_config_from_toml(config_path)
        flat_data = flatten_toml_config(toml_data)

        # Update config with TOML values
        config = dataclass_replace(config, **flat_data)
        logger.info(f"Loaded configuration from {config_path}")

    # Apply CLI overrides
    config = merge_cli_args(config, args)

    # Validate
    errors = validate_config(config)
    if errors:
        for error in errors:
            logger.error(f"Configuration error: {error}")
        raise SystemExit(1)

    return config
