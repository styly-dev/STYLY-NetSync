"""
Main entry point for running STYLY NetSync Server as a module.

This allows the package to be executed with:
    python -m styly_netsync

The main() function from the server module handles all command-line argument parsing
and server initialization.
"""

from .server import main

if __name__ == "__main__":
    main()