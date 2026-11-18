# Compiles StadiaRumbleApp.cs into app\publish\StadiaRumble.exe using the
# built-in .NET Framework 4.x C# compiler included with Windows 11 (no SDK needed).
$ErrorActionPreference = 'Stop'

$appDir = $PSScriptRoot
$src = Join-Path $appDir 'StadiaRumbleApp.cs'
$pubDir = Join-Path $appDir 'publish'
$outExe = Join-Path $pubDir 'StadiaRumble.exe'
$hashFile = Join-Path $pubDir 'StadiaRumble.sha256'

if (-not (Test-Path $src)) {
    throw "Missing source file: $src"
}
New-Item -ItemType Directory -Force $pubDir | Out-Null

$candidates = @(
    "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:windir\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $null
foreach ($c in $candidates) {
    if (Test-Path $c) { $csc = $c; break }
}
if (-not $csc) {
    throw 'Built-in Windows .NET Framework compiler (csc.exe v4.0.30319) was not found.'
}

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /utf8output `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /out:"$outExe" "$src"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outExe)) {
    throw "Failed to compile StadiaRumble.exe (exit code $LASTEXITCODE)"
}

$srcHash = (Get-FileHash -Path $src -Algorithm SHA256).Hash
Set-Content -Path $hashFile -Value $srcHash -Encoding ASCII -NoNewline

Write-Host "App ready: $outExe"
