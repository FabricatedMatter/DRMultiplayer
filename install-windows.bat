@echo off
rem Double-click me! Runs the PowerShell installer next to this file and keeps
rem the window open so you can read the output.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-windows.ps1" %*
echo.
pause
