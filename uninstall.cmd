@echo off
rem Double-click to remove CH397Bridge. Asks for administrator rights, then runs uninstall.ps1.
net session >nul 2>&1 || (
    powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo.
pause
