# Builds the UMDF driver package into driver\out\pkg. It is left unsigned;
# install.ps1 signs it on the machine it gets installed on.
# Uses an existing MSVC install if present, or automatically downloads only the
# official Microsoft portable MSVC + Windows SDK + WDK files needed for x64
# UMDF compilation (no Visual Studio installation and no third-party tools).
$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072 } catch {}
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path $PSScriptRoot -Parent
$wdk = Join-Path $root '.wdk\wdk\c'
$toolchain = Join-Path $root '.toolchain'
$sdkVersion = '10.0.26100.0'
$sdkPackageVersion = '10.0.26100.6584'
$umdfVersion = '2.31'

function Get-Prop($Obj, [string]$Name) {
    if ($null -eq $Obj) { return $null }
    if ($Obj -is [System.Collections.IDictionary]) {
        if ($Obj.ContainsKey($Name)) { return $Obj[$Name] }
        return $null
    }
    $prop = $Obj.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $null
}

function Download-Text([string]$Url) {
    $wc = New-Object System.Net.WebClient
    $wc.Encoding = [System.Text.Encoding]::UTF8
    try { return $wc.DownloadString($Url) } finally { $wc.Dispose() }
}

function Download-File([string]$Url, [string]$DestPath) {
    $wc = New-Object System.Net.WebClient
    try { $wc.DownloadFile($Url, $DestPath) } finally { $wc.Dispose() }
}

function Expand-ZipSubtree(
    [string]$ZipPath,
    [string]$EntryPrefix,
    [string]$DestRoot,
    [string[]]$IncludePrefixes = @(),
    [string[]]$ExcludeSubstrings = @()
) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $zip.Entries) {
            $norm = $entry.FullName.Replace('\', '/')
            if ($norm.EndsWith('/') -or -not $norm.StartsWith($EntryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            if ($IncludePrefixes.Count -gt 0) {
                $matched = $false
                foreach ($inc in $IncludePrefixes) {
                    if ($norm.StartsWith($inc, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $matched = $true
                        break
                    }
                }
                if (-not $matched) { continue }
            }
            $skip = $false
            foreach ($ex in $ExcludeSubstrings) {
                if ($norm.IndexOf($ex, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $skip = $true
                    break
                }
            }
            if ($skip) { continue }
            $rel = $norm.Substring($EntryPrefix.Length).TrimStart('/')
            if (-not $rel) { continue }
            $target = Join-Path $DestRoot ($rel.Replace('/', [IO.Path]::DirectorySeparatorChar))
            $dir = Split-Path $target -Parent
            if (-not (Test-Path $dir)) {
                New-Item -ItemType Directory -Force $dir | Out-Null
            }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally {
        $zip.Dispose()
    }
}

function Find-VsWhere {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\Installer\vswhere.exe"
    )
    if ($env:ChocolateyInstall) {
        $candidates += Join-Path $env:ChocolateyInstall 'bin\vswhere.exe'
    }
    $cmd = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }

    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    return $null
}

function Find-VcVars64 {
    $vswhere = Find-VsWhere
    if ($vswhere) {
        $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($vs) {
            $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path $vcvars) { return $vcvars }
        }
    }

    $patterns = @()
    if (${env:ProgramFiles}) {
        $patterns += "${env:ProgramFiles}\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat"
    }
    if (${env:ProgramFiles(x86)}) {
        $patterns += "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat"
    }
    if ($env:VSINSTALLDIR) {
        $patterns += (Join-Path $env:VSINSTALLDIR 'VC\Auxiliary\Build\vcvars64.bat')
    }
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem)) {
        $patterns += "$($drive.Root)Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat"
        $patterns += "$($drive.Root)BuildTools\VC\Auxiliary\Build\vcvars64.bat"
    }

    foreach ($pattern in $patterns) {
        $match = Get-Item $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($match) { return $match.FullName }
    }
    return $null
}

function Use-PortableToolchain {
    $ready = Join-Path $toolchain '.ready'
    $msvcDir = Join-Path $toolchain 'msvc'
    $sdkDir = Join-Path $toolchain 'sdk'

    if (-not (Test-Path $ready)) {
        Write-Host 'Visual Studio not found - downloading official Microsoft portable MSVC + Windows SDK into .toolchain...'
        Remove-Item $toolchain -Recurse -Force -ErrorAction SilentlyContinue
        $tmp = Join-Path $toolchain 'tmp'
        New-Item -ItemType Directory -Force $tmp, $msvcDir, $sdkDir | Out-Null

        Write-Host 'Fetching official Microsoft C++ manifest...'
        $channel = (Download-Text 'https://aka.ms/vs/17/release/channel') | ConvertFrom-Json
        $manifestItem = $channel.channelItems | Where-Object { $_.type -eq 'Manifest' } | Select-Object -First 1
        $vsmanUrl = $manifestItem.payloads[0].url
        $vsmanJson = Download-Text $vsmanUrl

        if ($PSVersionTable.PSVersion.Major -ge 6) {
            $vsman = $vsmanJson | ConvertFrom-Json
            $packages = $vsman.packages
        } else {
            Add-Type -AssemblyName System.Web.Extensions
            $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
            $ser.MaxJsonLength = [int]::MaxValue
            $vsman = $ser.DeserializeObject($vsmanJson)
            $packages = $vsman['packages']
        }

        $bestPrefix = $null
        $bestVer = [version]'0.0.0.0'
        foreach ($pkgItem in $packages) {
            $pkgId = [string](Get-Prop $pkgItem 'id')
            if ($pkgId -match '^(Microsoft\.VC\.(14\.\d+\.\d+\.\d+))\.Tools\.HostX64\.TargetX64\.base$') {
                $ver = [version]$Matches[2]
                if ($ver -gt $bestVer) {
                    $bestVer = $ver
                    $bestPrefix = $Matches[1]
                }
            }
        }
        if (-not $bestPrefix) {
            throw 'Could not locate portable MSVC toolchain in Microsoft manifest.'
        }

        Write-Host "Downloading official Microsoft MSVC ($bestPrefix, ~77 MB)..."
        $vsixTargets = @(
            @{ Id = "$bestPrefix.Tools.HostX64.TargetX64.base"; Lang = $null },
            @{ Id = "$bestPrefix.Tools.HostX64.TargetX64.Res.base"; Lang = 'en-US' },
            @{ Id = "$bestPrefix.CRT.Headers.base"; Lang = $null },
            @{ Id = "$bestPrefix.CRT.x64.Desktop.base"; Lang = $null }
        )
        foreach ($targetItem in $vsixTargets) {
            $pkgFound = $null
            foreach ($pkgItem in $packages) {
                if ([string](Get-Prop $pkgItem 'id') -ne $targetItem.Id) { continue }
                if ($targetItem.Lang -and ([string](Get-Prop $pkgItem 'language') -ine $targetItem.Lang)) { continue }
                $pkgFound = $pkgItem
                break
            }
            if (-not $pkgFound) { throw "Missing manifest package $($targetItem.Id)" }
            $rawPayloads = Get-Prop $pkgFound 'payloads'
            if ($null -eq $rawPayloads) { throw "No payloads in manifest package $($targetItem.Id)" }
            $payloads = @($rawPayloads)
            $payload = $payloads[0]
            if ($null -eq $payload) { throw "Empty payload in manifest package $($targetItem.Id)" }
            $url = [string](Get-Prop $payload 'url')
            $fileName = [string](Get-Prop $payload 'fileName')
            if (-not $url -or -not $fileName) { throw "Invalid payload metadata in $($targetItem.Id)" }
            $file = Join-Path $tmp (Split-Path $fileName -Leaf)
            Download-File $url $file
            Expand-ZipSubtree $file 'Contents/' $msvcDir @() @('/lib/x64/enclave/', '/lib/x64/onecore/', '/lib/x64/store/')
            Remove-Item $file -Force
        }

        Write-Host "Downloading official Microsoft Windows SDK ($sdkPackageVersion, ~200 MB)..."
        foreach ($sdkPkg in @('microsoft.windows.sdk.cpp', 'microsoft.windows.sdk.cpp.x64')) {
            $nupkgUrl = "https://api.nuget.org/v3-flatcontainer/$sdkPkg/$sdkPackageVersion/$sdkPkg.$sdkPackageVersion.nupkg"
            $nupkgFile = Join-Path $tmp "$sdkPkg.zip"
            Download-File $nupkgUrl $nupkgFile
            Expand-ZipSubtree $nupkgFile 'c/' $sdkDir @() @("c/Include/$sdkVersion/cppwinrt/", 'c/ucrt_enclave/')
            Remove-Item $nupkgFile -Force
        }

        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType File -Path $ready -Force | Out-Null
    }

    $vcTools = Get-ChildItem (Join-Path $msvcDir 'VC\Tools\MSVC') -Directory | Sort-Object Name | Select-Object -Last 1
    if (-not $vcTools) {
        throw "Portable MSVC toolchain looks incomplete in $msvcDir. Delete .toolchain and retry."
    }
    $vcBin = Join-Path $vcTools.FullName 'bin\Hostx64\x64'
    $vcInc = Join-Path $vcTools.FullName 'include'
    $vcLib = Join-Path $vcTools.FullName 'lib\x64'
    $sdkInc = Join-Path $sdkDir "Include\$sdkVersion"

    $env:PATH = "$vcBin;$env:PATH"
    $env:INCLUDE = "$vcInc;$sdkInc\ucrt;$sdkInc\um;$sdkInc\shared;$sdkInc\winrt"
    $env:LIB = "$vcLib;$sdkDir\ucrt\x64;$sdkDir\um\x64"
}

$wdkReady = (Test-Path "$wdk\Include\$sdkVersion\km\hidport.h") -and
            (Test-Path "$wdk\Lib\wdf\umdf\x64\$umdfVersion\WdfDriverStubUm.lib") -and
            (Test-Path "$wdk\bin\$sdkVersion\x64\stampinf.exe")
if (-not $wdkReady) {
    Write-Host "Downloading official Microsoft Windows WDK ($sdkPackageVersion, ~106 MB)..."
    Remove-Item (Join-Path $root '.wdk') -Recurse -Force -ErrorAction SilentlyContinue
    $wdkPackage = 'microsoft.windows.wdk.x64'
    $archive = Join-Path $root '.wdk\wdk.zip'
    New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
    Download-File "https://api.nuget.org/v3-flatcontainer/$wdkPackage/$sdkPackageVersion/$wdkPackage.$sdkPackageVersion.nupkg" $archive
    Expand-ZipSubtree $archive 'c/' $wdk @(
        "c/Include/$sdkVersion/km/hidport.h",
        "c/Include/wdf/umdf/$umdfVersion/",
        "c/Lib/wdf/umdf/x64/$umdfVersion/",
        "c/bin/$sdkVersion/x64/"
    )
    Remove-Item $archive -Force
}

$out = Join-Path $PSScriptRoot 'out'
$obj = Join-Path $out 'obj'
$pkg = Join-Path $out 'pkg'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $obj, $pkg | Out-Null

# Import an existing MSVC x64 environment if installed; otherwise use a portable toolchain without Visual Studio.
$hasX64Cl = (Get-Command cl.exe -ErrorAction SilentlyContinue) -and ($env:VSCMD_ARG_TGT_ARCH -eq 'x64')
if (-not $hasX64Cl) {
    $vcvars = Find-VcVars64
    if ($vcvars) {
        cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
            if ($_ -match '^([^=]+)=(.*)$') { Set-Item "env:$($Matches[1])" $Matches[2] }
        }
        if ($LASTEXITCODE -or -not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
            throw "Failed to initialize MSVC x64 environment from $vcvars"
        }
    } else {
        Use-PortableToolchain
        if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
            throw 'Portable cl.exe initialization failed.'
        }
    }
}

# hidport.h lives among the kernel headers; stage it alone so that the rest of
# the km directory cannot shadow user-mode SDK headers.
Copy-Item "$wdk\Include\$sdkVersion\km\hidport.h" $obj

$minor = $umdfVersion.Split('.')[1]
$sources = 'winstadia.c', 'stadia.c', 'usb.c', 'bluetooth.c' | ForEach-Object { "$PSScriptRoot\$_" }
& cl /nologo /W4 /O2 /MT /EHsc /std:c++17 /c /D UNICODE /D _UNICODE /D _WIN32_WINNT=0x0A00 /Fo"$obj\private-gatt.obj" "$PSScriptRoot\private-gatt.cpp"
if ($LASTEXITCODE) { throw 'private GATT probe compiling failed' }
& cl /nologo /W4 /external:anglebrackets /external:W0 /O2 /MT /LD /std:c17 `
    /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=$minor /D UMDF_USING_NTSTATUS `
    /D UNICODE /D _UNICODE /D _WIN32_WINNT=0x0A00 `
    /I "$wdk\Include\wdf\umdf\$umdfVersion" /I $obj `
    /Fo"$obj\\" /Fe"$pkg\winstadia.dll" $sources `
    /link /NOIMPLIB /NOEXP /NODEFAULTLIB:oldnames.lib "$obj\private-gatt.obj" "$wdk\Lib\wdf\umdf\x64\$umdfVersion\WdfDriverStubUm.lib" ntdll.lib runtimeobject.lib ole32.lib
if ($LASTEXITCODE) { throw 'compiling failed' }
Copy-Item "$PSScriptRoot\winstadia.inf" $pkg
# Windows only replaces an installed package when DriverVer is newer, so every
# build gets the current date and a time-based version.
& "$wdk\bin\$sdkVersion\x64\stampinf.exe" -f "$pkg\winstadia.inf" -d * -v * | Out-Null
if ($LASTEXITCODE) { throw 'stampinf failed' }

Write-Host "Driver package ready: $pkg"
