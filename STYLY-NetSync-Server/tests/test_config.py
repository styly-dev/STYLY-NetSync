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
