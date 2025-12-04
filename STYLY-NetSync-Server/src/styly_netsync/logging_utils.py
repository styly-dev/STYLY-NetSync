# logging_utils.py
import sys
import logging
import threading
from collections.abc import Callable
from datetime import timedelta
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
RotationRule = str | int | float | timedelta | Callable[[Any, Any], bool]
RetentionRule = str | int | float | timedelta | Callable[[list[Any]], Any]


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


def _resolve_log_path(file) -> Path:
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


def reset_rotation_state() -> None:
    """Helper for tests to reset cached rotation timestamp."""

    _rotation_state.reset()


def get_last_rotation_time() -> float | None:
    """Return the cached rotation timestamp (for tests/diagnostics)."""

    return _rotation_state.get()
