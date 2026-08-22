@echo off
REM Fallback launcher for environments where .vbs files are blocked by policy.
REM Prefer "STYLY NetSync Launcher.vbs" - this one flashes a console for a
REM fraction of a second before the GUI takes over.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0netsync-launcher.ps1"
exit /b
