using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Styly.NetSync.Editor
{
    public static class StartPythonServer
    {
        [MenuItem("STYLY/STYLY NetSync/Start NetSync Server", false, 100)]
        public static void StartServer()
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                StartServerMac();
            }
            else if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                StartServerWindows();
            }
            else if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                StartServerLinux();
            }
            else
            {
                EditorUtility.DisplayDialog("Unsupported Platform",
                    "Starting Python server is only supported on Windows, macOS, and Linux.", "OK");
            }
        }

        private static string GetServerVersionSafe()
        {
            string serverVersion;
            try
            {
                serverVersion = Information.GetVersion();
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to get server version: " + ex.Message);
                EditorUtility.DisplayDialog("Error", "Failed to get server version. Defaulting to latest.\n\n" + ex.Message, "OK");
                serverVersion = "latest";
            }

            // Fallback to "latest" if version is unknown
            if (serverVersion == "unknown")
            {
                serverVersion = "latest";
            }

            // Sanitize version to prevent script injection
            return SanitizeVersion(serverVersion);
        }

        private static string SanitizeVersion(string version)
        {
            // Remove any characters that could break shell scripts
            return System.Text.RegularExpressions.Regex.Replace(version, @"[^\w\.\-]", "");
        }

        private static string FindTerminalPath()
        {
            string[] possiblePaths = {
                "/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal",
                "/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal",
                "/System/Applications/Terminal.app/Contents/MacOS/Terminal",
                "/Applications/Terminal.app/Contents/MacOS/Terminal"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path)) return path;
            }

            throw new FileNotFoundException("Terminal application not found. Please ensure Terminal.app is installed.");
        }

        private static string GetProjectRoot()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../"));
            if (!Directory.Exists(projectRoot))
            {
                Debug.LogWarning($"Project root directory not found: {projectRoot}. Using Assets folder as fallback.");
                projectRoot = Application.dataPath;
            }
            return projectRoot;
        }

        private static void ScheduleTempFileCleanup(string filePath, int delayMs = 5000)
        {
            System.Threading.Tasks.Task.Delay(delayMs).ContinueWith(_ =>
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Debug.Log($"Cleaned up temporary script: {filePath}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to clean up temporary script: {e.Message}");
                }
            });
        }

        private static void StartServerMac()
        {
            string serverVersion = GetServerVersionSafe();
            string terminal;
            try
            {
                terminal = FindTerminalPath();
            }
            catch (FileNotFoundException e)
            {
                Debug.LogError(e.Message);
                EditorUtility.DisplayDialog("Error", e.Message, "OK");
                return;
            }

            string shellScript = @"#!/bin/bash
clear
echo 'STYLY NetSync Python Server Setup'
echo '=================================='
echo ''

# Check if uv exists
if ! command -v uv &> /dev/null; then
    echo 'uv is not installed on your system.'
    echo ''

    # Check if brew exists
    if command -v brew &> /dev/null; then
        echo 'Homebrew is installed. Would you like to install uv using brew?'
        echo 'This will run: brew install uv'
        echo ''
        read -p 'Install uv? (y/n): ' -n 1 -r
        echo ''
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            echo 'Installing uv with Homebrew...'
            brew install uv
            if [ $? -eq 0 ]; then
                echo 'uv installed successfully!'
            else
                echo 'Failed to install uv. Please install it manually.'
                read -p 'Press any key to exit...'
                exit 1
            fi
        else
            echo 'Installation cancelled.'
            read -p 'Press any key to exit...'
            exit 1
        fi
    else
        echo 'Homebrew is not installed. Would you like to install uv using the official installer?'
        echo 'This will run: curl -LsSf https://astral.sh/uv/install.sh | sh'
        echo ''
        read -p 'Install uv? (y/n): ' -n 1 -r
        echo ''
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            echo 'Installing uv with the official installer...'
            curl -LsSf https://astral.sh/uv/install.sh | sh
            if [ $? -eq 0 ]; then
                echo 'uv installed successfully!'
                # Add uv to PATH for current session
                export PATH=""$HOME/.local/bin:$PATH""
            else
                echo 'Failed to install uv. Please install it manually.'
                read -p 'Press any key to exit...'
                exit 1
            fi
        else
            echo 'Installation cancelled.'
            read -p 'Press any key to exit...'
            exit 1
        fi
    fi
fi

echo ''
echo 'Starting STYLY NetSync Python Server...'
echo 'Server version: " + serverVersion + @"'
echo ''
echo 'Running: uvx styly-netsync-server@" + serverVersion + @"'
echo ''
echo '========================================='
echo ''

# Start the server
uvx styly-netsync-server@" + serverVersion + @"

# Keep terminal open if server exits
echo ''
echo 'Server stopped.'
read -p 'Press any key to exit...'
";

            // Create a unique temp file with .sh extension
            string tempScriptPath = Path.Combine(Path.GetTempPath(), $"start_styly_netsync_server_{Guid.NewGuid():N}.sh");
            File.WriteAllText(tempScriptPath, shellScript);

            // Make script executable
            try
            {
                Process chmod = new Process();
                chmod.StartInfo.FileName = "/bin/chmod";
                chmod.StartInfo.Arguments = $"+x \"{tempScriptPath}\"";
                chmod.StartInfo.UseShellExecute = false;
                chmod.StartInfo.CreateNoWindow = true;
                chmod.Start();
                chmod.WaitForExit();
                if (chmod.ExitCode != 0)
                {
                    Debug.LogError($"Failed to make script executable. chmod exit code: {chmod.ExitCode}");
                    EditorUtility.DisplayDialog("Error",
                        $"Failed to make script executable. chmod exit code: {chmod.ExitCode}", "OK");
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception while making script executable: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Exception while making script executable: {e.Message}", "OK");
                return;
            }

            // Get the project root directory (where STYLY-NetSync-Server is located)
            string projectRoot = GetProjectRoot();

            // Execute script in Terminal
            Process process = new Process();
            process.StartInfo.FileName = terminal;
            process.StartInfo.Arguments = tempScriptPath;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.WorkingDirectory = projectRoot;

            try
            {
                process.Start();
                Debug.Log("STYLY NetSync: Starting Python server in Terminal...");

                // Schedule cleanup of temporary script file
                ScheduleTempFileCleanup(tempScriptPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start Terminal: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Failed to start Terminal: {e.Message}", "OK");
            }
        }

        private static void StartServerWindows()
        {
            string serverVersion = GetServerVersionSafe();
            string powershellScript = @"
Clear-Host
Write-Host 'STYLY NetSync Python Server Setup' -ForegroundColor Cyan
Write-Host '==================================' -ForegroundColor Cyan
Write-Host ''

# Check if uv exists
$uvExists = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uvExists) {
    Write-Host 'uv is not installed on your system.' -ForegroundColor Yellow
    Write-Host ''

    # Check if winget exists
    $wingetExists = Get-Command winget -ErrorAction SilentlyContinue
    if ($wingetExists) {
        Write-Host 'Would you like to install uv using winget?'
        Write-Host 'This will run: winget install --id=astral-sh.uv -e'
        Write-Host ''
        $response = Read-Host 'Install uv? (y/n)'
        if ($response -eq 'y' -or $response -eq 'Y') {
            Write-Host 'Installing uv with winget...' -ForegroundColor Green
            winget install --id=astral-sh.uv -e
            if ($LASTEXITCODE -eq 0) {
                Write-Host 'uv installed successfully!' -ForegroundColor Green
                # Refresh PATH
                $env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path','User')
            } else {
                Write-Host 'Failed to install uv. Please install it manually.' -ForegroundColor Red
                Write-Host 'Visit: https://docs.astral.sh/uv/getting-started/installation/'
                Read-Host 'Press Enter to exit'
                exit 1
            }
        } else {
            Write-Host 'Installation cancelled.' -ForegroundColor Yellow
            Read-Host 'Press Enter to exit'
            exit 1
        }
    } else {
        Write-Host 'winget is not available. Please install uv manually.' -ForegroundColor Red
        Write-Host 'You can install it from: https://docs.astral.sh/uv/getting-started/installation/'
        Write-Host ''
        Write-Host 'Or use PowerShell (Run as Administrator):' -ForegroundColor Yellow
        Write-Host 'powershell -ExecutionPolicy ByPass -c ""irm https://astral.sh/uv/install.ps1 | iex""' -ForegroundColor Gray
        Read-Host 'Press Enter to exit'
        exit 1
    }
}

Write-Host ''
Write-Host 'Starting STYLY NetSync Python Server...' -ForegroundColor Green
Write-Host 'Server version: " + serverVersion + @"' -ForegroundColor Gray
Write-Host ''
Write-Host 'Running: uvx styly-netsync-server@" + serverVersion + @"' -ForegroundColor Cyan
Write-Host ''
Write-Host '=========================================' -ForegroundColor Cyan
Write-Host ''

# Start the server
uvx styly-netsync-server@" + serverVersion + @"

# Keep terminal open if server exits
Write-Host ''
Write-Host 'Server stopped.' -ForegroundColor Yellow
Read-Host 'Press Enter to exit'
";

            // Create a unique temp file and change its extension to .ps1
            string tempScriptPath = Path.ChangeExtension(Path.GetTempFileName(), ".ps1");
            File.WriteAllText(tempScriptPath, powershellScript);

            // Get the project root directory
            string projectRoot = GetProjectRoot();

            // Notify user about execution policy
            bool proceed = EditorUtility.DisplayDialog("PowerShell Execution Policy",
                "The server startup script will run with PowerShell's ExecutionPolicy set to Bypass for this session only. " +
                "This is required to run the installation script but does not affect your system's security settings permanently.\n\n" +
                "Do you want to proceed?",
                "Yes, Start Server", "Cancel");

            if (!proceed)
            {
                Debug.Log("STYLY NetSync: Server startup cancelled by user.");
                File.Delete(tempScriptPath);
                return;
            }

            Process process = new Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.Arguments = $"-NoExit -ExecutionPolicy Bypass -File \"{tempScriptPath}\"";
            process.StartInfo.UseShellExecute = true;
            process.StartInfo.WorkingDirectory = projectRoot;
            process.StartInfo.Verb = ""; // Don't require admin

            try
            {
                process.Start();
                Debug.Log("STYLY NetSync: Starting Python server in PowerShell...");

                // Schedule cleanup of temporary script file
                ScheduleTempFileCleanup(tempScriptPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start PowerShell: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Failed to start PowerShell: {e.Message}", "OK");
            }
        }

        private static void StartServerLinux()
        {
            string serverVersion = GetServerVersionSafe();

            // Try to find available terminal emulator
            string[] terminals = { "gnome-terminal", "konsole", "xterm", "x-terminal-emulator" };
            string availableTerminal = null;

            foreach (var term in terminals)
            {
                Process checkTerm = new Process();
                checkTerm.StartInfo.FileName = "which";
                checkTerm.StartInfo.Arguments = term;
                checkTerm.StartInfo.UseShellExecute = false;
                checkTerm.StartInfo.RedirectStandardOutput = true;
                checkTerm.StartInfo.CreateNoWindow = true;

                try
                {
                    checkTerm.Start();
                    checkTerm.WaitForExit();
                    if (checkTerm.ExitCode == 0)
                    {
                        availableTerminal = term;
                        break;
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(availableTerminal))
            {
                EditorUtility.DisplayDialog("Error",
                    "No supported terminal emulator found. Please install gnome-terminal, konsole, or xterm.", "OK");
                return;
            }

            string shellScript = @"#!/bin/bash
clear
echo 'STYLY NetSync Python Server Setup'
echo '=================================='
echo ''

# Check if uv exists
if ! command -v uv &> /dev/null; then
    echo 'uv is not installed on your system.'
    echo ''
    echo 'Would you like to install uv using the official installer?'
    echo 'This will run: curl -LsSf https://astral.sh/uv/install.sh | sh'
    echo ''
    read -p 'Install uv? (y/n): ' -n 1 -r
    echo ''
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        echo 'Installing uv with the official installer...'
        curl -LsSf https://astral.sh/uv/install.sh | sh
        if [ $? -eq 0 ]; then
            echo 'uv installed successfully!'
            # Add uv to PATH for current session
            export PATH=""$HOME/.local/bin:$PATH""
        else
            echo 'Failed to install uv. Please install it manually.'
            read -p 'Press any key to exit...'
            exit 1
        fi
    else
        echo 'Installation cancelled.'
        read -p 'Press any key to exit...'
        exit 1
    fi
fi

echo ''
echo 'Starting STYLY NetSync Python Server...'
echo 'Server version: " + serverVersion + @"'
echo ''
echo 'Running: uvx styly-netsync-server@" + serverVersion + @"'
echo ''
echo '========================================='
echo ''

# Start the server
uvx styly-netsync-server@" + serverVersion + @"

# Keep terminal open if server exits
echo ''
echo 'Server stopped.'
read -p 'Press any key to exit...'
";

            // Create a unique temp file with .sh extension
            string tempScriptPath = Path.Combine(Path.GetTempPath(), $"start_styly_netsync_server_{Guid.NewGuid():N}.sh");
            File.WriteAllText(tempScriptPath, shellScript);

            // Make script executable
            try
            {
                Process chmod = new Process();
                chmod.StartInfo.FileName = "/bin/chmod";
                chmod.StartInfo.Arguments = $"+x \"{tempScriptPath}\"";
                chmod.StartInfo.UseShellExecute = false;
                chmod.StartInfo.CreateNoWindow = true;
                chmod.Start();
                chmod.WaitForExit();
                if (chmod.ExitCode != 0)
                {
                    Debug.LogError($"Failed to make script executable. chmod exit code: {chmod.ExitCode}");
                    EditorUtility.DisplayDialog("Error",
                        $"Failed to make script executable. chmod exit code: {chmod.ExitCode}", "OK");
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Exception while making script executable: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Exception while making script executable: {e.Message}", "OK");
                return;
            }

            string projectRoot = GetProjectRoot();

            // Execute script in terminal
            Process process = new Process();

            if (availableTerminal == "gnome-terminal")
            {
                process.StartInfo.FileName = availableTerminal;
                process.StartInfo.Arguments = $"-- bash {tempScriptPath}";
            }
            else if (availableTerminal == "konsole")
            {
                process.StartInfo.FileName = availableTerminal;
                process.StartInfo.Arguments = $"-e bash {tempScriptPath}";
            }
            else // xterm or x-terminal-emulator
            {
                process.StartInfo.FileName = availableTerminal;
                process.StartInfo.Arguments = $"-e bash {tempScriptPath}";
            }

            process.StartInfo.UseShellExecute = false;
            process.StartInfo.WorkingDirectory = projectRoot;

            try
            {
                process.Start();
                Debug.Log($"STYLY NetSync: Starting Python server in {availableTerminal}...");

                // Schedule cleanup of temporary script file
                ScheduleTempFileCleanup(tempScriptPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start terminal: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Failed to start {availableTerminal}: {e.Message}", "OK");
            }
        }
    }
}