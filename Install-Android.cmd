@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-Android.ps1"
if errorlevel 1 (
    echo Installation did not complete. See the message above.
    pause
    exit /b 1
)
pause
