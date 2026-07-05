@echo off
REM Double-click in Explorer to STOP the Windows receiver.
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0stop.ps1"
timeout /t 2 >nul
