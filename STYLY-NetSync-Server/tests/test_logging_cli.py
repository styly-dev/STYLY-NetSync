import logging
import sys
import time
from datetime import datetime, timedelta
from pathlib import Path

import pytest

from styly_netsync import logging_utils, server

# NOTE: _patch_quick_exit monkeypatches time.sleep globally via server.time.
# Capture the real sleep so tests can opt out of the interrupting stub.
REAL_SLEEP = time.sleep


@pytest.fixture(autouse=True)
def _reset_rotation_state():
    # Keep rotation cache clean across tests without per-test calls
    logging_utils.reset_rotation_state()
    yield


def _patch_quick_exit(monkeypatch):
    # Skip base64 logo and avoid network lookups for determinism
    monkeypatch.setattr(server, "display_logo", lambda: None)
    monkeypatch.setattr(server.network_utils, "get_local_ip_addresses", lambda: [])

    # Force the main loop to exit immediately
    def _raise_keyboard_interrupt(seconds):
        raise KeyboardInterrupt

    monkeypatch.setattr(server.time, "sleep", _raise_keyboard_interrupt)


def _patch_dummy_server(monkeypatch, store):
    class DummyServer:
        def __init__(self, **kwargs):
            store["init_kwargs"] = kwargs
            self.running = False

        def start(self):
            store["started"] = True

        def stop(self):
            store["stopped"] = True

    monkeypatch.setattr(server, "NetSyncServer", DummyServer)


def test_main_logging_args_with_log_dir(monkeypatch, tmp_path):
    _patch_quick_exit(monkeypatch)
    store: dict[str, object] = {}
    _patch_dummy_server(monkeypatch, store)

    original_configure = server.configure_logging

    def wrapped_configure_logging(
        *,
        log_dir,
        console_level="INFO",
        console_json=False,
        rotation=None,
        retention=None,
    ):
        store["configure_args"] = {
            "log_dir": log_dir,
            "console_level": console_level,
            "console_json": console_json,
            "rotation": rotation,
            "retention": retention,
        }
        return original_configure(
            log_dir=log_dir,
            console_level=console_level,
            console_json=console_json,
            rotation=rotation,
            retention=retention,
        )

    monkeypatch.setattr(server, "configure_logging", wrapped_configure_logging)

    argv = [
        "server.py",
        "--log-dir",
        str(tmp_path),
        "--log-json-console",
        "--log-level-console",
        "DEBUG",
        "--log-rotation",
        "10 MB",
        "--log-retention",
        "5 days",
        "--no-server-discovery",
    ]
    monkeypatch.setattr(sys, "argv", argv)

    try:
        server.main()
    finally:
        server.logger.remove()

    assert store["configure_args"]["log_dir"] == Path(tmp_path)
    assert store["configure_args"]["console_json"] is True
    assert store["configure_args"]["console_level"] == "DEBUG"
    assert store["configure_args"]["rotation"] == "10 MB"
    assert store["configure_args"]["retention"] == "5 days"

    log_file = tmp_path / "netsync-server.log"
    for _ in range(40):
        if log_file.exists() and log_file.stat().st_size > 0:
            break
        time.sleep(0.05)

    assert log_file.exists()
    first_line = log_file.read_text().splitlines()[0]
    assert first_line.lstrip().startswith("{")


def test_main_logging_args_without_log_dir(monkeypatch):
    _patch_quick_exit(monkeypatch)
    store: dict[str, object] = {}
    _patch_dummy_server(monkeypatch, store)

    def wrapped_configure_logging(
        *,
        log_dir,
        console_level="INFO",
        console_json=False,
        rotation=None,
        retention=None,
    ):
        store["configure_args"] = {
            "log_dir": log_dir,
            "console_level": console_level,
            "console_json": console_json,
            "rotation": rotation,
            "retention": retention,
        }
        return None

    monkeypatch.setattr(server, "configure_logging", wrapped_configure_logging)

    argv = [
        "server.py",
        "--no-server-discovery",
    ]
    monkeypatch.setattr(sys, "argv", argv)

    server.main()

    assert store["configure_args"]["log_dir"] is None
    assert store["configure_args"]["console_json"] is False
    assert store["configure_args"]["rotation"] is None
    assert store["configure_args"]["retention"] is None


def test_rotation_triggers_on_age(monkeypatch, tmp_path):
    # Age-based rotation using a short max age to avoid time travel helpers
    store: dict[str, object] = {}
    _patch_quick_exit(monkeypatch)
    _patch_dummy_server(monkeypatch, store)
    monkeypatch.setattr(logging_utils, "LOG_ROTATION_MAX_AGE", timedelta(seconds=1))

    original_configure = server.configure_logging

    def wrapped_configure_logging(**kwargs):
        store["configure_args"] = kwargs
        return original_configure(**kwargs)

    monkeypatch.setattr(server, "configure_logging", wrapped_configure_logging)

    argv = [
        "server.py",
        "--log-dir",
        str(tmp_path),
        "--no-server-discovery",
    ]
    monkeypatch.setattr(sys, "argv", argv)
    try:
        server.main()
    finally:
        server.logger.remove()

    # Advance time beyond age threshold to trigger rotation on next write
    log_file = tmp_path / "netsync-server.log"
    assert log_file.exists()
    REAL_SLEEP(1.2)

    server.logger.add(
        log_file,
        rotation=logging_utils._default_rotation_condition,
        serialize=True,
    )  # ensure handler present after previous remove
    server.logger.info("trigger rotation")
    server.logger.remove()

    rotated = sorted(tmp_path.glob("netsync-server*.log"))
    assert len(rotated) >= 2, "Expected rotation to create an additional log file"


def _make_message(ts: float):
    return type("Message", (), {"record": {"time": datetime.fromtimestamp(ts)}})()


class DummyLogger:
    def __init__(self):
        self.add_calls: list[dict[str, object]] = []
        self.errors: list[str] = []
        self.infos: list[str] = []
        self.logged: list[dict[str, object]] = []

    def remove(self):
        return None

    def add(self, *args, **kwargs):
        self.add_calls.append({"args": args, "kwargs": kwargs})
        return len(self.add_calls)

    def level(self, name):
        return type("Level", (), {"name": name})()

    def opt(self, depth, exception):
        self.logged.append({"depth": depth, "exception": exception})
        return self

    def log(self, level, message):
        self.logged[-1]["level"] = level
        self.logged[-1]["message"] = message

    def error(self, message):
        self.errors.append(str(message))

    def info(self, message):
        self.infos.append(str(message))


def test_rotation_triggers_on_size(monkeypatch, tmp_path):
    _patch_quick_exit(monkeypatch)
    store: dict[str, object] = {}
    _patch_dummy_server(monkeypatch, store)
    monkeypatch.setattr(logging_utils, "LOG_ROTATION_SIZE_BYTES", 1)

    original_configure = server.configure_logging

    def wrapped_configure_logging(**kwargs):
        store["configure_args"] = kwargs
        return original_configure(**kwargs)

    monkeypatch.setattr(server, "configure_logging", wrapped_configure_logging)

    argv = [
        "server.py",
        "--log-dir",
        str(tmp_path),
        "--no-server-discovery",
    ]
    monkeypatch.setattr(sys, "argv", argv)

    try:
        server.main()
    finally:
        server.logger.remove()

    log_file = tmp_path / "netsync-server.log"
    assert log_file.exists()

    server.logger.add(
        log_file,
        rotation=logging_utils._default_rotation_condition,
        serialize=True,
    )
    server.logger.info("trigger size rotation")
    server.logger.remove()

    rotated = sorted(tmp_path.glob("netsync-server*.log"))
    assert len(rotated) >= 2, "Expected size-based rotation to create another file"


def test_rotation_uses_cached_start_time(monkeypatch, tmp_path):
    log_file = tmp_path / "netsync-server.log"
    log_file.write_text("dummy\n", encoding="utf-8")

    start_ts = 1_000_000.0
    monkeypatch.setattr(logging_utils, "get_ctime", lambda path: start_ts)

    before_threshold = start_ts + logging_utils.LOG_ROTATION_MAX_AGE.total_seconds() - 1
    message_before = _make_message(before_threshold)
    assert logging_utils._default_rotation_condition(message_before, log_file) is False
    assert logging_utils.get_last_rotation_time() == pytest.approx(start_ts)

    after_threshold = start_ts + logging_utils.LOG_ROTATION_MAX_AGE.total_seconds() + 1
    message_after = _make_message(after_threshold)
    assert logging_utils._default_rotation_condition(message_after, log_file) is True
    assert logging_utils.get_last_rotation_time() == pytest.approx(after_threshold)


def test_intercept_handler_redirects_stdlib(monkeypatch):
    dummy_logger = DummyLogger()
    monkeypatch.setattr(logging_utils, "logger", dummy_logger)

    handler = logging_utils.InterceptHandler()
    record = logging.LogRecord(
        name="dummy",
        level=logging.WARNING,
        pathname=__file__,
        lineno=1,
        msg="hello",
        args=(),
        exc_info=None,
    )

    handler.emit(record)

    assert dummy_logger.logged[-1]["message"] == "hello"
    assert dummy_logger.logged[-1]["level"] == "WARNING"


def test_configure_logging_console_json(monkeypatch):
    dummy_logger = DummyLogger()
    monkeypatch.setattr(logging_utils, "logger", dummy_logger)
    monkeypatch.setattr(logging, "basicConfig", lambda **_: None)
    monkeypatch.setattr(logging, "captureWarnings", lambda *_, **__: None)

    logging_utils.configure_logging(
        log_dir=None, console_level="warning", console_json=True
    )

    console_kwargs = dummy_logger.add_calls[0]["kwargs"]
    assert console_kwargs["serialize"] is True
    assert "format" not in console_kwargs
    assert console_kwargs["level"] == "WARNING"


def test_configure_logging_uses_custom_rotation_and_retention(monkeypatch, tmp_path):
    dummy_logger = DummyLogger()
    monkeypatch.setattr(logging_utils, "logger", dummy_logger)
    monkeypatch.setattr(logging, "basicConfig", lambda **_: None)
    monkeypatch.setattr(logging, "captureWarnings", lambda *_, **__: None)

    def custom_rotation(*_, **__):
        return False

    def custom_retention(logs):
        logs.clear()

    logging_utils.configure_logging(
        log_dir=tmp_path,
        rotation=custom_rotation,
        retention=custom_retention,
    )

    assert len(dummy_logger.add_calls) == 2
    file_kwargs = dummy_logger.add_calls[1]["kwargs"]
    assert file_kwargs["rotation"] is custom_rotation
    assert file_kwargs["retention"] is custom_retention


def test_configure_logging_handles_directory_errors(monkeypatch, tmp_path):
    dummy_logger = DummyLogger()
    monkeypatch.setattr(logging_utils, "logger", dummy_logger)
    monkeypatch.setattr(logging, "basicConfig", lambda **_: None)
    monkeypatch.setattr(logging, "captureWarnings", lambda *_, **__: None)

    def fail_mkdir(*_, **__):  # pragma: no cover - error path
        raise OSError("fail")

    monkeypatch.setattr(logging_utils.Path, "mkdir", fail_mkdir)

    logging_utils.configure_logging(log_dir=tmp_path / "logs")

    assert dummy_logger.errors, "Expected log directory failure to be reported"
    assert len(dummy_logger.add_calls) == 1, "File sink should not be registered"
