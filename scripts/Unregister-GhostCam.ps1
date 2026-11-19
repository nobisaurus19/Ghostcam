<#
.SYNOPSIS
    Elevated uninstaller for Ghost Cam Windows 11 Virtual Camera.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

# Elevate to Administrator via UAC if not already elevated
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
        Write-Error "[-] Administrative privileges are required to unregister Ghost Cam Virtual Camera."
        exit 1223
    }
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Unregistering Ghost Cam Windows 11 Virtual Camera" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$destDll = "C:\Program Files\Ghostcam\GhostCamVirtualCamera.dll"
if (Test-Path $destDll) {
    Write-Host "[*] Unregistering COM server via regsvr32 /u /s..."
    Start-Process -FilePath "regsvr32.exe" -ArgumentList "/u /s `"$destDll`"" -Wait -NoNewWindow
}

# Clean registry keys
$clsid = "{8F4C2A19-7B3E-4D12-9A88-1E6F3C5A9B01}"
$hklmKey = "HKLM:\SOFTWARE\Classes\CLSID\$clsid"
if (Test-Path $hklmKey) {
    Remove-Item -Path $hklmKey -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[+] Removed HKLM registry key: $hklmKey" -ForegroundColor Green
}

$hkcuKey = "HKCU:\Software\Classes\CLSID\$clsid"
if (Test-Path $hkcuKey) {
    Remove-Item -Path $hkcuKey -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[+] Removed HKCU registry key: $hkcuKey" -ForegroundColor Green
}

# Clean installed DLL
if (Test-Path $destDll) {
    Remove-Item -Path $destDll -Force -ErrorAction SilentlyContinue
    Write-Host "[+] Removed $destDll" -ForegroundColor Green
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Ghost Cam successfully unregistered." -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
exit 0
