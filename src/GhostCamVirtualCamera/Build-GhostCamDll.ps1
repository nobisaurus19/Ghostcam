<#
.SYNOPSIS
    Builds the native Windows 11 Media Foundation Virtual Camera COM DLL (GhostCamVirtualCamera.dll)
    in C++20 using MSVC BuildTools (C:\BuildTools) and Windows SDK 10.0.22621.0.
#>

[CmdletBinding()]
param (
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$outDir = Join-Path $scriptDir "bin\x64\$Configuration"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$vcvarsBat = "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvarsBat)) {
    throw "MSVC vcvars64.bat not found at: $vcvarsBat"
}

$targetDll = Join-Path $outDir "GhostCamVirtualCamera.dll"
$defFile = Join-Path $scriptDir "GhostCam.def"
$srcFiles = @(
    (Join-Path $scriptDir "GhostCamMediaSource.cpp"),
    (Join-Path $scriptDir "DllMain.cpp")
)
$testSrc = Join-Path $scriptDir "Test-GhostCamVirtualCamera.cpp"
$testExe = Join-Path $scriptDir "Test-GhostCamVirtualCamera.exe"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building GhostCamVirtualCamera.dll (x64 $Configuration)" -ForegroundColor Cyan
Write-Host " Source dir: $scriptDir"
Write-Host " Output dir: $outDir"
Write-Host "=========================================================="

# Create temporary build batch file
$tempBat = Join-Path $outDir "build_temp.bat"
$compileCmd = @"
@echo off
call "$vcvarsBat"
cd /d "$outDir"
cl.exe /nologo /std:c++20 /EHsc /O2 /W4 /wd4324 /permissive- /LD /D_UNICODE /DUNICODE /D_WINDLL /DWIN32_LEAN_AND_MEAN /DNDEBUG /I"$scriptDir" "$($srcFiles[0])" "$($srcFiles[1])" /link /NOLOGO /DLL /DEF:"$defFile" /OUT:"$targetDll" mfplat.lib mf.lib mfuuid.lib mfsensorgroup.lib advapi32.lib ole32.lib oleaut32.lib shlwapi.lib
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%
cl.exe /nologo /std:c++20 /EHsc /O2 /W4 /permissive- /D_UNICODE /DUNICODE /DWIN32_LEAN_AND_MEAN /DNDEBUG /I"$scriptDir" "$testSrc" /link /NOLOGO /OUT:"$testExe" mfplat.lib mf.lib mfuuid.lib mfreadwrite.lib ole32.lib oleaut32.lib advapi32.lib shlwapi.lib
exit /b %ERRORLEVEL%
"@

Set-Content -Path $tempBat -Value $compileCmd -Encoding ASCII

try {
    cmd.exe /c "`"$tempBat`""
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
}
finally {
    if (Test-Path $tempBat) {
        Remove-Item $tempBat -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $targetDll)) {
    throw "Target DLL was not produced: $targetDll"
}

$fileInfo = Get-Item $targetDll
Write-Host "SUCCESS: Generated $targetDll ($($fileInfo.Length) bytes)" -ForegroundColor Green
