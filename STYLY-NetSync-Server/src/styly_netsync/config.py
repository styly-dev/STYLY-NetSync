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


# TOML section to config field mapping
_SECTION_MAPPING: dict[str, list[str]] = {
    "network": [
        "dealer_port",
        "pub_port",
        "server_discovery_port",
        "server_name",
        "enable_server_discovery",
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
