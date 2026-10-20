@echo off
net session >nul 2>&1
if %errorlevel% NEQ 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo RunAsAdmin wird entfernt...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
pause
