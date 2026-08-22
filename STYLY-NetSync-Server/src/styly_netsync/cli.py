"""
Command-line interface wrapper for STYLY NetSync Server.

This module provides the CLI entry point that will be used by uvx and pip-installed
scripts. It simply delegates to the main() function in the server module.
"""

import signal
import sys
from types import FrameType

from .server import main


def _raise_keyboard_interrupt(signum: int, frame: FrameType | None) -> None:
    """Turn a stop signal into the shutdown path the server already handles."""
    raise KeyboardInterrupt


def install_stop_signal_handlers() -> None:
    """Make SIGTERM (and Ctrl-Break on Windows) shut the server down cleanly.

    Without this the server only reacts to Ctrl-C, so process supervisors -
    the desktop launcher, ``docker stop``, systemd - would kill it outright.
    Signals can only be registered on the main thread; anywhere else this is a
    no-op.
    """
    for name in ("SIGTERM", "SIGBREAK"):
        sig = getattr(signal, name, None)
        if sig is None:
            continue
        try:
            signal.signal(sig, _raise_keyboard_interrupt)
        except (ValueError, OSError):  # not the main thread / unsupported
            pass


def cli_main() -> None:
    """
    Main CLI entry point for the styly-netsync-server command.

    This function is referenced in pyproject.toml as the console script entry point.
    It delegates to the main() function which handles argument parsing and server setup.
    """
    install_stop_signal_handlers()
    try:
        main()
    except KeyboardInterrupt:
        print("\nServer interrupted by user")
        sys.exit(0)
    except SystemExit:
        # Let SystemExit pass through as-is (from main())
        raise
    except Exception as e:
        print(f"Error: {e}")
        sys.exit(1)


if __name__ == "__main__":
    cli_main()
