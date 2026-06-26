# logging_utils.py
from __future__ import annotations

import json
import logging
import sys
import threading
from collections.abc import Callable, Iterator
from datetime import UTC, datetime, time, timedelta
from pathlib import Path
from typing import Any

from loguru import logger

# NOTE: This imports a loguru internal helper; we rely on it because stdlib
# file birth time is not portable, and we need a cross-platform ctime source
# for rotation age calculations.
from loguru._ctime_functions import get_ctime

LOG_ROTATION_SIZE_BYTES = 10 * 1024 * 1024
LOG_ROTATION_MAX_AGE = timedelta(days=7)
LOG_RETENTION_MAX_FILES = 20
DEFAULT_LOG_FILENAME = "netsync-server.log"
LOG_LEVEL_SEVERITY = {
    "TRACE": 5,
    "DEBUG": 10,
    "INFO": 20,
    "SUCCESS": 25,
    "WARNING": 30,
    "WARN": 30,
    "ERROR": 40,
    "CRITICAL": 50,
}
# Severity names accepted by the log export API, ordered from least to most severe.
VALID_MIN_SEVERITIES = tuple(
    sorted(LOG_LEVEL_SEVERITY, key=lambda name: (LOG_LEVEL_SEVERITY[name], name))
)
RotationRule = str | int | time | timedelta | Callable[..., bool]
RetentionRule = str | int | timedelta | Callable[[list[str]], None]


class _RotationState:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._last: float | None = None

    def get(self) -> float | None:
        with self._lock:
            return self._last

    def set(self, value: float | None) -> None:
        with self._lock:
            self._last = value

    def reset(self) -> None:
        self.set(None)

    def get_or_set(self, factory: Callable[[], float]) -> float:
        """Return cached value or compute/set once under lock."""

        with self._lock:
            if self._last is not None:
                return self._last

            self._last = factory()
            return self._last


_rotation_state = _RotationState()


class InterceptHandler(logging.Handler):
    """Redirect stdlib logging to loguru."""

    def emit(self, record: logging.LogRecord) -> None:
        level: str | int
        try:
            level = logger.level(record.levelname).name
        except (ValueError, TypeError):
            level = record.levelno

        frame = logging.currentframe()
        depth = 2
        while frame and frame.f_code.co_filename == logging.__file__:
            frame = frame.f_back  # type: ignore[assignment]
            depth += 1

        logger.opt(depth=depth, exception=record.exc_info).log(
            level, record.getMessage()
        )


def _resolve_log_path(file: Any) -> Path:
    """Return a Path for the log file supporting loguru handles and Path objects."""

    if isinstance(file, Path):
        return file

    name = getattr(file, "name", file)
    try:
        return Path(name)
    except (OSError, TypeError, ValueError):
        return Path(str(name))


def _get_rotation_start_time(file_path: Path, record_ts: float) -> float:
    """
    Baseline timestamp for age-based rotation.

    Args:
        file_path: Path of the log file being evaluated.
        record_ts: Current log record timestamp used as a fallback.
    """

    def _compute_baseline() -> float:
        start_time = None
        try:
            start_time = get_ctime(str(file_path))
        except (OSError, ValueError) as exc:
            logger.debug(f"get_ctime failed for {file_path}: {exc}")
        return start_time or record_ts

    return _rotation_state.get_or_set(_compute_baseline)


def _default_rotation_condition(message: Any, file: Any) -> bool:
    """Rotate when file exceeds size or age thresholds."""

    record_ts = message.record["time"].timestamp()

    try:
        path = _resolve_log_path(file)
        stat = path.stat()
    except (OSError, TypeError, ValueError) as exc:
        logger.debug(f"Rotation check skipped; stat failed: {exc}")
        return False

    if stat.st_size >= LOG_ROTATION_SIZE_BYTES:
        _rotation_state.set(record_ts)
        return True

    start_ts = _get_rotation_start_time(path, record_ts)
    if record_ts - start_ts >= LOG_ROTATION_MAX_AGE.total_seconds():
        _rotation_state.set(record_ts)
        return True

    return False


def _default_retention_policy(logs: list[Any]) -> None:
    """
    Keep the newest log files and drop older ones.

    Args:
        logs: Paths (string or Path) of log files to consider.
    """

    valid_logs: list[tuple[float, Any]] = []
    for path in logs:
        try:
            p = Path(path)
            valid_logs.append((p.stat().st_mtime, p))
        except (OSError, TypeError, ValueError):
            continue

    valid_logs.sort(key=lambda item: item[0], reverse=True)
    for _, path in valid_logs[LOG_RETENTION_MAX_FILES:]:
        try:
            path.unlink()
        except OSError as exc:
            logger.debug(f"Retention skip for {path}: {exc}")
            continue


def configure_logging(
    log_dir: Path | None,
    console_level: str = "INFO",
    console_json: bool = False,
    rotation: RotationRule | None = None,
    retention: RetentionRule | None = None,
) -> None:
    """
    Initialize console logging and optional rotated file sink (default: rotate at 10 MB or 7 days, keep newest 20 files).

    Args:
        log_dir: Target directory for `netsync-server.log`; enables file sink when set.
        console_level: Console level string (e.g., INFO/DEBUG).
        console_json: Emit console as JSON when True; otherwise colored text.
        rotation: loguru rotation rule (e.g., '10 MB', '1 day', '12:00') or callable.
        retention: loguru retention rule (e.g., '5', '1 week', 'keep 10 files') or callable.
    """
    reset_rotation_state()
    logger.remove()

    console_kwargs: dict[str, Any] = {
        "level": console_level.upper(),
        "serialize": console_json,
        "enqueue": True,
        "backtrace": False,
        "diagnose": False,
    }
    if not console_json:
        console_kwargs["format"] = (
            "<green>{time:HH:mm:ss}</green> | <level>{level: <8}</level> | {message}"
        )

    logger.add(sys.stderr, **console_kwargs)

    if log_dir is not None:
        rotation_rule: RotationRule = (
            rotation if rotation is not None else _default_rotation_condition
        )
        retention_rule: RetentionRule = (
            retention if retention is not None else _default_retention_policy
        )

        log_dir_path = Path(log_dir)
        try:
            log_dir_path.mkdir(parents=True, exist_ok=True)
        except OSError as exc:
            logger.error(f"Failed to create log directory {log_dir_path}: {exc}")
        else:
            log_file = log_dir_path / DEFAULT_LOG_FILENAME
            logger.add(
                log_file,
                level="DEBUG",
                serialize=True,
                rotation=rotation_rule,
                retention=retention_rule,
                enqueue=True,
                backtrace=False,
                diagnose=False,
            )
            logger.info(
                f"File logging enabled at {log_file} (rotation/retention active)"
            )

    logging.basicConfig(handlers=[InterceptHandler()], level=logging.NOTSET, force=True)
    logging.captureWarnings(True)


def iter_exported_log_lines(
    log_dir: Path,
    from_ts: datetime,
    to_ts: datetime,
    min_severity: str | None = None,
) -> Iterator[str]:
    """Yield original loguru JSONL lines matching the requested time window."""

    min_level_no = _parse_min_severity(min_severity)
    from_utc = _to_utc(from_ts)
    to_utc = _to_utc(to_ts)

    for log_file in sorted(log_dir.glob("netsync-server*.log")):
        if not log_file.is_file():
            continue
        try:
            with log_file.open("r", encoding="utf-8") as file:
                for raw_line in file:
                    line = raw_line.rstrip("\r\n")
                    if not line:
                        continue
                    try:
                        payload = json.loads(line)
                    except json.JSONDecodeError:
                        logger.warning(
                            f"Skipping malformed NetSync log line in {log_file}"
                        )
                        continue
                    record = payload.get("record")
                    if not isinstance(record, dict):
                        logger.warning(
                            f"Skipping NetSync log line without record in {log_file}"
                        )
                        continue
                    record_time = _extract_record_time(record)
                    if record_time is None:
                        logger.warning(
                            f"Skipping NetSync log line without timestamp in {log_file}"
                        )
                        continue
                    record_time_utc = _to_utc(record_time)
                    if record_time_utc < from_utc or record_time_utc > to_utc:
                        continue
                    if min_level_no is not None:
                        level_no = _extract_level_no(record)
                        if level_no is None or level_no < min_level_no:
                            continue
                    yield line
        except OSError as exc:
            logger.warning(f"Failed to read NetSync log file {log_file}: {exc}")


def _parse_min_severity(min_severity: str | None) -> int | None:
    if min_severity is None:
        return None
    normalized = min_severity.strip().upper()
    if not normalized:
        return None
    if normalized not in LOG_LEVEL_SEVERITY:
        raise ValueError(f"Unsupported min_severity: {min_severity}")
    return LOG_LEVEL_SEVERITY[normalized]


def _extract_record_time(record: dict[str, Any]) -> datetime | None:
    time_value = record.get("time")
    if not isinstance(time_value, dict):
        return None

    timestamp = time_value.get("timestamp")
    if isinstance(timestamp, int | float):
        return datetime.fromtimestamp(timestamp, tz=UTC)

    repr_value = time_value.get("repr")
    if isinstance(repr_value, str):
        normalized = repr_value.replace("Z", "+00:00")
        try:
            return datetime.fromisoformat(normalized)
        except ValueError:
            return None
    return None


def _extract_level_no(record: dict[str, Any]) -> int | None:
    level = record.get("level")
    if not isinstance(level, dict):
        return None

    level_no = level.get("no")
    if isinstance(level_no, int):
        return level_no

    level_name = level.get("name")
    if isinstance(level_name, str):
        return LOG_LEVEL_SEVERITY.get(level_name.upper())
    return None


def _to_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)


def reset_rotation_state() -> None:
    """Helper for tests to reset cached rotation timestamp."""

    _rotation_state.reset()


def get_last_rotation_time() -> float | None:
    """Return the cached rotation timestamp (for tests/diagnostics)."""

    return _rotation_state.get()
