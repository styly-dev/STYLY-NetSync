#!/usr/bin/env python3
"""
Test all execution methods documented in CLAUDE.md

This test ensures that all documented ways to run the server and client simulator
work correctly, preventing regression when code is refactored.
"""

import subprocess
import sys
import time
import signal
import os
import pytest
from pathlib import Path

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
            
            process = subprocess.Popen(
                full_command,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True
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
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-server',
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Server" in stdout or "STYLY NetSync Server" in stderr
        assert code == 0

    def test_server_module_execution_help(self):
        """Test: python -m styly_netsync --help"""
        success, stdout, stderr, code = self.run_command(
            [sys.executable, '-m', 'styly_netsync'],
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Server" in stdout or "STYLY NetSync Server" in stderr
        assert code == 0

    def test_server_direct_script_help(self):
        """Test: python src/styly_netsync/server.py --help"""
        success, stdout, stderr, code = self.run_command(
            [sys.executable, 'src/styly_netsync/server.py'],
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Server" in stdout or "STYLY NetSync Server" in stderr
        assert code == 0

    def test_server_cli_entry_point_run(self):
        """Test: styly-netsync-server (brief run)"""
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-server',
            args=['--dealer-port', '15555', '--pub-port', '15556', '--beacon-port', '19999'],
            expect_help=False,
            timeout=3
        )
        assert success, f"Server failed to start. stderr: {stderr}"

    def test_server_module_execution_run(self):
        """Test: python -m styly_netsync (brief run)"""
        success, stdout, stderr, code = self.run_command(
            [sys.executable, '-m', 'styly_netsync'],
            args=['--dealer-port', '25555', '--pub-port', '25556', '--beacon-port', '29999'],
            expect_help=False,
            timeout=3
        )
        assert success, f"Server failed to start. stderr: {stderr}"

    def test_server_direct_script_run(self):
        """Test: python src/styly_netsync/server.py (brief run)"""
        success, stdout, stderr, code = self.run_command(
            [sys.executable, 'src/styly_netsync/server.py'],
            args=['--dealer-port', '35555', '--pub-port', '35556', '--beacon-port', '39999'],
            expect_help=False,
            timeout=3
        )
        assert success, f"Server failed to start. stderr: {stderr}"

    # Client simulator execution tests
    
    def test_simulator_cli_entry_point_help(self):
        """Test: styly-netsync-simulator --help"""
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-simulator',
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Client Simulator" in stdout or "Client Simulator" in stdout or "client_simulator" in stdout.lower()
        assert code == 0

    def test_simulator_direct_script_help(self):
        """Test: python src/styly_netsync/client_simulator.py --help"""
        success, stdout, stderr, code = self.run_command(
            [sys.executable, 'src/styly_netsync/client_simulator.py'],
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Client Simulator" in stdout or "Client Simulator" in stdout or "client_simulator" in stdout.lower()
        assert code == 0

    def test_simulator_module_execution_help(self):
        """Test: python -m styly_netsync.client_simulator --help"""
        success, stdout, stderr, code = self.run_command(
            [sys.executable, '-m', 'styly_netsync.client_simulator'],
            args=['--help'],
            expect_help=True
        )
        assert success, f"Failed with stderr: {stderr}"
        assert "STYLY NetSync Client Simulator" in stdout or "Client Simulator" in stdout or "client_simulator" in stdout.lower()
        assert code == 0

    # Server options tests
    
    def test_server_with_custom_ports(self):
        """Test: styly-netsync-server --dealer-port 5555 --pub-port 5556 --beacon-port 9999"""
        success, stdout, stderr, code = self.run_command(
            'styly-netsync-server',
            args=['--dealer-port', '45555', '--pub-port', '45556', '--beacon-port', '49999'],
            expect_help=False,
            timeout=3
        )
        assert success, f"Server with custom ports failed. stderr: {stderr}"

    def test_server_no_beacon(self):
        """Test: styly-netsync-server --no-beacon"""
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