# Modified by Stadia Rumble Project; derived from aisk/WinStadia under GPL-3.0.
# Installs (or with -Uninstall removes) the driver package, built by build.ps1
# or taken from a release: signs it for this machine and puts the driver on
# Stadia controllers, connected now or later.
# Elevates itself; output goes to install.log next to the package.
param(
    [switch]$Uninstall,
    [switch]$Pause
)

$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Security

# A release has the package next to this script, a checkout has it in the
# build output. If both exist, prefer whichever winstadia.dll is newer.
$releasePkg = Join-Path $PSScriptRoot 'pkg'
$buildPkg = Join-Path $PSScriptRoot 'out\pkg'
$releaseDll = Join-Path $releasePkg 'winstadia.dll'
$buildDll = Join-Path $buildPkg 'winstadia.dll'
if ((Test-Path $buildDll) -and (Test-Path $releaseDll)) {
    $pkg = if ((Get-Item $buildDll).LastWriteTimeUtc -ge (Get-Item $releaseDll).LastWriteTimeUtc) { $buildPkg } else { $releasePkg }
} elseif (Test-Path $buildDll) {
    $pkg = $buildPkg
} elseif (Test-Path $releasePkg) {
    $pkg = $releasePkg
} else {
    $pkg = $buildPkg
}
$inf = Join-Path $pkg 'winstadia.inf'
$cat = Join-Path $pkg 'winstadia.cat'
$log = Join-Path (Split-Path $pkg -Parent) 'install.log'
$certSubject = 'CN=Stadia Rumble driver signing'
$stores = 'Root', 'TrustedPublisher'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = [Security.Principal.WindowsBuiltInRole]::Administrator
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole($admin)) {
    $arguments = '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    if ($Uninstall) { $arguments += '-Uninstall' }
    $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $arguments
    if (Test-Path $log) { Get-Content $log }
    if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
    exit $process.ExitCode
}

# Driver packages of this driver in the driver store, whatever their version.
function Get-InstalledPackage {
    Get-ChildItem "$env:windir\INF\oem*.inf" -ErrorAction SilentlyContinue |
        Where-Object { Select-String -Path $_ -Pattern 'winstadia\.dll' -Quiet }
}

# Takes the trust away from this driver's certificates, from all of them or
# from those the filter picks.
function Remove-Trust([scriptblock]$filter = { $true }) {
    foreach ($store in $stores) {
        Get-ChildItem "Cert:\LocalMachine\$store" |
            Where-Object { $_.Subject -eq $certSubject } |
            Where-Object $filter |
            Remove-Item
    }
}

# Windows wants driver packages signed by someone the machine trusts. The
# certificate for that is made here and trusted only here, and its private key
# is gone again once the package is signed, so nothing else can ever be signed
# with it.
function Add-Signature {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject `
        -CertStoreLocation Cert:\LocalMachine\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(30)
    try {
        # Signing checks the result, so the trust has to be there first.
        $public = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (, $cert.RawData)
        foreach ($store in $stores) {
            $target = New-Object Security.Cryptography.X509Certificates.X509Store $store, 'LocalMachine'
            $target.Open('ReadWrite')
            $target.Add($public)
            $target.Close()
        }

        Remove-Item $cat -ErrorAction SilentlyContinue
        New-FileCatalog -Path $pkg -CatalogFilePath $cat -CatalogVersion 2 | Out-Null
        $signature = Set-AuthenticodeSignature -FilePath $cat -Certificate $cert -HashAlgorithm SHA256
        if ($signature.Status -ne 'Valid') { throw "signing failed: $($signature.StatusMessage)" }
    } finally {
        Remove-Item $cert.PSPath -DeleteKey
    }
    $cert.Thumbprint
}

$exitCode = 0
$restartRequired = $false
$pnputilExitCode = 0
New-Item -ItemType Directory -Force (Split-Path $log -Parent) | Out-Null
Start-Transcript $log -Force | Out-Null
try {
    if ($Uninstall) {
        # Returns the controller to the inbox HID driver.
        foreach ($package in @(Get-InstalledPackage)) {
            pnputil /delete-driver $package.Name /uninstall
            $removeExitCode = $LASTEXITCODE
            if ($removeExitCode -eq 3010) {
                $restartRequired = $true
            } elseif ($removeExitCode -ne 0) {
                throw "pnputil /delete-driver failed for $($package.Name) with exit code $removeExitCode"
            }
        }
        Remove-Trust
    } else {
        if (-not (Test-Path $inf)) { throw "no driver package in $pkg, run build.ps1 first" }
        $thumbprint = Add-Signature
        $previous = @(Get-InstalledPackage)
        pnputil /add-driver $inf /install
        $pnputilExitCode = $LASTEXITCODE
        "pnputil exit code: $pnputilExitCode"
        if (@(Get-InstalledPackage).Count -gt $previous.Count) {
            # Earlier versions would pile up in the driver store. A controller
            # that is not connected right now is still on one, and moves to the
            # new package with this.
            foreach ($package in $previous) {
                pnputil /delete-driver $package.Name /uninstall
                $removeExitCode = $LASTEXITCODE
                if ($removeExitCode -eq 3010) {
                    $restartRequired = $true
                } elseif ($removeExitCode -ne 0) {
                    throw "pnputil /delete-driver failed for $($package.Name) with exit code $removeExitCode"
                }
            }
            Remove-Trust { $_.Thumbprint -ne $thumbprint }
        } else {
            # The same build was in the store already, under the certificate
            # it got back then.
            Remove-Trust { $_.Thumbprint -eq $thumbprint }
        }
        # 259 = the package was staged but no controller was connected to
        # install onto yet. Checked after the cleanup so a failed install
        # still gets its certificate trust removed.
        if ($pnputilExitCode -notin 0, 259, 3010) {
            throw "pnputil /add-driver failed with exit code $pnputilExitCode"
        }
        if ($pnputilExitCode -eq 3010) {
            $restartRequired = $true
        }
        if ($pnputilExitCode -eq 259) {
            'No controller was connected. Reconnect it to finish installation.'
        }
    }
    if ($restartRequired) {
        'Windows requires a restart to finish. Restart the PC, then reconnect the controller.'
    } elseif ($Uninstall) {
        'Driver removed. Reconnect the controller to use the inbox Windows driver.'
    }
} catch {
    "FAILED: $_"
    $exitCode = 1
} finally {
    Stop-Transcript | Out-Null
}
if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
exit $exitCode
