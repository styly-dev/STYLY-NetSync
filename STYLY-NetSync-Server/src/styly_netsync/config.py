"""Configuration management for STYLY NetSync Server.

This module provides TOML-based configuration support with CLI override capability.

Configuration priority: CLI args > user config > default config
"""

from __future__ import annotations

import argparse
import importlib.resources
import tomllib
from dataclasses import dataclass, fields
from dataclasses import replace as dataclass_replace
from pathlib import Path
from typing import Any


class ConfigurationError(Exception):
    """Raised when configuration validation fails.

    Attributes:
        errors: List of validation error messages.
    """

    def __init__(self, errors: list[str]) -> None:
        self.errors = errors
        super().__init__(f"Configuration validation failed: {'; '.join(errors)}")


class DefaultConfigError(Exception):
    """Raised when default configuration cannot be loaded.

    This is a fatal error that prevents server startup.
    """

    def __init__(self, message: str) -> None:
        super().__init__(f"Failed to load default configuration: {message}")


@dataclass
class ServerConfig:
    """Server configuration with all settings.

    All fields are required. Default values are loaded from default.toml.
    Configuration priority: CLI args > user config > default config
    """

    # Network settings
    dealer_port: int
    pub_port: int
    server_discovery_port: int
    server_name: str
    enable_server_discovery: bool

    # Timing settings
    base_broadcast_interval: float
    idle_broadcast_interval: float
    dirty_threshold: float
    client_timeout: float
    cleanup_interval: float
    device_id_expiry_time: float
    status_log_interval: float
    main_loop_sleep: float
    poll_timeout: int

    # Network Variable settings
    max_global_vars: int
    max_client_vars: int
    max_var_name_length: int
    max_var_value_length: int
    nv_flush_interval: float
    nv_monitor_window_size: float
    nv_monitor_threshold: int

    # Internal limits
    max_virtual_transforms: int
    pub_queue_maxsize: int
    delta_ring_size: int

    # Logging settings
    log_dir: str | None
    log_level_console: str
    log_json_console: bool
    log_rotation: str | None
    log_retention: str | None


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
    "logging": [
        "log_dir",
        "log_level_console",
        "log_json_console",
        "log_rotation",
        "log_retention",
    ],
}


def load_default_toml_data() -> dict[str, Any]:
    """Load the default.toml data from the bundled package resource.

    Returns:
        Parsed TOML data as a dictionary.

    Raises:
        DefaultConfigError: If default.toml cannot be found or parsed.
    """
    try:
        # Python 3.9+ approach using importlib.resources
        files = importlib.resources.files("styly_netsync")
        default_toml = files.joinpath("default.toml")
        # Read the file content directly (works with zip archives too)
        content = default_toml.read_bytes()
        return tomllib.loads(content.decode("utf-8"))
    except FileNotFoundError as e:
        raise DefaultConfigError(f"default.toml not found in package: {e}") from e
    except tomllib.TOMLDecodeError as e:
        raise DefaultConfigError(f"Invalid TOML syntax in default.toml: {e}") from e
    except Exception as e:
        raise DefaultConfigError(f"Failed to read default.toml: {e}") from e


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
                    value = toml_data[section][key]
                    # Convert empty strings to None for optional fields
                    if key in ("log_dir", "log_rotation", "log_retention"):
                        if value == "":
                            value = None
                    flat[key] = value

    return flat


def get_unknown_keys(toml_data: dict[str, Any]) -> dict[str, list[str]]:
    """Detect unknown sections and keys in TOML configuration.

    Args:
        toml_data: Parsed TOML data with nested sections.

    Returns:
        Dictionary mapping section names to lists of unknown keys.
        Unknown sections are reported with key "_unknown_section".
    """
    unknown: dict[str, list[str]] = {}

    for section, values in toml_data.items():
        if not isinstance(values, dict):
            # Top-level keys outside sections are not supported
            if "_root" not in unknown:
                unknown["_root"] = []
            unknown["_root"].append(section)
            continue

        if section not in _SECTION_MAPPING:
            # Unknown section
            unknown[section] = ["_unknown_section"]
        else:
            # Check for unknown keys within known section
            known_keys = set(_SECTION_MAPPING[section])
            for key in values:
                if key not in known_keys:
                    if section not in unknown:
                        unknown[section] = []
                    unknown[section].append(key)

    return unknown


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

    # Logging validation
    valid_log_levels = ["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"]
    if config.log_level_console.upper() not in valid_log_levels:
        errors.append(
            f"log_level_console must be one of {valid_log_levels}, "
            f"got {config.log_level_console}"
        )

    return errors


def load_default_config() -> ServerConfig:
    """Load the default configuration from the bundled default.toml.

    Returns:
        ServerConfig instance with default values.

    Raises:
        DefaultConfigError: If default.toml cannot be loaded or is incomplete.
    """
    try:
        toml_data = load_default_toml_data()
        flat_data = flatten_toml_config(toml_data)

        # Verify all required fields are present
        config_fields = {f.name for f in fields(ServerConfig)}
        missing = config_fields - set(flat_data.keys())
        if missing:
            raise DefaultConfigError(
                f"Missing required fields in default.toml: {', '.join(sorted(missing))}"
            )

        return ServerConfig(**flat_data)
    except DefaultConfigError:
        # Re-raise DefaultConfigError as-is
        raise
    except TypeError as e:
        raise DefaultConfigError(f"Invalid field types in default.toml: {e}") from e


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

    # Logging settings from CLI
    if hasattr(args, "log_dir") and args.log_dir is not None:
        updates["log_dir"] = str(args.log_dir)
    if hasattr(args, "log_level_console") and args.log_level_console is not None:
        updates["log_level_console"] = args.log_level_console
    if hasattr(args, "log_json_console") and args.log_json_console:
        updates["log_json_console"] = True
    if hasattr(args, "log_rotation") and args.log_rotation is not None:
        updates["log_rotation"] = args.log_rotation
    if hasattr(args, "log_retention") and args.log_retention is not None:
        updates["log_retention"] = args.log_retention

    if not updates:
        return config

    return dataclass_replace(config, **updates)


def create_config_from_args(
    args: argparse.Namespace,
) -> ServerConfig:
    """Create ServerConfig from CLI arguments with layered config loading.

    Configuration priority: CLI args > user config > default config

    Args:
        args: Parsed CLI arguments (may have 'user_config' attribute for user config path).

    Returns:
        Configured ServerConfig instance.

    Raises:
        DefaultConfigError: If default.toml cannot be loaded (fatal).
        FileNotFoundError: If specified user config file does not exist.
        tomllib.TOMLDecodeError: If config file has invalid TOML syntax.
        ConfigurationError: If configuration validation fails.
    """
    # Step 1: Load default configuration (required)
    config = load_default_config()

    # Step 2: Override with user config if specified
    if hasattr(args, "user_config") and args.user_config is not None:
        user_config_path = Path(args.user_config)
        toml_data = load_config_from_toml(user_config_path)

        # Warn about unknown keys (possible typos)
        unknown = get_unknown_keys(toml_data)
        if unknown:
            print(f"WARNING: Unknown keys in {user_config_path}:")
            for section, keys in unknown.items():
                if "_unknown_section" in keys:
                    print(f"  - Unknown section: [{section}]")
                else:
                    for key in keys:
                        print(f"  - [{section}] unknown key: {key}")

        flat_data = flatten_toml_config(toml_data)

        # Apply user config overrides
        if flat_data:
            config = dataclass_replace(config, **flat_data)

    # Step 3: Apply CLI overrides (highest priority)
    config = merge_cli_args(config, args)

    # Step 4: Validate final configuration
    errors = validate_config(config)
    if errors:
        raise ConfigurationError(errors)

    return config
