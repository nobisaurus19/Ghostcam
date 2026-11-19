<#
.SYNOPSIS
    One-click elevated installer and COM registrar for Ghost Cam Windows 11 Virtual Camera.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

# 1. Elevate to Administrator via UAC if not already elevated
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[*] Requesting Administrator privileges via UAC..." -ForegroundColor Yellow
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "powershell.exe"
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $psi.Verb = "runas"
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.WaitForExit()
        exit $proc.ExitCode
    }
    catch {
        Write-Error "[-] Administrative privileges are required to register Ghost Cam Virtual Camera."
        exit 1223 # ERROR_CANCELLED
    }
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Registering Ghost Cam Windows 11 Virtual Camera (x64)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir

# 2. Locate built GhostCamVirtualCamera.dll
$candidatePaths = @(
    (Join-Path $repoRoot "src\GhostCamVirtualCamera\bin\x64\Release\GhostCamVirtualCamera.dll"),
    (Join-Path $repoRoot "src\Ghostcam.Studio\bin\Release\net8.0-windows\GhostCamVirtualCamera.dll"),
    (Join-Path $scriptDir "..\GhostCamVirtualCamera.dll"),
    (Join-Path $scriptDir "GhostCamVirtualCamera.dll"),
    (Join-Path $repoRoot "GhostCamVirtualCamera.dll")
)

$sourceDll = $null
foreach ($path in $candidatePaths) {
    if (Test-Path $path) {
        $sourceDll = (Resolve-Path $path).Path
        break
    }
}

if (-not $sourceDll) {
    Write-Error "[-] Could not find GhostCamVirtualCamera.dll in release build directories!"
    exit 1
}

Write-Host "[+] Found source DLL: $sourceDll" -ForegroundColor Green

# 3. Create target directory and copy DLL to C:\Program Files\Ghostcam
$installDir = "C:\Program Files\Ghostcam"
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
}

$destDll = Join-Path $installDir "GhostCamVirtualCamera.dll"
$oldDll = "$destDll.old"

# 3a. Stop Windows Camera Frame Server if running to release DLL locks
$frameServices = @("FrameServer", "FrameServerMonitor")
foreach ($svcName in $frameServices) {
    $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Host "[*] Stopping $svcName to release potential DLL locks..." -ForegroundColor Yellow
        Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }
}

# 3b. Clean up stale .old file if present
if (Test-Path $oldDll) {
    Remove-Item -Path $oldDll -Force -ErrorAction SilentlyContinue
}

# 3c. Copy DLL safely (if locked by another process, rename existing DLL to .old and retry)
try {
    Copy-Item -Path $sourceDll -Destination $destDll -Force -ErrorAction Stop
}
catch {
    Write-Host "[!] $destDll is locked by an active process. Moving to $oldDll and retrying..." -ForegroundColor Yellow
    if (Test-Path $destDll) {
        Move-Item -Path $destDll -Destination $oldDll -Force
    }
    Copy-Item -Path $sourceDll -Destination $destDll -Force
}
Write-Host "[+] Installed to: $destDll" -ForegroundColor Green

# 4. Create C:\ProgramData\Ghostcam and pre-create frame.shm (18,874,368 zeroed bytes)
$shmDir = "C:\ProgramData\Ghostcam"
if (-not (Test-Path $shmDir)) {
    New-Item -ItemType Directory -Force -Path $shmDir | Out-Null
}

$shmPath = Join-Path $shmDir "frame.shm"
$expectedSize = 18874368 # 18 MB

if (-not (Test-Path $shmPath) -or ((Get-Item $shmPath).Length -ne $expectedSize)) {
    Write-Host "[*] Initializing shared memory file ($expectedSize bytes)..."
    $fs = [System.IO.File]::Create($shmPath)
    $fs.SetLength($expectedSize)
    $fs.Close()
}

# Grant permissions using well-known SIDs:
# Users: *S-1-5-32-545 (Modify)
# LOCAL SERVICE: *S-1-5-19 (Modify)
# ALL APPLICATION PACKAGES: *S-1-15-2-1 (Read/Execute)
Write-Host "[*] Applying security permissions on $shmDir..."
icacls.exe "$shmDir" /grant "*S-1-5-32-545:(OI)(CI)M" /grant "*S-1-5-19:(OI)(CI)M" /grant "*S-1-15-2-1:(OI)(CI)RX" /T | Out-Null
icacls.exe "$shmPath" /grant "*S-1-5-32-545:M" /grant "*S-1-5-19:M" /grant "*S-1-15-2-1:RX" | Out-Null
Write-Host "[+] Permissions granted successfully" -ForegroundColor Green

# 5. Register COM Server in HKLM using regsvr32.exe /s
Write-Host "[*] Registering COM InprocServer32 with regsvr32..."
$regProc = Start-Process -FilePath "regsvr32.exe" -ArgumentList "/s `"$destDll`"" -Wait -PassThru -NoNewWindow
if ($regProc.ExitCode -ne 0) {
    Write-Error "[-] regsvr32 failed with exit code $($regProc.ExitCode)"
    exit $regProc.ExitCode
}

# 6. Verify Registry Registration in HKLM
$clsidPath = "HKLM:\SOFTWARE\Classes\CLSID\{8F4C2A19-7B3E-4D12-9A88-1E6F3C5A9B01}\InprocServer32"
if (-not (Test-Path $clsidPath)) {
    $clsidPath = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{8F4C2A19-7B3E-4D12-9A88-1E6F3C5A9B01}\InprocServer32"
}

if (Test-Path $clsidPath) {
    $server = (Get-ItemProperty -Path $clsidPath).'(default)'
    Write-Host "[+] Verified Registry Key: $clsidPath" -ForegroundColor Green
    Write-Host "[+] InprocServer32 -> $server" -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Cyan
    Write-Host " Ghost Cam successfully registered and ready for use!" -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Cyan
    exit 0
} else {
    Write-Error "[-] Registration verification failed: $clsidPath not found"
    exit 1
}
