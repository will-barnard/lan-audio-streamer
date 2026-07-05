@echo off
REM Double-click in Explorer to START the Windows receiver.
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0run.ps1"
