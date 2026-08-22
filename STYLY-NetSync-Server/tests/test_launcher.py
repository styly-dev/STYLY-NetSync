"""Tests for the desktop launcher's non-GUI logic.

Only the pure parts are exercised here: command building, settings merging and
input parsing. Importing the module needs Tk to be present but never opens a
window.
"""

from __future__ import annotations

import json
import signal
import subprocess
import sys
import time
from pathlib import Path

import pytest

pytest.importorskip("tkinter", reason="Tk is not available in this environment")

from styly_netsync import launcher  # noqa: E402 - after importorskip
from styly_netsync.cli import (  # noqa: E402 - after importorskip
    install_stop_signal_handlers,
)
from styly_netsync.launcher import (  # noqa: E402 - after importorskip
    _ANSI_RE,
    ManagedProcess,
    ServerSettings,
    SimulatorSettings,
    _apply_overrides,
    _apply_stored,
    _parse_int,
    _parse_port,
    _severity_tag,
    acquire_single_instance,
    build_server_command,
    build_simulator_command,
    clear_running_server,
    find_stale_server,
    record_running_server,
    settings_path,
    stop_stale_server,
)

PYTHON = "/opt/python"


# --------------------------------------------------------------------------
# Server command
# --------------------------------------------------------------------------


def test_defaults_produce_a_bare_command() -> None:
    command = build_server_command(ServerSettings(), PYTHON)

    assert command == [PYTHON, "-m", "styly_netsync"]


def test_non_default_ports_become_flags() -> None:
    settings = ServerSettings(
        control_port=6000,
        transform_port=6002,
        pub_port=6001,
        rest_api_port=9100,
        server_discovery_port=9998,
        log_level_console="DEBUG",
    )

    command = build_server_command(settings, PYTHON)

    assert command[:3] == [PYTHON, "-m", "styly_netsync"]
    assert "--control-port" in command and "6000" in command
    assert "--transform-port" in command and "6002" in command
    assert "--pub-port" in command and "6001" in command
    assert "--rest-api-port" in command and "9100" in command
    assert "--server-discovery-port" in command and "9998" in command
    assert command[-2:] == ["--log-level-console", "DEBUG"]


def test_disabling_discovery_omits_the_discovery_port() -> None:
    settings = ServerSettings(disable_server_discovery=True, server_discovery_port=9998)

    command = build_server_command(settings, PYTHON)

    assert "--no-server-discovery" in command
    assert "--server-discovery-port" not in command


def test_config_file_comes_first_so_flags_override_it() -> None:
    settings = ServerSettings(config_file="C:/venue/lbe.toml", control_port=6000)

    command = build_server_command(settings, PYTHON)

    assert command[3:5] == ["--config", "C:/venue/lbe.toml"]
    assert command.index("--control-port") > command.index("--config")


def test_server_command_defaults_to_the_running_interpreter() -> None:
    import sys

    assert build_server_command(ServerSettings())[0] == sys.executable


# --------------------------------------------------------------------------
# Simulator command
# --------------------------------------------------------------------------


def test_simulator_command_follows_the_server_ports() -> None:
    command = build_simulator_command(
        SimulatorSettings(clients=25, server="192.168.1.20", room="venue_a"),
        ServerSettings(control_port=6000, transform_port=6002, pub_port=6001),
        PYTHON,
    )

    assert command[:3] == [PYTHON, "-m", "styly_netsync.client_simulator"]
    assert command[command.index("--clients") + 1] == "25"
    assert command[command.index("--server") + 1] == "192.168.1.20"
    assert command[command.index("--room") + 1] == "venue_a"
    assert command[command.index("--control-port") + 1] == "6000"
    assert command[command.index("--transform-port") + 1] == "6002"
    assert command[command.index("--sub-port") + 1] == "6001"


# --------------------------------------------------------------------------
# Settings merging
# --------------------------------------------------------------------------


def test_stored_settings_are_applied() -> None:
    merged = _apply_stored(ServerSettings(), {"control_port": 6000, "config_file": "x"})

    assert merged.control_port == 6000
    assert merged.config_file == "x"


def test_stored_settings_ignore_wrong_types_and_unknown_keys() -> None:
    merged = _apply_stored(
        ServerSettings(),
        {"control_port": "6000", "disable_server_discovery": 1, "bogus": 3},
    )

    assert merged.control_port == 5555
    assert merged.disable_server_discovery is False
    assert not hasattr(merged, "bogus")


def test_stored_settings_tolerate_garbage() -> None:
    assert _apply_stored(ServerSettings(), None).control_port == 5555
    assert _apply_stored(SimulatorSettings(), "nope").clients == 10


def test_overrides_only_apply_non_default_values() -> None:
    base = _apply_stored(ServerSettings(), {"control_port": 6000, "pub_port": 6001})

    merged = _apply_overrides(base, ServerSettings(pub_port=7000))

    assert merged.control_port == 6000, "stored value must survive an unset override"
    assert merged.pub_port == 7000


def test_overrides_may_be_absent() -> None:
    assert (
        _apply_overrides(ServerSettings(control_port=6000), None).control_port == 6000
    )


# --------------------------------------------------------------------------
# Input parsing and paths
# --------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("text", "expected"),
    [("5555", 5555), (" 6000 ", 6000), ("0", 1), ("70000", 65535), ("abc", 5555)],
)
def test_parse_port(text: str, expected: int) -> None:
    assert _parse_port(text, 5555) == expected


def test_parse_int_clamps_to_range() -> None:
    assert _parse_int("500", 10, 1, 100) == 100
    assert _parse_int("", 10, 1, 100) == 10


def test_settings_path_is_user_scoped() -> None:
    path = settings_path()

    assert isinstance(path, Path)
    assert path.name == "launcher.json"
    assert path.parent.name == "STYLY-NetSync"


# --------------------------------------------------------------------------
# Log rendering
# --------------------------------------------------------------------------


def test_ansi_colour_codes_are_stripped() -> None:
    coloured = "\x1b[32m00:10:41\x1b[0m | \x1b[1mINFO    \x1b[0m | Server started"

    assert _ANSI_RE.sub("", coloured) == "00:10:41 | INFO     | Server started"


@pytest.mark.parametrize(
    ("line", "expected"),
    [
        ("00:10:41 | ERROR    | boom", "error"),
        ("00:10:41 | CRITICAL | boom", "error"),
        ("Traceback (most recent call last):", "error"),
        ("00:10:41 | WARNING  | hmm", "warning"),
        ("00:10:41 | INFO     | fine", None),
    ],
)
def test_severity_tagging(line: str, expected: str | None) -> None:
    assert _severity_tag(line) == expected


# --------------------------------------------------------------------------
# Child process supervision
# --------------------------------------------------------------------------


def _drain_until(process: ManagedProcess, needle: str, timeout: float) -> list[str]:
    """Collect streamed lines until one contains *needle* or time runs out."""
    collected: list[str] = []
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        lines, ended = process.drain()
        collected.extend(lines)
        if any(needle in line for line in collected) or ended:
            break
        time.sleep(0.05)
    return collected


def test_managed_process_streams_strips_colour_and_stops() -> None:
    script = (
        "import time;" "print('\\x1b[32mready\\x1b[0m', flush=True);" "time.sleep(60)"
    )
    process = ManagedProcess()
    process.start([sys.executable, "-c", script])
    try:
        assert process.is_running
        assert isinstance(process.pid, int)

        lines = _drain_until(process, "ready", timeout=20)
        assert "ready" in lines, f"expected a streamed line, got {lines!r}"
        assert not any("\x1b" in line for line in lines)
    finally:
        process.stop(timeout=6.0)

    assert not process.is_running

    tail = _drain_until(process, "[stopped]", timeout=5)
    assert "[stopped]" in tail, "a requested stop must not look like a crash"


def test_managed_process_reports_an_unrequested_exit() -> None:
    process = ManagedProcess()
    process.start([sys.executable, "-c", "raise SystemExit(3)"])

    tail = _drain_until(process, "[process exited", timeout=20)

    assert any("[process exited with code 3]" in line for line in tail)


def test_managed_process_refuses_a_second_start() -> None:
    process = ManagedProcess()
    process.start([sys.executable, "-c", "import time; time.sleep(60)"])
    try:
        with pytest.raises(RuntimeError, match="already running"):
            process.start([sys.executable, "-c", "pass"])
    finally:
        process.stop(timeout=6.0)


def test_stopping_an_idle_process_is_harmless() -> None:
    ManagedProcess().stop(timeout=1.0)


# --------------------------------------------------------------------------
# Recovering a server left behind by a killed launcher
# --------------------------------------------------------------------------


@pytest.fixture()
def isolated_state(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    """Point the launcher's state files at a temp dir, not the real user one."""
    state = tmp_path / "running-server.json"
    monkeypatch.setattr(launcher, "server_state_path", lambda: state)
    return state


def test_no_record_means_no_stale_server(isolated_state: Path) -> None:
    assert find_stale_server() is None


def _spawn(*extra_argv: str) -> subprocess.Popen[bytes]:
    """A long-lived child whose argv we control, so PID matching can be tested."""
    return subprocess.Popen(
        [sys.executable, "-c", "import time; time.sleep(60)", *extra_argv],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )


def test_a_dead_pid_is_not_reported_and_the_record_is_dropped(
    isolated_state: Path,
) -> None:
    dead = _spawn("styly_netsync")
    dead.kill()
    dead.wait(timeout=10)
    record_running_server(dead.pid, 5555)

    assert find_stale_server() is None
    assert not isolated_state.exists(), "a stale record must clean itself up"


def test_an_unrelated_process_is_never_claimed(isolated_state: Path) -> None:
    other = _spawn()
    try:
        record_running_server(other.pid, 5555)

        assert find_stale_server() is None, "only a NetSync server may be claimed"
    finally:
        other.kill()
        other.wait(timeout=10)


def test_a_live_server_is_found_and_can_be_stopped(isolated_state: Path) -> None:
    # argv carries the module name the real server is launched with.
    server = _spawn("styly_netsync")
    try:
        record_running_server(server.pid, 5555)

        assert find_stale_server() == {"pid": server.pid, "control_port": 5555}
        assert stop_stale_server(server.pid) is True
        assert not isolated_state.exists()
        assert find_stale_server() is None
    finally:
        server.kill()
        server.wait(timeout=10)


def test_the_record_survives_a_round_trip(isolated_state: Path) -> None:
    record_running_server(4242, 6000)

    assert json.loads(isolated_state.read_text(encoding="utf-8")) == {
        "pid": 4242,
        "control_port": 6000,
    }
    clear_running_server()
    assert not isolated_state.exists()
    clear_running_server()  # removing twice must not raise


# --------------------------------------------------------------------------
# Single-instance lock
# --------------------------------------------------------------------------


def test_second_launcher_is_locked_out() -> None:
    first = acquire_single_instance()
    assert first is not None, "the lock should be free in a clean test run"
    try:
        assert acquire_single_instance() is None
    finally:
        first.close()

    released = acquire_single_instance()
    assert released is not None, "closing must release the lock"
    released.close()


# --------------------------------------------------------------------------
# Stop signals (used by the launcher's Stop button)
# --------------------------------------------------------------------------


def test_install_stop_signal_handlers_registers_sigterm() -> None:
    previous = signal.getsignal(signal.SIGTERM)
    try:
        install_stop_signal_handlers()
        handler = signal.getsignal(signal.SIGTERM)
        assert callable(handler)
        with pytest.raises(KeyboardInterrupt):
            handler(signal.SIGTERM, None)  # type: ignore[operator]
    finally:
        signal.signal(signal.SIGTERM, previous)
