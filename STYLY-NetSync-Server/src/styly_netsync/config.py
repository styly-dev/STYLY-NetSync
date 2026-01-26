"""Configuration management for STYLY NetSync Server.

This module provides TOML-based configuration support with CLI override capability.

Configuration priority: CLI args > user config > default config
"""

from __future__ import annotations

import argparse
import importlib.resources
import sys
import tomllib
from dataclasses import dataclass, fields
from dataclasses import replace as dataclass_replace
from pathlib import Path
from typing import Any, NamedTuple


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


class ConfigOverride(NamedTuple):
    """Represents a configuration value override.

    Attributes:
        key: The configuration field name.
        default_value: The default value from default.toml.
        new_value: The new value from user config or CLI.
    """

    key: str
    default_value: Any
    new_value: Any


@dataclass
class ServerConfig:
    """Server configuration with all settings.

    All fields are required. Default values are loaded from default.toml.
    Configuration priority: CLI args > user config > default config
    """

    # Network settings
    dealer_port: int
    pub_port: int
    transform_pub_port: int
    state_pub_port: int
    server_discovery_port: int
    server_name: str
    enable_server_discovery: bool

    # Timing settings
    idle_broadcast_interval: float
    transform_broadcast_rate: int
    state_broadcast_rate_hz: int
    client_timeout: float
    heartbeat_timeout: float
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

    # RPC settings
    rpc_retry_initial_ms: int
    rpc_retry_max_ms: int
    rpc_retry_max_attempts: int
    rpc_outbox_max_per_client: int

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


# Valid config keys (for unknown key detection)
_VALID_KEYS: set[str] = {
    # Network settings
    "dealer_port",
    "pub_port",
    "transform_pub_port",
    "state_pub_port",
    "server_discovery_port",
    "server_name",
    "enable_server_discovery",
    # Timing settings
    "idle_broadcast_interval",
    "transform_broadcast_rate",
    "state_broadcast_rate_hz",
    "client_timeout",
    "heartbeat_timeout",
    "cleanup_interval",
    "device_id_expiry_time",
    "status_log_interval",
    "main_loop_sleep",
    "poll_timeout",
    # Network Variable settings
    "max_global_vars",
    "max_client_vars",
    "max_var_name_length",
    "max_var_value_length",
    "nv_flush_interval",
    "nv_monitor_window_size",
    "nv_monitor_threshold",
    # RPC settings
    "rpc_retry_initial_ms",
    "rpc_retry_max_ms",
    "rpc_retry_max_attempts",
    "rpc_outbox_max_per_client",
    # Internal limits
    "max_virtual_transforms",
    "pub_queue_maxsize",
    "delta_ring_size",
    # Logging settings
    "log_dir",
    "log_level_console",
    "log_json_console",
    "log_rotation",
    "log_retention",
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


def process_toml_config(toml_data: dict[str, Any]) -> dict[str, Any]:
    """Process TOML config data into ServerConfig-compatible dictionary.

    Args:
        toml_data: Parsed TOML data (flat structure).

    Returns:
        Dictionary with config field names as keys.
    """
    result: dict[str, Any] = {}

    for key, value in toml_data.items():
        if key in _VALID_KEYS:
            # Convert empty strings to None for optional fields
            if key in ("log_dir", "log_rotation", "log_retention"):
                if value == "":
                    value = None
            result[key] = value

    return result


def get_unknown_keys(toml_data: dict[str, Any]) -> list[str]:
    """Detect unknown keys in TOML configuration.

    Args:
        toml_data: Parsed TOML data (flat structure).

    Returns:
        List of unknown key names.
    """
    unknown: list[str] = []

    for key in toml_data:
        if key not in _VALID_KEYS:
            unknown.append(key)

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
    port_fields = [
        "dealer_port",
        "pub_port",
        "transform_pub_port",
        "state_pub_port",
        "server_discovery_port",
    ]
    for field_name in port_fields:
        port = getattr(config, field_name)
        if not 1 <= port <= 65535:
            errors.append(f"{field_name} must be between 1 and 65535, got {port}")

    # Timing validation (must be positive)
    timing_fields = [
        "idle_broadcast_interval",
        "client_timeout",
        "heartbeat_timeout",
        "cleanup_interval",
        "device_id_expiry_time",
        "status_log_interval",
        "main_loop_sleep",
    ]
    for field_name in timing_fields:
        value = getattr(config, field_name)
        if value <= 0:
            errors.append(f"{field_name} must be positive, got {value}")

    # Transform broadcast rate validation (0.5-60 Hz range)
    if not 0.5 <= config.transform_broadcast_rate <= 60:
        errors.append(
            f"transform_broadcast_rate must be between 0.5 and 60 Hz, "
            f"got {config.transform_broadcast_rate}"
        )
    else:
        # Cross-field validation for timing values (only if rate is in valid range)
        # Convert transform_broadcast_rate (Hz) to interval (seconds) for comparison
        broadcast_interval = 1.0 / config.transform_broadcast_rate
        if broadcast_interval > config.idle_broadcast_interval:
            errors.append(
                f"transform_broadcast_rate ({config.transform_broadcast_rate} Hz = "
                f"{broadcast_interval:.3f}s interval) results in slower broadcast than "
                f"idle_broadcast_interval ({config.idle_broadcast_interval}s)"
            )

    # State broadcast rate validation (0.1-60 Hz range)
    if not 0.1 <= config.state_broadcast_rate_hz <= 60:
        errors.append(
            f"state_broadcast_rate_hz must be between 0.1 and 60 Hz, "
            f"got {config.state_broadcast_rate_hz}"
        )

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

    # RPC settings validation (must be positive integers)
    rpc_fields = [
        "rpc_retry_initial_ms",
        "rpc_retry_max_ms",
        "rpc_retry_max_attempts",
        "rpc_outbox_max_per_client",
    ]
    for field_name in rpc_fields:
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
        config_data = process_toml_config(toml_data)

        # Verify all required fields are present
        config_fields = {f.name for f in fields(ServerConfig)}
        missing = config_fields - set(config_data.keys())
        if missing:
            raise DefaultConfigError(
                f"Missing required fields in default.toml: {', '.join(sorted(missing))}"
            )

        return ServerConfig(**config_data)
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
) -> tuple[ServerConfig, list[ConfigOverride]]:
    """Create ServerConfig from CLI arguments with layered config loading.

    Configuration priority: CLI args > user config > default config

    Args:
        args: Parsed CLI arguments (may have 'config' attribute for user config path).

    Returns:
        Tuple of (ServerConfig instance, list of ConfigOverride).
        The overrides list contains all values from user config that differ from defaults.

    Raises:
        DefaultConfigError: If default.toml cannot be loaded (fatal).
        FileNotFoundError: If specified user config file does not exist.
        tomllib.TOMLDecodeError: If config file has invalid TOML syntax.
        ConfigurationError: If configuration validation fails.
    """
    # Step 1: Load default configuration (required)
    config = load_default_config()
    overrides: list[ConfigOverride] = []

    # Step 2: Override with user config if specified
    if hasattr(args, "config") and args.config is not None:
        user_config_path = Path(args.config)
        toml_data = load_config_from_toml(user_config_path)

        # Warn about unknown keys (possible typos)
        # Using stderr since logging is not configured yet
        unknown = get_unknown_keys(toml_data)
        if unknown:
            print(f"WARNING: Unknown keys in {user_config_path}:", file=sys.stderr)
            for key in unknown:
                print(f"  - {key}", file=sys.stderr)

        config_data = process_toml_config(toml_data)

        # Apply user config overrides
        if config_data:
            # Track what values are being overridden (compare with existing config)
            for key, new_value in config_data.items():
                default_value = getattr(config, key)
                if default_value != new_value:
                    overrides.append(ConfigOverride(key, default_value, new_value))

            config = dataclass_replace(config, **config_data)

    # Step 3: Apply CLI overrides (highest priority)
    config = merge_cli_args(config, args)

    # Step 4: Validate final configuration
    errors = validate_config(config)
    if errors:
        raise ConfigurationError(errors)

    return config, overrides
