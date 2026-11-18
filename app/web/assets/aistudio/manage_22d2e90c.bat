@echo off
setlocal
title Stadia Rumble setup
echo.
echo   Stadia Rumble - driver setup
echo.
choice /C 12 /N /M "1 - Install or update   2 - Remove   [1/2]: "
if errorlevel 2 goto remove

echo.
if not exist "%~dp0driver\pkg\winstadia.dll" (
    echo Building the latest driver...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0driver\build.ps1"
    if errorlevel 1 (
        echo.
        echo BUILD FAILED. Check the messages above.
        pause
        exit /b 1
    )
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0driver\install.ps1" -Pause
if errorlevel 1 (
    echo.
    echo INSTALL FAILED. Check driver\install.log or driver\out\install.log.
    pause
    exit /b 1
)
exit /b 0

:remove
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0driver\install.ps1" -Uninstall -Pause
if errorlevel 1 (
    echo.
    echo UNINSTALL FAILED. Check driver\install.log or driver\out\install.log.
    pause
    exit /b 1
)
exit /b 0
