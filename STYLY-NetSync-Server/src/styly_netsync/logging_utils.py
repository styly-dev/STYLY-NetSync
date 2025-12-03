# logging_utils.py
import sys
import logging
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
_last_rotation_time: float | None = None


class InterceptHandler(logging.Handler):
    """Redirect stdlib logging to loguru."""

    def emit(self, record: logging.LogRecord) -> None:
        try:
            level = logger.level(record.levelname).name
        except Exception:
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
    except Exception:
        return Path(str(name))


def _get_rotation_start_time(file_path: Path, record_ts: float) -> float:
    """Return the baseline timestamp for age-based rotation."""

    global _last_rotation_time

    if _last_rotation_time is not None:
        return _last_rotation_time

    start_time = None
    try:
        start_time = get_ctime(str(file_path))
    except Exception:
        pass

    _last_rotation_time = start_time or record_ts
    return _last_rotation_time


def _default_rotation_condition(message, file) -> bool:  # type: ignore[override]
    """Rotate when file exceeds size or age thresholds."""

    global _last_rotation_time

    record_ts = message.record["time"].timestamp()

    try:
        path = _resolve_log_path(file)
        stat = path.stat()
    except Exception:
        return False

    if stat.st_size >= LOG_ROTATION_SIZE_BYTES:
        _last_rotation_time = record_ts
        return True

    start_ts = _get_rotation_start_time(path, record_ts)
    if record_ts - start_ts >= LOG_ROTATION_MAX_AGE.total_seconds():
        _last_rotation_time = record_ts
        return True

    return False


def _default_retention_policy(logs):  # type: ignore[override]
    """Keep the newest N files; delete older ones."""

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
        except Exception:
            continue


def configure_logging(
    log_dir: Path | None,
    console_level: str = "INFO",
    console_json: bool = False,
    rotation: str | None = None,
    retention: str | None = None,
) -> None:
    logger.remove()

    console_format = (
        "<green>{time:HH:mm:ss}</green> | <level>{level: <8}</level> | {message}"
    )
    logger.add(
        sys.stderr,
        level=str(console_level).upper(),
        serialize=console_json,
        format=console_format,
        enqueue=True,
        backtrace=False,
        diagnose=False,
    )

    rotation_rule = rotation if rotation else _default_rotation_condition
    retention_rule = retention if retention else _default_retention_policy

    if log_dir is not None:
        log_dir_path = Path(log_dir)
        log_dir_path.mkdir(parents=True, exist_ok=True)
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
        logger.info(f"File logging enabled at {log_file} (rotation/retention active)")

    logging.basicConfig(handlers=[InterceptHandler()], level=logging.NOTSET, force=True)
    logging.captureWarnings(True)

