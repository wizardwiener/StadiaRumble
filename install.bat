@echo off
setlocal
set "NEED_BUILD="
if not exist "%~dp0driver\pkg\winstadia.dll" if not exist "%~dp0driver\out\pkg\winstadia.dll" set "NEED_BUILD=1"
if not defined NEED_BUILD (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$dll = if (Test-Path '%~dp0driver\out\pkg\winstadia.dll') { Get-Item '%~dp0driver\out\pkg\winstadia.dll' } else { Get-Item '%~dp0driver\pkg\winstadia.dll' }; $newer = Get-ChildItem '%~dp0driver' -Include *.c,*.cpp,*.h -Recurse | Where-Object { $_.LastWriteTimeUtc -gt $dll.LastWriteTimeUtc }; if ($newer) { exit 2 }"
    if errorlevel 2 set "NEED_BUILD=1"
)
if defined NEED_BUILD (
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
if defined NEED_BUILD (
    if exist "%~dp0.toolchain" rmdir /s /q "%~dp0.toolchain"
    if exist "%~dp0.wdk" rmdir /s /q "%~dp0.wdk"
)
