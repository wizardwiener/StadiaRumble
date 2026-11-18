@echo off
rem One click: builds the fixed driver, then installs it.
rem First build downloads WDK + portable C++ toolchain automatically if Visual Studio is not installed.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Check the messages above or inspect driver\build.log.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
    echo.
    echo INSTALL FAILED. Check out\install.log.
    pause
    exit /b 1
)
echo.
if exist "%~dp0install.log" type "%~dp0install.log"
if exist "%~dp0out\install.log" type "%~dp0out\install.log"
echo.
echo Follow the installer message above. Restart Windows if requested before testing.
pause
