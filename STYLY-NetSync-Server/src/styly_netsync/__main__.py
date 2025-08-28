"""
Main entry point for running STYLY NetSync Server as a module.

This allows the package to be executed with:
    python -m styly_netsync

This is useful for development and debugging purposes.
The recommended way to run the server is using the installed CLI command:
    styly-netsync-server
"""

from .cli import cli_main

if __name__ == "__main__":
    cli_main()