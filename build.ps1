#Requires -Version 5.1
<#
.SYNOPSIS
    Builds VngeQuitFix.dll and optionally deploys it to the game's BepInEx plugins folder.

.PARAMETER GameDir
    Game root that contains CharaStudio_Data and BepInEx. Default: D:\Games\Koikatu

.PARAMETER Deploy
    Copy the built DLL to <GameDir>\BepInEx\plugins\VngeQuitFix.dll.
    The game must be closed, otherwise the DLL is locked.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Deploy
    .\build.ps1 -GameDir 'D:\Games\Koikatu' -Deploy
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'D:\Games\Koikatu',
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v3.5\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) {
    throw 'The .NET Framework 3.5 C# compiler (csc.exe) was not found. Enable the ".NET Framework 3.5 (includes .NET 2.0 and 3.0)" Windows feature.'
}

if (-not (Test-Path -LiteralPath $srcDir)) {
    throw "Source directory not found: $srcDir"
}
$sources = @(Get-ChildItem -LiteralPath $srcDir -Filter '*.cs' -File | Sort-Object Name)
if ($sources.Count -eq 0) {
    throw "No .cs files found in $srcDir"
}

$references = @(
    (Join-Path $GameDir 'CharaStudio_Data\Managed\mscorlib.dll'),
    (Join-Path $GameDir 'CharaStudio_Data\Managed\System.dll'),
    (Join-Path $GameDir 'CharaStudio_Data\Managed\System.Core.dll'),
    (Join-Path $GameDir 'CharaStudio_Data\Managed\UnityEngine.dll'),
    (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'),
    (Join-Path $GameDir 'BepInEx\core\0Harmony.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "Missing reference assembly: $reference (check -GameDir)"
    }
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$outputDll = Join-Path $distDir 'VngeQuitFix.dll'

# Build with the .NET Framework 3.5 C# compiler (C# 3.0) against the game's own 3.5-era
# class libraries (Unity Mono 2.0). /noconfig ignores the framework csc.rsp and /nostdlib+
# makes the references below the only BCL surface, so anything outside .NET 3.5 is rejected
# at compile time instead of failing at runtime.
$cscArgs = @(
    '/nologo',
    '/noconfig',
    '/nostdlib+',
    '/target:library',
    '/optimize+',
    '/warnaserror-'
)
foreach ($reference in $references) { $cscArgs += "/r:$reference" }
$cscArgs += "/out:$outputDll"
$cscArgs += $sources.FullName

Write-Host ("Compiling {0} source file(s) with {1} (file version {2})" -f $sources.Count, $csc, (Get-Item -LiteralPath $csc).VersionInfo.FileVersion)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$built = Get-Item -LiteralPath $outputDll
Write-Host ("Built: {0} ({1} bytes, file version {2})" -f $built.FullName, $built.Length, $built.VersionInfo.FileVersion)

if ($Deploy) {
    $pluginDir = Join-Path $GameDir 'BepInEx\plugins'
    if (-not (Test-Path -LiteralPath $pluginDir)) {
        throw "Plugin directory not found: $pluginDir"
    }

    $running = @(Get-Process -Name 'CharaStudio', 'Koikatu', 'KoikatuVR' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $names = ($running | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
        throw "Close the game before deploying; still running: $names"
    }

    $target = Join-Path $pluginDir 'VngeQuitFix.dll'
    Copy-Item -LiteralPath $outputDll -Destination $target -Force

    $sourceHash = (Get-FileHash -LiteralPath $outputDll -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) {
        throw "Deployed file hash mismatch: $target"
    }

    $deployed = Get-Item -LiteralPath $target
    Write-Host ("Deployed: {0} (file version {1}, sha256 {2})" -f $deployed.FullName, $deployed.VersionInfo.FileVersion, $targetHash)
}
