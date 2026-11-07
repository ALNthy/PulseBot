@echo off
REM Double-click entry point: hand off to the PowerShell script.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-dev.ps1" %*
if errorlevel 1 pause
