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
        [MenuItem("STYLY NetSync/Start Python Server", false, 100)]
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
            else
            {
                EditorUtility.DisplayDialog("Unsupported Platform",
                    "Starting Python server is only supported on Windows and macOS.", "OK");
            }
        }

        private static void StartServerMac()
        {
            string terminal = "/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal";
            if (!File.Exists(terminal))
            {
                terminal = "/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal";
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

# Navigate to server directory
SERVER_DIR=""$(dirname ""$0"")/../../../../../../STYLY-NetSync-Server""
cd ""$SERVER_DIR""

echo ''
echo 'Starting STYLY NetSync Python Server...'
echo 'Server directory: ' $(pwd)
echo ''
echo 'Running: uvx --from . styly-netsync'
echo ''
echo '========================================='
echo ''

# Start the server
uvx --from . styly-netsync

# Keep terminal open if server exits
echo ''
echo 'Server stopped.'
read -p 'Press any key to exit...'
";

            string tempScriptPath = Path.Combine(Path.GetTempPath(), "start_styly_netsync_server.sh");
            File.WriteAllText(tempScriptPath, shellScript);

            // Make script executable
            Process chmod = new Process();
            chmod.StartInfo.FileName = "/bin/chmod";
            chmod.StartInfo.Arguments = $"+x \"{tempScriptPath}\"";
            chmod.StartInfo.UseShellExecute = false;
            chmod.StartInfo.CreateNoWindow = true;
            chmod.Start();
            chmod.WaitForExit();

            // Get the project root directory (where STYLY-NetSync-Server is located)
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../"));

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

# Navigate to server directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverDir = Join-Path $scriptDir '..\..\..\..\..\..\STYLY-NetSync-Server'
Set-Location $serverDir

Write-Host ''
Write-Host 'Starting STYLY NetSync Python Server...' -ForegroundColor Green
Write-Host ""Server directory: $((Get-Location).Path)"" -ForegroundColor Gray
Write-Host ''
Write-Host 'Running: uvx --from . styly-netsync' -ForegroundColor Cyan
Write-Host ''
Write-Host '=========================================' -ForegroundColor Cyan
Write-Host ''

# Start the server
uvx --from . styly-netsync

# Keep terminal open if server exits
Write-Host ''
Write-Host 'Server stopped.' -ForegroundColor Yellow
Read-Host 'Press Enter to exit'
";

            string tempScriptPath = Path.Combine(Path.GetTempPath(), "start_styly_netsync_server.ps1");
            File.WriteAllText(tempScriptPath, powershellScript);

            // Get the project root directory
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../"));

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
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start PowerShell: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Failed to start PowerShell: {e.Message}", "OK");
            }
        }
    }
}