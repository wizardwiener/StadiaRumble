@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$exe = Join-Path '%~dp0app\publish' 'StadiaRumble.exe'; $hashFile = Join-Path '%~dp0app\publish' 'StadiaRumble.sha256'; $src = Join-Path '%~dp0app' 'StadiaRumbleApp.cs'; $cur = (Get-FileHash -Path $src -Algorithm SHA256).Hash; $old = if (Test-Path $hashFile) { (Get-Content -Path $hashFile -Raw).Trim() } else { '' }; if (-not (Test-Path $exe) -or ($cur -ne $old)) { & '%~dp0app\build-app.ps1'; if ($LASTEXITCODE) { exit $LASTEXITCODE } }"
if errorlevel 1 (
    echo.
    echo Failed to build StadiaRumble.exe. Check the messages above.
    pause
    exit /b 1
)
start "" "%~dp0app\publish\StadiaRumble.exe"
