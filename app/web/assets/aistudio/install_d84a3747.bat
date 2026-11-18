@echo off
setlocal
if not exist "%~dp0driver\pkg\winstadia.dll" (
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
