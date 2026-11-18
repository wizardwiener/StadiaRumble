@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0driver\install.ps1" -Uninstall -Pause
if errorlevel 1 (
    echo.
    echo UNINSTALL FAILED. Check driver\install.log or driver\out\install.log.
    pause
    exit /b 1
)
