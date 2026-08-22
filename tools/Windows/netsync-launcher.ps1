#Requires -Version 5.1
<#
.SYNOPSIS
    Bootstraps and starts the STYLY NetSync Launcher GUI without a console.

.DESCRIPTION
    Double-click "STYLY NetSync Launcher.vbs" rather than running this file
    directly - the .vbs starts PowerShell with no window at all.

    This script:
      1. Finds the 'uv' runtime, offering a one-click user-level install if it
         is missing (no administrator rights, nothing added to PATH by us).
      2. Prepares the STYLY NetSync environment behind a progress window,
         falling back to uv's offline cache when the venue has no internet.
      3. Starts the launcher GUI hidden, so no terminal is ever shown.

    Every failure is reported in a dialog box, never on a console.

.PARAMETER Version
    Package version to run. Defaults to the version in the repository this
    script sits in, then to the pinned fallback, then to 'latest'.

.PARAMETER FromPyPI
    Ignore the local repository checkout and always resolve from PyPI.

.PARAMETER Autostart
    Start the NetSync server as soon as the GUI opens.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$FromPyPI,
    [switch]$Autostart
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$AppName = 'STYLY NetSync Launcher'
$ScriptDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $ScriptDir)

# ---------------------------------------------------------------------------
# Dialog helpers - this script must never print to a console
# ---------------------------------------------------------------------------

function Show-Message {
    param([string]$Text, [string]$Icon = 'Information')
    [void][System.Windows.Forms.MessageBox]::Show($Text, $AppName, 'OK', $Icon)
}

function Confirm-Action {
    param([string]$Text)
    $answer = [System.Windows.Forms.MessageBox]::Show($Text, $AppName, 'OKCancel', 'Question')
    return ($answer -eq [System.Windows.Forms.DialogResult]::OK)
}

function New-Splash {
    param([string]$Text)
    $form = New-Object System.Windows.Forms.Form
    $form.Text = $AppName
    $form.ClientSize = New-Object System.Drawing.Size(430, 110)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MinimizeBox = $false
    $form.MaximizeBox = $false
    $form.ControlBox = $false
    $form.TopMost = $true

    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.SetBounds(18, 18, 394, 40)

    $bar = New-Object System.Windows.Forms.ProgressBar
    $bar.Style = 'Marquee'
    $bar.MarqueeAnimationSpeed = 25
    $bar.SetBounds(18, 66, 394, 18)

    $form.Controls.AddRange(@($label, $bar))
    $form.Show()
    $form.Refresh()
    [System.Windows.Forms.Application]::DoEvents()
    return $form
}

function Start-Hidden {
    # Start a process with no window and return it.
    #
    # Touching .Handle is not optional: Start-Process -PassThru leaves
    # ExitCode null unless the handle has been cached, which would make every
    # run look like a failure.
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$StandardOutput,
        [string]$StandardError
    )
    $parameters = @{
        FilePath     = $FilePath
        ArgumentList = $ArgumentList
        WindowStyle  = 'Hidden'
        PassThru     = $true
    }
    if ($StandardOutput) { $parameters['RedirectStandardOutput'] = $StandardOutput }
    if ($StandardError) { $parameters['RedirectStandardError'] = $StandardError }

    $process = Start-Process @parameters
    try { $null = $process.Handle } catch { }
    return $process
}

function Wait-ForProcess {
    # Keeps the splash animating while the child runs.
    param([System.Diagnostics.Process]$Process)
    while (-not $Process.HasExited) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 80
    }
    $Process.WaitForExit()
    $code = $Process.ExitCode
    if ($null -eq $code) {
        # Unknown rather than failed: let the launch itself report any problem.
        return 0
    }
    return $code
}

# ---------------------------------------------------------------------------
# uv runtime
# ---------------------------------------------------------------------------

function Find-Uv {
    $candidates = @()
    $command = Get-Command uv -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    $candidates += (Join-Path $env:USERPROFILE '.local\bin\uv.exe')
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\uv.exe')
    $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\uv\uv.exe')
    $candidates += (Join-Path $env:LOCALAPPDATA 'uv\bin\uv.exe')

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }
    return $null
}

function Install-Uv {
    $consent = Confirm-Action @"
STYLY NetSync needs the 'uv' Python runtime manager, which is not installed yet.

Install it now?

  - Downloads and runs the official installer from https://astral.sh/uv
  - Installs for your user account only (no administrator rights)
  - About 40 MB
"@
    if (-not $consent) { return $null }

    $splash = New-Splash 'Installing the uv runtime. This takes a minute...'
    try {
        $arguments = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-Command', 'irm https://astral.sh/uv/install.ps1 | iex'
        )
        $process = Start-Hidden -FilePath 'powershell.exe' -ArgumentList $arguments
        $exitCode = Wait-ForProcess -Process $process
    } finally {
        $splash.Close()
    }

    if ($exitCode -ne 0) {
        Show-Message "The uv installer exited with code $exitCode.`n`nInstall uv manually from https://docs.astral.sh/uv/getting-started/installation/ and start this launcher again." 'Error'
        return $null
    }
    return (Find-Uv)
}

# ---------------------------------------------------------------------------
# Version and package source
# ---------------------------------------------------------------------------

function Get-PackageVersion {
    if ($Version) { return $Version }

    $packageJson = Join-Path $RepoRoot 'STYLY-NetSync-Unity\Packages\com.styly.styly-netsync\package.json'
    if (Test-Path $packageJson) {
        try {
            $parsed = Get-Content $packageJson -Raw | ConvertFrom-Json
            if ($parsed.version) { return $parsed.version }
        } catch {
            # Fall through to the other sources.
        }
    }

    $override = Join-Path $ScriptDir 'netsync-version.txt'
    if (Test-Path $override) {
        $text = (Get-Content $override -Raw).Trim()
        if ($text) { return $text }
    }

    return 'latest'
}

function Get-PackageSource {
    param([string]$PackageVersion)

    $serverDir = Join-Path $RepoRoot 'STYLY-NetSync-Server'
    $useLocal = (-not $FromPyPI) -and (Test-Path (Join-Path $serverDir 'pyproject.toml'))
    if ($useLocal) {
        return [pscustomobject]@{
            From    = $serverDir
            Label   = 'this repository'
            IsLocal = $true
        }
    }
    return [pscustomobject]@{
        From    = "styly-netsync-server@$PackageVersion"
        Label   = "PyPI ($PackageVersion)"
        IsLocal = $false
    }
}

function Get-UvArguments {
    param([pscustomobject]$Source, [string[]]$Extra)

    $arguments = @('tool', 'run')
    if (-not $Source.IsLocal) {
        # Keep resolution off brand-new third-party releases, while still
        # allowing any version of NetSync itself. Mirrors the Unity launcher.
        $arguments += @(
            '--exclude-newer', '5 days',
            '--exclude-newer-package', 'styly-netsync-server=2999-12-31'
        )
    }
    $arguments += @('--from', $Source.From, 'styly-netsync-launcher')
    if ($Extra) { $arguments += $Extra }
    return $arguments
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$uv = Find-Uv
if (-not $uv) {
    $uv = Install-Uv
    if (-not $uv) { exit 1 }
}

$packageVersion = Get-PackageVersion
$source = Get-PackageSource -PackageVersion $packageVersion
$stdout = [System.IO.Path]::GetTempFileName()
$stderr = [System.IO.Path]::GetTempFileName()

# Warm the environment up first: uv may need to download a Python interpreter
# and the dependencies, which must happen behind the progress window rather
# than as a silent delay before the GUI appears.
$splash = New-Splash "Preparing STYLY NetSync from $($source.Label).`nThe first run downloads Python and dependencies."
try {
    $warmup = Get-UvArguments -Source $source -Extra @('--help')
    $process = Start-Hidden -FilePath $uv -ArgumentList $warmup `
        -StandardOutput $stdout -StandardError $stderr
    $exitCode = Wait-ForProcess -Process $process

    if ($exitCode -ne 0) {
        # No internet is normal in a venue; retry from uv's local cache.
        $env:UV_OFFLINE = '1'
        $process = Start-Hidden -FilePath $uv -ArgumentList $warmup `
            -StandardOutput $stdout -StandardError $stderr
        $offlineExit = Wait-ForProcess -Process $process
        if ($offlineExit -ne 0) {
            Remove-Item Env:\UV_OFFLINE -ErrorAction SilentlyContinue
        }
        $exitCode = $offlineExit
    }
} finally {
    $splash.Close()
}

if ($exitCode -ne 0) {
    $details = ''
    if (Test-Path $stderr) { $details = (Get-Content $stderr -Raw) }
    if (-not $details) { $details = '(no output)' }
    if ($details.Length -gt 1500) { $details = $details.Substring(0, 1500) + '...' }
    Show-Message "Could not prepare STYLY NetSync (exit code $exitCode).`n`n$details" 'Error'
    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
    exit 1
}
Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue

$extra = @()
if ($Autostart) { $extra += '--autostart' }
$launch = Get-UvArguments -Source $source -Extra $extra

try {
    Start-Process -FilePath $uv -ArgumentList $launch -WindowStyle Hidden
} catch {
    Show-Message "Could not start the launcher:`n`n$($_.Exception.Message)" 'Error'
    exit 1
}
