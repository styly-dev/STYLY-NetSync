"""Tests for configuration module."""

import argparse
from pathlib import Path

import pytest

from styly_netsync.config import (
    ServerConfig,
    flatten_toml_config,
    load_config_from_toml,
    merge_cli_args,
    validate_config,
)


class TestServerConfig:
    """Tests for ServerConfig dataclass."""

    def test_default_values(self):
        """Test that default values are set correctly."""
        config = ServerConfig()
        assert config.dealer_port == 5555
        assert config.pub_port == 5556
        assert config.server_discovery_port == 9999
        assert config.server_name == "STYLY-NetSync-Server"
        assert config.enable_server_discovery is True

    def test_custom_values(self):
        """Test that custom values override defaults."""
        config = ServerConfig(
            dealer_port=6666,
            pub_port=6667,
            server_discovery_port=8888,
            server_name="Custom Server",
            enable_server_discovery=False,
        )
        assert config.dealer_port == 6666
        assert config.pub_port == 6667
        assert config.server_discovery_port == 8888
        assert config.server_name == "Custom Server"
        assert config.enable_server_discovery is False

    def test_timing_default_values(self):
        """Test that timing default values are set correctly."""
        config = ServerConfig()
        assert config.base_broadcast_interval == 0.1
        assert config.idle_broadcast_interval == 0.5
        assert config.dirty_threshold == 0.05
        assert config.client_timeout == 1.0
        assert config.cleanup_interval == 1.0
        assert config.device_id_expiry_time == 300.0
        assert config.status_log_interval == 10.0
        assert config.main_loop_sleep == 0.02
        assert config.poll_timeout == 100

    def test_timing_custom_values(self):
        """Test that timing custom values override defaults."""
        config = ServerConfig(
            base_broadcast_interval=0.2,
            idle_broadcast_interval=1.0,
            client_timeout=2.0,
            poll_timeout=200,
        )
        assert config.base_broadcast_interval == 0.2
        assert config.idle_broadcast_interval == 1.0
        assert config.client_timeout == 2.0
        assert config.poll_timeout == 200

    def test_nv_default_values(self):
        """Test that NV default values are set correctly."""
        config = ServerConfig()
        assert config.max_global_vars == 100
        assert config.max_client_vars == 100
        assert config.max_var_name_length == 64
        assert config.max_var_value_length == 1024
        assert config.nv_flush_interval == 0.05
        assert config.nv_monitor_window_size == 1.0
        assert config.nv_monitor_threshold == 200

    def test_nv_custom_values(self):
        """Test that NV custom values override defaults."""
        config = ServerConfig(
            max_global_vars=200,
            max_client_vars=50,
            max_var_value_length=2048,
            nv_flush_interval=0.1,
        )
        assert config.max_global_vars == 200
        assert config.max_client_vars == 50
        assert config.max_var_value_length == 2048
        assert config.nv_flush_interval == 0.1

    def test_limits_default_values(self):
        """Test that limits default values are set correctly."""
        config = ServerConfig()
        assert config.max_virtual_transforms == 50
        assert config.pub_queue_maxsize == 10000
        assert config.delta_ring_size == 10000

    def test_limits_custom_values(self):
        """Test that limits custom values override defaults."""
        config = ServerConfig(
            max_virtual_transforms=100,
            pub_queue_maxsize=20000,
            delta_ring_size=5000,
        )
        assert config.max_virtual_transforms == 100
        assert config.pub_queue_maxsize == 20000
        assert config.delta_ring_size == 5000


class TestLoadConfigFromToml:
    """Tests for load_config_from_toml function."""

    def test_load_valid_toml(self, tmp_path: Path):
        """Test loading a valid TOML file."""
        toml_content = """
[network]
dealer_port = 7777
pub_port = 7778
server_name = "Test Server"
enable_server_discovery = false
"""
        config_file = tmp_path / "config.toml"
        config_file.write_text(toml_content)

        data = load_config_from_toml(config_file)
        assert data["network"]["dealer_port"] == 7777
        assert data["network"]["pub_port"] == 7778
        assert data["network"]["server_name"] == "Test Server"
        assert data["network"]["enable_server_discovery"] is False

    def test_load_nonexistent_file(self, tmp_path: Path):
        """Test that FileNotFoundError is raised for missing file."""
        with pytest.raises(FileNotFoundError):
            load_config_from_toml(tmp_path / "nonexistent.toml")

    def test_load_invalid_toml(self, tmp_path: Path):
        """Test that TOMLDecodeError is raised for invalid TOML."""
        import tomllib

        config_file = tmp_path / "invalid.toml"
        config_file.write_text("invalid = [unclosed")

        with pytest.raises(tomllib.TOMLDecodeError):
            load_config_from_toml(config_file)

    def test_load_empty_toml(self, tmp_path: Path):
        """Test loading an empty TOML file."""
        config_file = tmp_path / "empty.toml"
        config_file.write_text("")

        data = load_config_from_toml(config_file)
        assert data == {}


class TestFlattenTomlConfig:
    """Tests for flatten_toml_config function."""

    def test_flatten_network_section(self):
        """Test flattening network section."""
        toml_data = {
            "network": {
                "dealer_port": 5555,
                "pub_port": 5556,
                "server_discovery_port": 9999,
                "server_name": "Test",
                "enable_server_discovery": True,
            }
        }

        flat = flatten_toml_config(toml_data)
        assert flat["dealer_port"] == 5555
        assert flat["pub_port"] == 5556
        assert flat["server_discovery_port"] == 9999
        assert flat["server_name"] == "Test"
        assert flat["enable_server_discovery"] is True

    def test_flatten_partial_config(self):
        """Test flattening partial configuration."""
        toml_data = {"network": {"dealer_port": 6666}}
        flat = flatten_toml_config(toml_data)
        assert flat == {"dealer_port": 6666}

    def test_flatten_empty_config(self):
        """Test flattening empty configuration."""
        flat = flatten_toml_config({})
        assert flat == {}

    def test_flatten_ignores_unknown_sections(self):
        """Test that unknown sections are ignored."""
        toml_data = {
            "network": {"dealer_port": 5555},
            "unknown_section": {"some_key": "some_value"},
        }
        flat = flatten_toml_config(toml_data)
        assert "some_key" not in flat
        assert flat == {"dealer_port": 5555}

    def test_flatten_ignores_unknown_keys(self):
        """Test that unknown keys within known sections are ignored."""
        toml_data = {
            "network": {
                "dealer_port": 5555,
                "unknown_key": "unknown_value",
            }
        }
        flat = flatten_toml_config(toml_data)
        assert "unknown_key" not in flat
        assert flat == {"dealer_port": 5555}

    def test_flatten_timing_section(self):
        """Test flattening timing section."""
        toml_data = {
            "timing": {
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
        }
        flat = flatten_toml_config(toml_data)
        assert flat["base_broadcast_interval"] == 0.2
        assert flat["idle_broadcast_interval"] == 1.0
        assert flat["dirty_threshold"] == 0.1
        assert flat["client_timeout"] == 2.0
        assert flat["cleanup_interval"] == 2.0
        assert flat["device_id_expiry_time"] == 600.0
        assert flat["status_log_interval"] == 20.0
        assert flat["main_loop_sleep"] == 0.05
        assert flat["poll_timeout"] == 200

    def test_flatten_network_variables_section(self):
        """Test flattening network_variables section."""
        toml_data = {
            "network_variables": {
                "max_global_vars": 200,
                "max_client_vars": 50,
                "max_var_name_length": 128,
                "max_var_value_length": 2048,
                "nv_flush_interval": 0.1,
                "nv_monitor_window_size": 2.0,
                "nv_monitor_threshold": 300,
            }
        }
        flat = flatten_toml_config(toml_data)
        assert flat["max_global_vars"] == 200
        assert flat["max_client_vars"] == 50
        assert flat["max_var_name_length"] == 128
        assert flat["max_var_value_length"] == 2048
        assert flat["nv_flush_interval"] == 0.1
        assert flat["nv_monitor_window_size"] == 2.0
        assert flat["nv_monitor_threshold"] == 300

    def test_flatten_limits_section(self):
        """Test flattening limits section."""
        toml_data = {
            "limits": {
                "max_virtual_transforms": 100,
                "pub_queue_maxsize": 20000,
                "delta_ring_size": 5000,
            }
        }
        flat = flatten_toml_config(toml_data)
        assert flat["max_virtual_transforms"] == 100
        assert flat["pub_queue_maxsize"] == 20000
        assert flat["delta_ring_size"] == 5000


class TestValidateConfig:
    """Tests for validate_config function."""

    def test_valid_config(self):
        """Test that valid configuration passes validation."""
        config = ServerConfig()
        errors = validate_config(config)
        assert errors == []

    def test_invalid_dealer_port_too_low(self):
        """Test that port 0 fails validation."""
        config = ServerConfig(dealer_port=0)
        errors = validate_config(config)
        assert any("dealer_port" in e for e in errors)

    def test_invalid_dealer_port_too_high(self):
        """Test that port > 65535 fails validation."""
        config = ServerConfig(dealer_port=70000)
        errors = validate_config(config)
        assert any("dealer_port" in e for e in errors)

    def test_invalid_pub_port(self):
        """Test that invalid pub_port fails validation."""
        config = ServerConfig(pub_port=0)
        errors = validate_config(config)
        assert any("pub_port" in e for e in errors)

    def test_invalid_server_discovery_port(self):
        """Test that invalid server_discovery_port fails validation."""
        config = ServerConfig(server_discovery_port=99999)
        errors = validate_config(config)
        assert any("server_discovery_port" in e for e in errors)

    def test_valid_edge_ports(self):
        """Test that edge case ports (1 and 65535) are valid."""
        config = ServerConfig(
            dealer_port=1,
            pub_port=65535,
            server_discovery_port=1024,
        )
        errors = validate_config(config)
        assert errors == []

    def test_invalid_timing_zero(self):
        """Test that zero timing values fail validation."""
        config = ServerConfig(base_broadcast_interval=0)
        errors = validate_config(config)
        assert any("base_broadcast_interval" in e for e in errors)

    def test_invalid_timing_negative(self):
        """Test that negative timing values fail validation."""
        config = ServerConfig(client_timeout=-1.0)
        errors = validate_config(config)
        assert any("client_timeout" in e for e in errors)

    def test_invalid_poll_timeout(self):
        """Test that invalid poll_timeout fails validation."""
        config = ServerConfig(poll_timeout=0)
        errors = validate_config(config)
        assert any("poll_timeout" in e for e in errors)

    def test_valid_timing_values(self):
        """Test that valid timing values pass validation."""
        config = ServerConfig(
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

    def test_invalid_nv_max_global_vars(self):
        """Test that invalid max_global_vars fails validation."""
        config = ServerConfig(max_global_vars=0)
        errors = validate_config(config)
        assert any("max_global_vars" in e for e in errors)

    def test_invalid_nv_flush_interval(self):
        """Test that invalid nv_flush_interval fails validation."""
        config = ServerConfig(nv_flush_interval=-0.1)
        errors = validate_config(config)
        assert any("nv_flush_interval" in e for e in errors)

    def test_valid_nv_values(self):
        """Test that valid NV values pass validation."""
        config = ServerConfig(
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

    def test_invalid_max_virtual_transforms(self):
        """Test that invalid max_virtual_transforms fails validation."""
        config = ServerConfig(max_virtual_transforms=0)
        errors = validate_config(config)
        assert any("max_virtual_transforms" in e for e in errors)

    def test_invalid_pub_queue_maxsize(self):
        """Test that invalid pub_queue_maxsize fails validation."""
        config = ServerConfig(pub_queue_maxsize=-1)
        errors = validate_config(config)
        assert any("pub_queue_maxsize" in e for e in errors)

    def test_valid_limits_values(self):
        """Test that valid limits values pass validation."""
        config = ServerConfig(
            max_virtual_transforms=1,
            pub_queue_maxsize=1,
            delta_ring_size=1,
        )
        errors = validate_config(config)
        assert errors == []


class TestMergeCliArgs:
    """Tests for merge_cli_args function."""

    def test_cli_overrides_server_discovery_port(self):
        """Test that CLI server_discovery_port overrides config."""
        config = ServerConfig(server_discovery_port=9999)

        args = argparse.Namespace(
            server_discovery_port=8888,
            no_server_discovery=False,
        )

        merged = merge_cli_args(config, args)
        assert merged.server_discovery_port == 8888
        # Original config unchanged
        assert config.server_discovery_port == 9999

    def test_no_server_discovery_flag(self):
        """Test that --no-server-discovery disables discovery."""
        config = ServerConfig(enable_server_discovery=True)

        args = argparse.Namespace(
            server_discovery_port=None,
            no_server_discovery=True,
        )

        merged = merge_cli_args(config, args)
        assert merged.enable_server_discovery is False

    def test_none_values_dont_override(self):
        """Test that None CLI values don't override config."""
        config = ServerConfig(server_discovery_port=9999)

        args = argparse.Namespace(
            server_discovery_port=None,
            no_server_discovery=False,
        )

        merged = merge_cli_args(config, args)
        assert merged.server_discovery_port == 9999

    def test_missing_attributes_handled(self):
        """Test that missing CLI attributes are handled gracefully."""
        config = ServerConfig()
        args = argparse.Namespace()  # Empty namespace

        merged = merge_cli_args(config, args)
        assert merged == config
