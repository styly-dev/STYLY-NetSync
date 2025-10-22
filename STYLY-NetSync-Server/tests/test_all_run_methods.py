#!/usr/bin/env python3
"""
Test all execution methods documented in CLAUDE.md

This test ensures that all documented ways to run the server and client simulator
work correctly, preventing regression when code is refactored.
"""

import os
import subprocess
import sys
import time
from pathlib import Path
from shutil import which

import pytest

# Get the project root directory
PROJECT_ROOT = Path(__file__).parent.parent


class TestAllRunMethods:
    """Test all documented execution methods for server and client simulator."""

    def run_command(self, command, args=None, timeout=5, expect_help=False):
        """
        Run a command and check if it executes successfully.
        
        Args:
            command: Command to run (can be string or list)
            args: Additional arguments (default: ['--help'])
            timeout: Maximum time to wait for command
            expect_help: If True, expect the command to show help and exit
        
        Returns:
            tuple: (success, stdout, stderr, return_code)
        """
        if args is None:
            args = ['--help'] if expect_help else []

        if isinstance(command, str):
            command = command.split()

        full_command = command + args

        try:
            # Change to project root for consistent execution
            original_cwd = os.getcwd()
            os.chdir(PROJECT_ROOT)

            env = os.environ.copy()
            src_path = str(PROJECT_ROOT / "src")
            existing_pythonpath = env.get("PYTHONPATH")
            if existing_pythonpath:
                env["PYTHONPATH"] = os.pathsep.join([src_path, existing_pythonpath])
            else:
                env["PYTHONPATH"] = src_path

            process = subprocess.Popen(
                full_command,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                env=env,
            )

            if expect_help:
                # For --help, wait for completion
                stdout, stderr = process.communicate(timeout=timeout)
                return_code = process.returncode
                success = return_code == 0
            else:
                # For server processes, check if they start successfully
                # Give it a moment to start
                time.sleep(1)

                # Check if process is still running
                if process.poll() is None:
                    # Process is running, terminate it
                    process.terminate()
                    try:
                        process.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()

                    stdout, stderr = process.communicate()
                    # If we got here, the process started successfully
                    success = True
                    return_code = 0
                else:
                    # Process ended on its own
                    stdout, stderr = process.communicate()
                    return_code = process.returncode
                    # Check if it failed immediately
                    success = return_code == 0

            return success, stdout, stderr, return_code

        except subprocess.TimeoutExpired:
            process.kill()
            stdout, stderr = process.communicate()
            return False, stdout, stderr, -1
        except Exception as e:
            return False, "", str(e), -1
        finally:
            os.chdir(original_cwd)

    # Server execution tests

    def test_server_cli_entry_point_help(self):
        """Test: styly-netsync-server --help"""
        if which("styly-netsync-server") is None:
            pytest.skip("Server CLI entry point not installed in test environment")
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-server',
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Server" in stdout or "STYLY NetSync Server" in stderr
        assert code == 0

    def test_server_module_execution_help(self):
        """Test: python -m styly_netsync --help (development/debugging)"""
        success, stdout, stderr, code = self.run_command(
            [sys.executable, '-m', 'styly_netsync'],
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Server" in stdout or "STYLY NetSync Server" in stderr
        assert code == 0

    # Note: Direct script execution (python server.py) is no longer supported

    def test_server_cli_entry_point_run(self):
        """Test: styly-netsync-server (brief run)"""
        if which("styly-netsync-server") is None:
            pytest.skip("Server CLI entry point not installed in test environment")
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-server',
            args=['--dealer-port', '15555', '--pub-port', '15556', '--beacon-port', '19999'],
            expect_help=False,
            timeout=3
        )
        assert success, f"Server failed to start. stderr: {stderr}"

    # Client simulator execution tests

    def test_simulator_cli_entry_point_help(self):
        """Test: styly-netsync-simulator --help"""
        if which("styly-netsync-simulator") is None:
            pytest.skip("Simulator CLI entry point not installed in test environment")
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-simulator',
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Client Simulator" in stdout or "Client Simulator" in stdout or "client_simulator" in stdout.lower()
        assert code == 0

    # Removed deprecated execution methods:
    # - python src/styly_netsync/client_simulator.py
    # - python -m styly_netsync.client_simulator

    # Server options tests

    def test_server_with_custom_ports(self):
        """Test: styly-netsync-server --dealer-port 5555 --pub-port 5556 --beacon-port 9999"""
        if which("styly-netsync-server") is None:
            pytest.skip("Server CLI entry point not installed in test environment")
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-server',
            args=['--dealer-port', '45555', '--pub-port', '45556', '--beacon-port', '49999'],
            expect_help=False,
            timeout=3
        )
        assert success, f"Server with custom ports failed. stderr: {stderr}"

    def test_server_no_beacon(self):
        """Test: styly-netsync-server --no-beacon"""
        if which("styly-netsync-server") is None:
            pytest.skip("Server CLI entry point not installed in test environment")
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-server',
            args=['--no-beacon', '--dealer-port', '55555', '--pub-port', '55556'],
            expect_help=False,
            timeout=3
        )
        assert success, f"Server with --no-beacon failed. stderr: {stderr}"


if __name__ == "__main__":
    # Run tests with pytest if available, otherwise run directly
    try:
        import pytest
        pytest.main([__file__, '-v'])
    except ImportError:
        # Basic test runner without pytest
        test_instance = TestAllRunMethods()
        test_methods = [method for method in dir(test_instance) if method.startswith('test_')]

        passed = 0
        failed = 0

        for method_name in test_methods:
            print(f"Running {method_name}...", end=" ")
            try:
                method = getattr(test_instance, method_name)
                method()
                print("✓ PASSED")
                passed += 1
            except AssertionError as e:
                print(f"✗ FAILED: {e}")
                failed += 1
            except Exception as e:
                print(f"✗ ERROR: {e}")
                failed += 1

        print(f"\nResults: {passed} passed, {failed} failed")
        sys.exit(0 if failed == 0 else 1)
