@echo off
setlocal
title Stadia Rumble setup
echo.
echo   Stadia Rumble - setup and diagnostics
echo.
choice /C 123 /N /M "1 - Install or update   2 - Remove   3 - Open StadiaRumble App   [1/2/3]: "
if errorlevel 3 goto openapp
if errorlevel 2 goto remove

echo.
set "HAS_PKG="
set "BUILT_NOW="
if exist "%~dp0driver\pkg\winstadia.dll" set "HAS_PKG=1"
if exist "%~dp0driver\out\pkg\winstadia.dll" set "HAS_PKG=1"
if not defined HAS_PKG (
    set "BUILT_NOW=1"
    echo Building the latest driver...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0driver\build.ps1"
    if errorlevel 1 (
        echo.
        echo BUILD FAILED. Check the messages above.
        pause
        exit /b 1
    )
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\build-app.ps1"
if errorlevel 1 (
    echo.
    echo Warning: StadiaRumble.exe build failed, continuing driver install...
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0driver\install.ps1" -Pause
if errorlevel 1 (
    echo.
    echo INSTALL FAILED. Check driver\install.log or driver\out\install.log.
    pause
    exit /b 1
)
if defined BUILT_NOW (
    if exist "%~dp0.toolchain" rmdir /s /q "%~dp0.toolchain"
    if exist "%~dp0.wdk" rmdir /s /q "%~dp0.wdk"
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

:openapp
call "%~dp0StadiaRumble.bat"
exit /b %errorlevel%
