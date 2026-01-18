"""Tests for configuration module."""

import argparse
from pathlib import Path

import pytest

from styly_netsync.config import (
    ConfigurationError,
    DefaultConfigError,
    ServerConfig,
    create_config_from_args,
    get_unknown_keys,
    load_config_from_toml,
    load_default_config,
    merge_cli_args,
    process_toml_config,
    validate_config,
)


@pytest.fixture
def default_config() -> ServerConfig:
    """Load default configuration for tests."""
    return load_default_config()


class TestLoadDefaultConfig:
    """Tests for load_default_config function."""

    def test_load_default_config_succeeds(self) -> None:
        """Test that default configuration loads successfully."""
        config = load_default_config()
        assert isinstance(config, ServerConfig)

    def test_default_config_has_all_fields(self) -> None:
        """Test that default config has all expected fields."""
        config = load_default_config()
        # Network
        assert config.dealer_port == 5555
        assert config.pub_port == 5556
        assert config.server_discovery_port == 9999
        assert config.server_name == "STYLY-NetSync-Server"
        assert config.enable_server_discovery is True
        # Timing
        assert config.base_broadcast_interval == 0.1
        assert config.idle_broadcast_interval == 0.5
        assert config.dirty_threshold == 0.05
        assert config.client_timeout == 1.0
        assert config.cleanup_interval == 1.0
        assert config.device_id_expiry_time == 300.0
        assert config.status_log_interval == 10.0
        assert config.main_loop_sleep == 0.02
        assert config.poll_timeout == 100
        # NV
        assert config.max_global_vars == 100
        assert config.max_client_vars == 100
        assert config.max_var_name_length == 64
        assert config.max_var_value_length == 1024
        assert config.nv_flush_interval == 0.05
        assert config.nv_monitor_window_size == 1.0
        assert config.nv_monitor_threshold == 200
        # Limits
        assert config.max_virtual_transforms == 50
        assert config.pub_queue_maxsize == 10000
        assert config.delta_ring_size == 10000
        # Logging
        assert config.log_dir is None
        assert config.log_level_console == "INFO"
        assert config.log_json_console is False
        assert config.log_rotation is None
        assert config.log_retention is None

    def test_default_config_is_valid(self) -> None:
        """Test that default configuration passes validation."""
        config = load_default_config()
        errors = validate_config(config)
        assert errors == []


class TestServerConfig:
    """Tests for ServerConfig dataclass."""

    def test_custom_values(self, default_config: ServerConfig) -> None:
        """Test that custom values can be set."""
        config = ServerConfig(
            dealer_port=6666,
            pub_port=6667,
            server_discovery_port=8888,
            server_name="Custom Server",
            enable_server_discovery=False,
            base_broadcast_interval=default_config.base_broadcast_interval,
            idle_broadcast_interval=default_config.idle_broadcast_interval,
            dirty_threshold=default_config.dirty_threshold,
            client_timeout=default_config.client_timeout,
            cleanup_interval=default_config.cleanup_interval,
            device_id_expiry_time=default_config.device_id_expiry_time,
            status_log_interval=default_config.status_log_interval,
            main_loop_sleep=default_config.main_loop_sleep,
            poll_timeout=default_config.poll_timeout,
            max_global_vars=default_config.max_global_vars,
            max_client_vars=default_config.max_client_vars,
            max_var_name_length=default_config.max_var_name_length,
            max_var_value_length=default_config.max_var_value_length,
            nv_flush_interval=default_config.nv_flush_interval,
            nv_monitor_window_size=default_config.nv_monitor_window_size,
            nv_monitor_threshold=default_config.nv_monitor_threshold,
            max_virtual_transforms=default_config.max_virtual_transforms,
            pub_queue_maxsize=default_config.pub_queue_maxsize,
            delta_ring_size=default_config.delta_ring_size,
            log_dir=default_config.log_dir,
            log_level_console=default_config.log_level_console,
            log_json_console=default_config.log_json_console,
            log_rotation=default_config.log_rotation,
            log_retention=default_config.log_retention,
        )
        assert config.dealer_port == 6666
        assert config.pub_port == 6667
        assert config.server_discovery_port == 8888
        assert config.server_name == "Custom Server"
        assert config.enable_server_discovery is False


class TestLoadConfigFromToml:
    """Tests for load_config_from_toml function."""

    def test_load_valid_toml(self, tmp_path: Path) -> None:
        """Test loading a valid TOML file."""
        toml_content = """
dealer_port = 7777
pub_port = 7778
server_name = "Test Server"
enable_server_discovery = false
"""
        config_file = tmp_path / "config.toml"
        config_file.write_text(toml_content)

        data = load_config_from_toml(config_file)
        assert data["dealer_port"] == 7777
        assert data["pub_port"] == 7778
        assert data["server_name"] == "Test Server"
        assert data["enable_server_discovery"] is False

    def test_load_nonexistent_file(self, tmp_path: Path) -> None:
        """Test that FileNotFoundError is raised for missing file."""
        with pytest.raises(FileNotFoundError):
            load_config_from_toml(tmp_path / "nonexistent.toml")

    def test_load_invalid_toml(self, tmp_path: Path) -> None:
        """Test that TOMLDecodeError is raised for invalid TOML."""
        import tomllib

        config_file = tmp_path / "invalid.toml"
        config_file.write_text("invalid = [unclosed")

        with pytest.raises(tomllib.TOMLDecodeError):
            load_config_from_toml(config_file)

    def test_load_empty_toml(self, tmp_path: Path) -> None:
        """Test loading an empty TOML file."""
        config_file = tmp_path / "empty.toml"
        config_file.write_text("")

        data = load_config_from_toml(config_file)
        assert data == {}


class TestProcessTomlConfig:
    """Tests for process_toml_config function."""

    def test_process_network_keys(self) -> None:
        """Test processing network keys."""
        toml_data = {
            "dealer_port": 5555,
            "pub_port": 5556,
            "server_discovery_port": 9999,
            "server_name": "Test",
            "enable_server_discovery": True,
        }

        result = process_toml_config(toml_data)
        assert result["dealer_port"] == 5555
        assert result["pub_port"] == 5556
        assert result["server_discovery_port"] == 9999
        assert result["server_name"] == "Test"
        assert result["enable_server_discovery"] is True

    def test_process_partial_config(self) -> None:
        """Test processing partial configuration."""
        toml_data = {"dealer_port": 6666}
        result = process_toml_config(toml_data)
        assert result == {"dealer_port": 6666}

    def test_process_empty_config(self) -> None:
        """Test processing empty configuration."""
        result = process_toml_config({})
        assert result == {}

    def test_process_ignores_unknown_keys(self) -> None:
        """Test that unknown keys are ignored."""
        toml_data = {
            "dealer_port": 5555,
            "unknown_key": "unknown_value",
        }
        result = process_toml_config(toml_data)
        assert "unknown_key" not in result
        assert result == {"dealer_port": 5555}

    def test_process_timing_keys(self) -> None:
        """Test processing timing keys."""
        toml_data = {
            "base_broadcast_interval": 0.2,
            "idle_broadcast_interval": 1.0,
            "dirty_threshold": 0.1,
            "client_timeout": 2.0,
            "cleanup_interval": 2.0,
            "device_id_expiry_time": 600.0,
            "status_log_interval": 20.0,
            "main_loop_sleep": 0.05,
            "poll_timeout": 200,
        }
        result = process_toml_config(toml_data)
        assert result["base_broadcast_interval"] == 0.2
        assert result["idle_broadcast_interval"] == 1.0
        assert result["dirty_threshold"] == 0.1
        assert result["client_timeout"] == 2.0
        assert result["cleanup_interval"] == 2.0
        assert result["device_id_expiry_time"] == 600.0
        assert result["status_log_interval"] == 20.0
        assert result["main_loop_sleep"] == 0.05
        assert result["poll_timeout"] == 200

    def test_process_network_variables_keys(self) -> None:
        """Test processing network_variables keys."""
        toml_data = {
            "max_global_vars": 200,
            "max_client_vars": 50,
            "max_var_name_length": 128,
            "max_var_value_length": 2048,
            "nv_flush_interval": 0.1,
            "nv_monitor_window_size": 2.0,
            "nv_monitor_threshold": 300,
        }
        result = process_toml_config(toml_data)
        assert result["max_global_vars"] == 200
        assert result["max_client_vars"] == 50
        assert result["max_var_name_length"] == 128
        assert result["max_var_value_length"] == 2048
        assert result["nv_flush_interval"] == 0.1
        assert result["nv_monitor_window_size"] == 2.0
        assert result["nv_monitor_threshold"] == 300

    def test_process_limits_keys(self) -> None:
        """Test processing limits keys."""
        toml_data = {
            "max_virtual_transforms": 100,
            "pub_queue_maxsize": 20000,
            "delta_ring_size": 5000,
        }
        result = process_toml_config(toml_data)
        assert result["max_virtual_transforms"] == 100
        assert result["pub_queue_maxsize"] == 20000
        assert result["delta_ring_size"] == 5000

    def test_process_logging_keys(self) -> None:
        """Test processing logging keys."""
        toml_data = {
            "log_dir": "/var/log/netsync",
            "log_level_console": "DEBUG",
            "log_json_console": True,
            "log_rotation": "1 day",
            "log_retention": "7 days",
        }
        result = process_toml_config(toml_data)
        assert result["log_dir"] == "/var/log/netsync"
        assert result["log_level_console"] == "DEBUG"
        assert result["log_json_console"] is True
        assert result["log_rotation"] == "1 day"
        assert result["log_retention"] == "7 days"

    def test_process_empty_string_to_none(self) -> None:
        """Test that empty strings are converted to None for optional fields."""
        toml_data = {
            "log_dir": "",
            "log_rotation": "",
            "log_retention": "",
        }
        result = process_toml_config(toml_data)
        assert result["log_dir"] is None
        assert result["log_rotation"] is None
        assert result["log_retention"] is None


class TestValidateConfig:
    """Tests for validate_config function."""

    def test_valid_config(self, default_config: ServerConfig) -> None:
        """Test that valid configuration passes validation."""
        errors = validate_config(default_config)
        assert errors == []

    def test_invalid_dealer_port_too_low(self, default_config: ServerConfig) -> None:
        """Test that port 0 fails validation."""
        from dataclasses import replace

        config = replace(default_config, dealer_port=0)
        errors = validate_config(config)
        assert any("dealer_port" in e for e in errors)

    def test_invalid_dealer_port_too_high(self, default_config: ServerConfig) -> None:
        """Test that port > 65535 fails validation."""
        from dataclasses import replace

        config = replace(default_config, dealer_port=70000)
        errors = validate_config(config)
        assert any("dealer_port" in e for e in errors)

    def test_invalid_pub_port(self, default_config: ServerConfig) -> None:
        """Test that invalid pub_port fails validation."""
        from dataclasses import replace

        config = replace(default_config, pub_port=0)
        errors = validate_config(config)
        assert any("pub_port" in e for e in errors)

    def test_invalid_server_discovery_port(self, default_config: ServerConfig) -> None:
        """Test that invalid server_discovery_port fails validation."""
        from dataclasses import replace

        config = replace(default_config, server_discovery_port=99999)
        errors = validate_config(config)
        assert any("server_discovery_port" in e for e in errors)

    def test_valid_edge_ports(self, default_config: ServerConfig) -> None:
        """Test that edge case ports (1 and 65535) are valid."""
        from dataclasses import replace

        config = replace(
            default_config,
            dealer_port=1,
            pub_port=65535,
            server_discovery_port=1024,
        )
        errors = validate_config(config)
        assert errors == []

    def test_invalid_timing_zero(self, default_config: ServerConfig) -> None:
        """Test that zero timing values fail validation."""
        from dataclasses import replace

        config = replace(default_config, base_broadcast_interval=0)
        errors = validate_config(config)
        assert any("base_broadcast_interval" in e for e in errors)

    def test_invalid_timing_negative(self, default_config: ServerConfig) -> None:
        """Test that negative timing values fail validation."""
        from dataclasses import replace

        config = replace(default_config, client_timeout=-1.0)
        errors = validate_config(config)
        assert any("client_timeout" in e for e in errors)

    def test_invalid_poll_timeout(self, default_config: ServerConfig) -> None:
        """Test that invalid poll_timeout fails validation."""
        from dataclasses import replace

        config = replace(default_config, poll_timeout=0)
        errors = validate_config(config)
        assert any("poll_timeout" in e for e in errors)

    def test_valid_timing_values(self, default_config: ServerConfig) -> None:
        """Test that valid timing values pass validation."""
        from dataclasses import replace

        config = replace(
            default_config,
            base_broadcast_interval=0.001,
            idle_broadcast_interval=0.001,
            dirty_threshold=0.001,
            client_timeout=0.001,
            cleanup_interval=0.001,
            device_id_expiry_time=0.001,
            status_log_interval=0.001,
            main_loop_sleep=0.001,
            poll_timeout=1,
        )
        errors = validate_config(config)
        assert errors == []

    def test_invalid_nv_max_global_vars(self, default_config: ServerConfig) -> None:
        """Test that invalid max_global_vars fails validation."""
        from dataclasses import replace

        config = replace(default_config, max_global_vars=0)
        errors = validate_config(config)
        assert any("max_global_vars" in e for e in errors)

    def test_invalid_nv_flush_interval(self, default_config: ServerConfig) -> None:
        """Test that invalid nv_flush_interval fails validation."""
        from dataclasses import replace

        config = replace(default_config, nv_flush_interval=-0.1)
        errors = validate_config(config)
        assert any("nv_flush_interval" in e for e in errors)

    def test_valid_nv_values(self, default_config: ServerConfig) -> None:
        """Test that valid NV values pass validation."""
        from dataclasses import replace

        config = replace(
            default_config,
            max_global_vars=1,
            max_client_vars=1,
            max_var_name_length=1,
            max_var_value_length=1,
            nv_flush_interval=0.001,
            nv_monitor_window_size=0.001,
            nv_monitor_threshold=1,
        )
        errors = validate_config(config)
        assert errors == []

    def test_invalid_max_virtual_transforms(self, default_config: ServerConfig) -> None:
        """Test that invalid max_virtual_transforms fails validation."""
        from dataclasses import replace

        config = replace(default_config, max_virtual_transforms=0)
        errors = validate_config(config)
        assert any("max_virtual_transforms" in e for e in errors)

    def test_invalid_pub_queue_maxsize(self, default_config: ServerConfig) -> None:
        """Test that invalid pub_queue_maxsize fails validation."""
        from dataclasses import replace

        config = replace(default_config, pub_queue_maxsize=-1)
        errors = validate_config(config)
        assert any("pub_queue_maxsize" in e for e in errors)

    def test_valid_limits_values(self, default_config: ServerConfig) -> None:
        """Test that valid limits values pass validation."""
        from dataclasses import replace

        config = replace(
            default_config,
            max_virtual_transforms=1,
            pub_queue_maxsize=1,
            delta_ring_size=1,
        )
        errors = validate_config(config)
        assert errors == []

    def test_invalid_log_level(self, default_config: ServerConfig) -> None:
        """Test that invalid log_level_console fails validation."""
        from dataclasses import replace

        config = replace(default_config, log_level_console="INVALID")
        errors = validate_config(config)
        assert any("log_level_console" in e for e in errors)

    def test_valid_log_levels(self, default_config: ServerConfig) -> None:
        """Test that all valid log levels pass validation."""
        from dataclasses import replace

        for level in ["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"]:
            config = replace(default_config, log_level_console=level)
            errors = validate_config(config)
            assert errors == [], f"Failed for log level: {level}"

    def test_log_level_case_insensitive(self, default_config: ServerConfig) -> None:
        """Test that log level validation is case insensitive."""
        from dataclasses import replace

        config = replace(default_config, log_level_console="debug")
        errors = validate_config(config)
        assert errors == []

    def test_dirty_threshold_exceeds_base_interval(
        self, default_config: ServerConfig
    ) -> None:
        """Test that dirty_threshold > base_broadcast_interval fails validation."""
        from dataclasses import replace

        config = replace(
            default_config,
            base_broadcast_interval=0.1,
            dirty_threshold=0.2,  # Should be <= base_broadcast_interval
        )
        errors = validate_config(config)
        assert any(
            "dirty_threshold" in e and "base_broadcast_interval" in e for e in errors
        )

    def test_base_interval_exceeds_idle_interval(
        self, default_config: ServerConfig
    ) -> None:
        """Test that base_broadcast_interval > idle_broadcast_interval fails validation."""
        from dataclasses import replace

        config = replace(
            default_config,
            base_broadcast_interval=1.0,
            idle_broadcast_interval=0.5,  # Should be >= base_broadcast_interval
        )
        errors = validate_config(config)
        assert any(
            "base_broadcast_interval" in e and "idle_broadcast_interval" in e
            for e in errors
        )

    def test_valid_timing_relationships(self, default_config: ServerConfig) -> None:
        """Test that valid timing relationships pass validation."""
        from dataclasses import replace

        # dirty_threshold <= base_broadcast_interval <= idle_broadcast_interval
        config = replace(
            default_config,
            dirty_threshold=0.01,
            base_broadcast_interval=0.05,
            idle_broadcast_interval=0.5,
        )
        errors = validate_config(config)
        assert errors == []


class TestMergeCliArgs:
    """Tests for merge_cli_args function."""

    def test_cli_overrides_server_discovery_port(
        self, default_config: ServerConfig
    ) -> None:
        """Test that CLI server_discovery_port overrides config."""
        args = argparse.Namespace(
            server_discovery_port=8888,
            no_server_discovery=False,
        )

        merged = merge_cli_args(default_config, args)
        assert merged.server_discovery_port == 8888
        # Original config unchanged
        assert default_config.server_discovery_port == 9999

    def test_no_server_discovery_flag(self, default_config: ServerConfig) -> None:
        """Test that --no-server-discovery disables discovery."""
        args = argparse.Namespace(
            server_discovery_port=None,
            no_server_discovery=True,
        )

        merged = merge_cli_args(default_config, args)
        assert merged.enable_server_discovery is False

    def test_none_values_dont_override(self, default_config: ServerConfig) -> None:
        """Test that None CLI values don't override config."""
        args = argparse.Namespace(
            server_discovery_port=None,
            no_server_discovery=False,
        )

        merged = merge_cli_args(default_config, args)
        assert merged.server_discovery_port == 9999

    def test_missing_attributes_handled(self, default_config: ServerConfig) -> None:
        """Test that missing CLI attributes are handled gracefully."""
        args = argparse.Namespace()  # Empty namespace

        merged = merge_cli_args(default_config, args)
        assert merged == default_config

    def test_cli_overrides_logging_settings(self, default_config: ServerConfig) -> None:
        """Test that CLI logging settings override config."""
        args = argparse.Namespace(
            server_discovery_port=None,
            no_server_discovery=False,
            log_dir="/tmp/logs",
            log_level_console="DEBUG",
            log_json_console=True,
            log_rotation="5 MB",
            log_retention="10 files",
        )

        merged = merge_cli_args(default_config, args)
        assert merged.log_dir == "/tmp/logs"
        assert merged.log_level_console == "DEBUG"
        assert merged.log_json_console is True
        assert merged.log_rotation == "5 MB"
        assert merged.log_retention == "10 files"

    def test_cli_none_values_preserve_config(
        self, default_config: ServerConfig
    ) -> None:
        """Test that None CLI values don't override config file settings."""
        from dataclasses import replace

        config = replace(
            default_config,
            log_dir="/var/log/app",
            log_level_console="DEBUG",
            log_json_console=True,
            log_rotation="1 day",
            log_retention="7 days",
        )

        args = argparse.Namespace(
            server_discovery_port=None,
            no_server_discovery=False,
            log_dir=None,
            log_level_console=None,
            log_json_console=False,
            log_rotation=None,
            log_retention=None,
        )

        merged = merge_cli_args(config, args)
        assert merged.log_dir == "/var/log/app"
        assert merged.log_level_console == "DEBUG"
        assert merged.log_json_console is True
        assert merged.log_rotation == "1 day"
        assert merged.log_retention == "7 days"


class TestConfigurationError:
    """Tests for ConfigurationError exception."""

    def test_configuration_error_stores_errors(self) -> None:
        """Test that ConfigurationError stores the error list."""
        errors = ["error1", "error2", "error3"]
        exc = ConfigurationError(errors)
        assert exc.errors == errors
        assert "error1" in str(exc)
        assert "error2" in str(exc)

    def test_create_config_raises_configuration_error(self, tmp_path: Path) -> None:
        """Test that create_config_from_args raises ConfigurationError on validation failure."""
        # Create config with invalid port
        toml_content = """
dealer_port = 99999
"""
        config_file = tmp_path / "invalid.toml"
        config_file.write_text(toml_content)

        args = argparse.Namespace(
            config=config_file,
            server_discovery_port=None,
            no_server_discovery=False,
            log_dir=None,
            log_level_console=None,
            log_json_console=False,
            log_rotation=None,
            log_retention=None,
        )

        with pytest.raises(ConfigurationError) as exc_info:
            create_config_from_args(args)

        assert len(exc_info.value.errors) > 0
        assert any("dealer_port" in e for e in exc_info.value.errors)


class TestDefaultConfigError:
    """Tests for DefaultConfigError exception."""

    def test_default_config_error_message(self) -> None:
        """Test that DefaultConfigError has proper message."""
        exc = DefaultConfigError("test message")
        assert "Failed to load default configuration" in str(exc)
        assert "test message" in str(exc)


class TestGetUnknownKeys:
    """Tests for get_unknown_keys function."""

    def test_no_unknown_keys(self) -> None:
        """Test that valid config returns empty list."""
        toml_data = {
            "dealer_port": 5555,
            "pub_port": 5556,
            "base_broadcast_interval": 0.1,
        }
        unknown = get_unknown_keys(toml_data)
        assert unknown == []

    def test_unknown_key(self) -> None:
        """Test detection of unknown key (typo)."""
        toml_data = {
            "log_levl_console": "DEBUG",  # Typo: should be log_level_console
            "log_dir": "/var/log",
        }
        unknown = get_unknown_keys(toml_data)
        assert "log_levl_console" in unknown

    def test_multiple_unknown_keys(self) -> None:
        """Test detection of multiple unknown keys."""
        toml_data = {
            "dealer_port": 5555,
            "unknown_key1": "value1",
            "unknown_key2": "value2",
        }
        unknown = get_unknown_keys(toml_data)
        assert "unknown_key1" in unknown
        assert "unknown_key2" in unknown


class TestCreateConfigFromArgs:
    """Tests for create_config_from_args function."""

    def test_create_config_without_config_file(self) -> None:
        """Test creating config without config file uses defaults."""
        args = argparse.Namespace(
            config=None,
            server_discovery_port=None,
            no_server_discovery=False,
            log_dir=None,
            log_level_console=None,
            log_json_console=False,
            log_rotation=None,
            log_retention=None,
        )

        config = create_config_from_args(args)
        # Should match default config
        assert config.dealer_port == 5555
        assert config.pub_port == 5556

    def test_config_file_overrides_default(self, tmp_path: Path) -> None:
        """Test that config file overrides default config."""
        toml_content = """
dealer_port = 7777
server_name = "User Server"
"""
        config_file = tmp_path / "user.toml"
        config_file.write_text(toml_content)

        args = argparse.Namespace(
            config=config_file,
            server_discovery_port=None,
            no_server_discovery=False,
            log_dir=None,
            log_level_console=None,
            log_json_console=False,
            log_rotation=None,
            log_retention=None,
        )

        config = create_config_from_args(args)
        # User overrides
        assert config.dealer_port == 7777
        assert config.server_name == "User Server"
        # Defaults preserved
        assert config.pub_port == 5556

    def test_cli_overrides_config_file(self, tmp_path: Path) -> None:
        """Test that CLI args override config file."""
        toml_content = """
server_discovery_port = 7777
"""
        config_file = tmp_path / "user.toml"
        config_file.write_text(toml_content)

        args = argparse.Namespace(
            config=config_file,
            server_discovery_port=8888,  # CLI override
            no_server_discovery=False,
            log_dir=None,
            log_level_console=None,
            log_json_console=False,
            log_rotation=None,
            log_retention=None,
        )

        config = create_config_from_args(args)
        # CLI takes precedence
        assert config.server_discovery_port == 8888

    def test_config_file_not_found(self, tmp_path: Path) -> None:
        """Test that FileNotFoundError is raised when config file doesn't exist."""
        args = argparse.Namespace(
            config=tmp_path / "nonexistent.toml",
            server_discovery_port=None,
            no_server_discovery=False,
            log_dir=None,
            log_level_console=None,
            log_json_console=False,
            log_rotation=None,
            log_retention=None,
        )

        with pytest.raises(FileNotFoundError):
            create_config_from_args(args)

    def test_partial_config_file(self, tmp_path: Path) -> None:
        """Test that partial config file works correctly."""
        toml_content = """
client_timeout = 5.0
"""
        config_file = tmp_path / "user.toml"
        config_file.write_text(toml_content)

        args = argparse.Namespace(
            config=config_file,
            server_discovery_port=None,
            no_server_discovery=False,
            log_dir=None,
            log_level_console=None,
            log_json_console=False,
            log_rotation=None,
            log_retention=None,
        )

        config = create_config_from_args(args)
        # User override
        assert config.client_timeout == 5.0
        # All other defaults preserved
        assert config.dealer_port == 5555
        assert config.base_broadcast_interval == 0.1
