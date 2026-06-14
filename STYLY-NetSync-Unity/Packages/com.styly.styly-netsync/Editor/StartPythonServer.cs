using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace Styly.NetSync.Editor
{
    /// <summary>
    /// Configuration for launching the NetSync Python server.
    /// </summary>
    internal class ServerLaunchConfig
    {
        // Port settings
        public int ControlPort = 5555;
        public int DealerPort = 5555;
        public int TransformPort = 5557;
        public int PubPort = 5556;
        public int ServerDiscoveryPort = StartPythonServer.DefaultServerDiscoveryPort;
        public int RestApiPort = 8800;
        public bool DisableServerDiscovery = false;
        public string ConfigFile = "";

        // Logging settings
        public string LogDir = "";
        public string LogRotation = "";
        public string LogRetention = "";
        public bool LogJsonConsole = false;
        public string LogLevelConsole = "";

        /// <summary>
        /// Build the uvx command string for display (double-quoted).
        /// </summary>
        public string BuildCommand(string serverVersion)
        {
            return BuildCommand(serverVersion, v => $"\"{v}\"");
        }

        /// <summary>
        /// Build the uvx command string with a custom quoting function for shell-safe execution.
        /// </summary>
        public string BuildCommand(string serverVersion, Func<string, string> quoteValue)
        {
            var sb = new StringBuilder();
            sb.Append($"uvx --exclude-newer {quoteValue("5 days")} --exclude-newer-package {quoteValue("styly-netsync-server=2999-12-31")} styly-netsync-server@{serverVersion}");

            if (!string.IsNullOrEmpty(ConfigFile))
                sb.Append($" --config {quoteValue(ConfigFile)}");

            int resolvedControlPort = ControlPort != 5555 ? ControlPort : DealerPort;
            if (resolvedControlPort != 5555)
                sb.Append($" --control-port {resolvedControlPort}");

            if (TransformPort != 5557)
                sb.Append($" --transform-port {TransformPort}");

            if (PubPort != 5556)
                sb.Append($" --pub-port {PubPort}");

            if (DisableServerDiscovery)
            {
                sb.Append(" --no-server-discovery");
            }
            else if (ServerDiscoveryPort != StartPythonServer.DefaultServerDiscoveryPort)
            {
                sb.Append($" --server-discovery-port {ServerDiscoveryPort}");
            }

            if (RestApiPort != 8800)
                sb.Append($" --rest-api-port {RestApiPort}");

            if (!string.IsNullOrEmpty(LogDir))
                sb.Append($" --log-dir {quoteValue(LogDir)}");

            if (!string.IsNullOrEmpty(LogRotation))
                sb.Append($" --log-rotation {quoteValue(LogRotation)}");

            if (!string.IsNullOrEmpty(LogRetention))
                sb.Append($" --log-retention {quoteValue(LogRetention)}");

            if (LogJsonConsole)
                sb.Append(" --log-json-console");

            if (!string.IsNullOrEmpty(LogLevelConsole))
                sb.Append($" --log-level-console {quoteValue(LogLevelConsole)}");

            return sb.ToString();
        }
    }

    internal static class StartPythonServer
    {
        internal const int DefaultServerDiscoveryPort = 9999;

        internal static bool TryGetServerDiscoveryPortFromScene(out int port)
        {
            // Try to find NetSyncManager in the active scene first, then other loaded scenes
            Scene activeScene = SceneManager.GetActiveScene();
            var scenesToSearch = new List<Scene> { activeScene };
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s != activeScene)
                    scenesToSearch.Add(s);
            }

            foreach (Scene scene in scenesToSearch)
            {
                if (!scene.isLoaded)
                    continue;

                GameObject[] rootObjects = scene.GetRootGameObjects();
                foreach (GameObject rootObject in rootObjects)
                {
                    NetSyncManager manager = rootObject.GetComponentInChildren<NetSyncManager>(true);
                    if (manager != null)
                    {
                        port = manager.ServerDiscoveryPort;
                        return true;
                    }
                }
            }

            port = DefaultServerDiscoveryPort;
            return false;
        }

        internal static int GetDefaultServerDiscoveryPort()
        {
            return TryGetServerDiscoveryPortFromScene(out int port) ? port : DefaultServerDiscoveryPort;
        }

        internal static string GetServerVersionSafe()
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

        private static string EscapeForAppleScript(string text)
        {
            return text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string QuoteForShell(string value)
        {
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        private static string EscapeForBashSingleQuote(string value)
        {
            return value.Replace("'", "'\\''");
        }

        private static string EscapeForPowerShellSingleQuote(string value)
        {
            return value.Replace("'", "''");
        }

        private static string QuoteForPowerShell(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        private static void RunInTerminal(string command)
        {
            string escaped = EscapeForAppleScript(command);

            // Use AppleScript to open Terminal and run the command, then bring Terminal to front
            string appleScript = $"-e \"tell application \\\"Terminal\\\"\" " +
                                 $"-e \"do script \\\"{escaped}\\\"\" " +
                                 $"-e \"activate\" " +
                                 $"-e \"end tell\"";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = appleScript,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start Terminal with osascript: {ex.Message}\n" +
                               "Please ensure that 'osascript' and the Terminal application are available on your system.");
                throw;
            }
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

        // Temp scripts are kept on disk so the user can stop and restart the server from
        // the same terminal. Sweep ones older than this on editor load / domain reload.
        private const double TempScriptMaxAgeHours = 24.0;
        private const string TempScriptPrefix = "start_styly_netsync_server_";

        [InitializeOnLoadMethod]
        private static void SweepStaleTempScripts()
        {
            try
            {
                string tempPath = Path.GetTempPath();
                if (!Directory.Exists(tempPath)) return;

                DateTime cutoff = DateTime.UtcNow.AddHours(-TempScriptMaxAgeHours);
                string[] patterns = { TempScriptPrefix + "*.sh", TempScriptPrefix + "*.ps1" };

                foreach (string pattern in patterns)
                {
                    foreach (string file in Directory.GetFiles(tempPath, pattern))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(file) < cutoff)
                            {
                                File.Delete(file);
                            }
                        }
                        catch
                        {
                            // Ignore individual failures (file in use, permissions, etc.)
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to sweep stale NetSync server scripts: {e.Message}");
            }
        }

        /// <summary>
        /// Launch the server with the given configuration.
        /// </summary>
        internal static void LaunchServer(ServerLaunchConfig config)
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                StartServerMac(config);
            }
            else if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                StartServerWindows(config);
            }
            else if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                StartServerLinux(config);
            }
            else
            {
                EditorUtility.DisplayDialog("Unsupported Platform",
                    "Starting Python server is only supported on Windows, macOS, and Linux.", "OK");
            }
        }

        private static void StartServerMac(ServerLaunchConfig config)
        {
            string serverVersion = GetServerVersionSafe();
            string uvxCommand = config.BuildCommand(serverVersion, QuoteForShell);

            string shellScript = @"#!/bin/bash
clear
echo 'STYLY NetSync Python Server'
echo '============================'
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

# Check uv version (--exclude-newer-package requires uv >= 0.9.2)
UV_VERSION=$(uv --version 2>/dev/null | sed 's/uv //')
UV_MAJOR=$(echo ""$UV_VERSION"" | cut -d. -f1)
UV_MINOR=$(echo ""$UV_VERSION"" | cut -d. -f2)
UV_PATCH=$(echo ""$UV_VERSION"" | cut -d. -f3 | sed 's/[^0-9].*//')
UV_MIN_MAJOR=0
UV_MIN_MINOR=9
UV_MIN_PATCH=2

UV_TOO_OLD=0
if [ ""$UV_MAJOR"" -lt ""$UV_MIN_MAJOR"" ] 2>/dev/null; then
    UV_TOO_OLD=1
elif [ ""$UV_MAJOR"" -eq ""$UV_MIN_MAJOR"" ] 2>/dev/null; then
    if [ ""$UV_MINOR"" -lt ""$UV_MIN_MINOR"" ] 2>/dev/null; then
        UV_TOO_OLD=1
    elif [ ""$UV_MINOR"" -eq ""$UV_MIN_MINOR"" ] 2>/dev/null; then
        if [ ""$UV_PATCH"" -lt ""$UV_MIN_PATCH"" ] 2>/dev/null; then
            UV_TOO_OLD=1
        fi
    fi
fi

if [ ""$UV_TOO_OLD"" -eq 1 ]; then
    echo ""Your uv version ($UV_VERSION) is too old. Version 0.9.2 or later is required.""
    echo ''
    echo 'Please update uv by running:'
    echo '  uv self update'
    echo ''
    read -p 'Press any key to exit...'
    exit 1
fi

echo ''
echo 'Running: " + EscapeForBashSingleQuote(uvxCommand) + @"'
echo ''
echo '========================================='
echo ''

# Resolve the package. Try online first (downloads if needed); only if that fails
# (e.g. no network) retry from uv's offline cache. The '--version' probe exits
# immediately without starting the server, so this never restarts a running server.
if ! " + uvxCommand + @" --version >/dev/null 2>&1; then
    if UV_OFFLINE=1 " + uvxCommand + @" --version >/dev/null 2>&1; then
        echo 'Could not reach PyPI. Using cached packages (offline mode).'
        echo ''
        export UV_OFFLINE=1
    fi
fi

# Start the server
" + uvxCommand + @"

# Keep terminal open if server exits
echo ''
echo 'Server stopped.'
read -p 'Press any key to exit...'
";

            // Create a unique temp file with .sh extension
            string tempScriptPath = Path.Combine(Path.GetTempPath(), $"{TempScriptPrefix}{Guid.NewGuid():N}.sh");
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

            // Execute script in Terminal using AppleScript
            try
            {
                RunInTerminal($"/bin/bash {QuoteForShell(tempScriptPath)}");
                Debug.Log("STYLY NetSync: Starting Python server in Terminal...");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start Terminal: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Failed to start Terminal: {e.Message}", "OK");
            }
        }

        private static void StartServerWindows(ServerLaunchConfig config)
        {
            string serverVersion = GetServerVersionSafe();
            string uvxCommand = config.BuildCommand(serverVersion, QuoteForPowerShell);

            string powershellScript = @"
Clear-Host
Write-Host 'STYLY NetSync Python Server' -ForegroundColor Cyan
Write-Host '============================' -ForegroundColor Cyan
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

# Check uv version (--exclude-newer-package requires uv >= 0.9.2)
$uvVersionStr = (uv --version 2>$null) -replace 'uv ', ''
$uvParts = $uvVersionStr -split '\.'
$uvMajor = [int]$uvParts[0]
$uvMinor = [int]$uvParts[1]
$uvPatch = [int]($uvParts[2] -replace '[^0-9].*','')

$uvTooOld = $false
if ($uvMajor -lt 0) { $uvTooOld = $true }
elseif ($uvMajor -eq 0) {
    if ($uvMinor -lt 9) { $uvTooOld = $true }
    elseif ($uvMinor -eq 9 -and $uvPatch -lt 2) { $uvTooOld = $true }
}

if ($uvTooOld) {
    Write-Host ""Your uv version ($uvVersionStr) is too old. Version 0.9.2 or later is required."" -ForegroundColor Red
    Write-Host ''
    Write-Host 'Please update uv by running:' -ForegroundColor Yellow
    Write-Host '  uv self update' -ForegroundColor White
    Write-Host ''
    Read-Host 'Press Enter to exit'
    exit 1
}

Write-Host ''
Write-Host 'Running: " + EscapeForPowerShellSingleQuote(uvxCommand) + @"' -ForegroundColor Cyan
Write-Host ''
Write-Host '=========================================' -ForegroundColor Cyan
Write-Host ''

# Resolve the package. Try online first (downloads if needed); only if that fails
# (e.g. no network) retry from uv's offline cache. The '--version' probe exits
# immediately without starting the server, so this never restarts a running server.
Invoke-Expression '" + EscapeForPowerShellSingleQuote(uvxCommand) + @" --version' *> $null
if ($LASTEXITCODE -ne 0) {
    $env:UV_OFFLINE = '1'
    Invoke-Expression '" + EscapeForPowerShellSingleQuote(uvxCommand) + @" --version' *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'Could not reach PyPI. Using cached packages (offline mode).' -ForegroundColor Yellow
        Write-Host ''
    } else {
        Remove-Item Env:\UV_OFFLINE -ErrorAction SilentlyContinue
    }
}

# Start the server
" + uvxCommand + @"

# Keep terminal open if server exits
Write-Host ''
Write-Host 'Server stopped.' -ForegroundColor Yellow
Read-Host 'Press Enter to exit'
";

            // Create a unique temp file with .ps1 extension. The shared prefix lets the
            // startup sweep recognize and clean up stale scripts from previous sessions.
            string tempScriptPath = Path.Combine(Path.GetTempPath(), $"{TempScriptPrefix}{Guid.NewGuid():N}.ps1");
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
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start PowerShell: {e.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Failed to start PowerShell: {e.Message}", "OK");
            }
        }

        private static void StartServerLinux(ServerLaunchConfig config)
        {
            string serverVersion = GetServerVersionSafe();
            string uvxCommand = config.BuildCommand(serverVersion, QuoteForShell);

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
echo 'STYLY NetSync Python Server'
echo '============================'
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

# Check uv version (--exclude-newer-package requires uv >= 0.9.2)
UV_VERSION=$(uv --version 2>/dev/null | sed 's/uv //')
UV_MAJOR=$(echo ""$UV_VERSION"" | cut -d. -f1)
UV_MINOR=$(echo ""$UV_VERSION"" | cut -d. -f2)
UV_PATCH=$(echo ""$UV_VERSION"" | cut -d. -f3 | sed 's/[^0-9].*//')
UV_MIN_MAJOR=0
UV_MIN_MINOR=9
UV_MIN_PATCH=2

UV_TOO_OLD=0
if [ ""$UV_MAJOR"" -lt ""$UV_MIN_MAJOR"" ] 2>/dev/null; then
    UV_TOO_OLD=1
elif [ ""$UV_MAJOR"" -eq ""$UV_MIN_MAJOR"" ] 2>/dev/null; then
    if [ ""$UV_MINOR"" -lt ""$UV_MIN_MINOR"" ] 2>/dev/null; then
        UV_TOO_OLD=1
    elif [ ""$UV_MINOR"" -eq ""$UV_MIN_MINOR"" ] 2>/dev/null; then
        if [ ""$UV_PATCH"" -lt ""$UV_MIN_PATCH"" ] 2>/dev/null; then
            UV_TOO_OLD=1
        fi
    fi
fi

if [ ""$UV_TOO_OLD"" -eq 1 ]; then
    echo ""Your uv version ($UV_VERSION) is too old. Version 0.9.2 or later is required.""
    echo ''
    echo 'Please update uv by running:'
    echo '  uv self update'
    echo ''
    read -p 'Press any key to exit...'
    exit 1
fi

echo ''
echo 'Running: " + EscapeForBashSingleQuote(uvxCommand) + @"'
echo ''
echo '========================================='
echo ''

# Resolve the package. Try online first (downloads if needed); only if that fails
# (e.g. no network) retry from uv's offline cache. The '--version' probe exits
# immediately without starting the server, so this never restarts a running server.
if ! " + uvxCommand + @" --version >/dev/null 2>&1; then
    if UV_OFFLINE=1 " + uvxCommand + @" --version >/dev/null 2>&1; then
        echo 'Could not reach PyPI. Using cached packages (offline mode).'
        echo ''
        export UV_OFFLINE=1
    fi
fi

# Start the server
" + uvxCommand + @"

# Keep terminal open if server exits
echo ''
echo 'Server stopped.'
read -p 'Press any key to exit...'
";

            // Create a unique temp file with .sh extension
            string tempScriptPath = Path.Combine(Path.GetTempPath(), $"{TempScriptPrefix}{Guid.NewGuid():N}.sh");
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
