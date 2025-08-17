"""
Command-line interface wrapper for STYLY NetSync Server.

This module provides the CLI entry point that will be used by uvx and pip-installed
scripts. It simply delegates to the main() function in the server module.
"""

import sys

from .server import main


def cli_main() -> None:
    """
    Main CLI entry point for the styly-netsync-server command.
    
    This function is referenced in pyproject.toml as the console script entry point.
    It delegates to the main() function which handles argument parsing and server setup.
    """
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
