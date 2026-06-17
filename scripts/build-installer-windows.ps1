param(
    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg"),

    [string]$InnoSetupCompiler,

    [string]$AppVersion = "0.1.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$publishScript = Join-Path $PSScriptRoot "publish-windows.ps1"
$publishDir = Join-Path $repoRoot "RDP.Client\bin\Release\net9.0\win-x64\publish"
$installerScript = Join-Path $repoRoot "installer\RDP.Client.iss"
$outputDir = Join-Path $repoRoot "artifacts\installer"

function Find-InnoSetupCompiler {
    if ($InnoSetupCompiler) {
        if (-not (Test-Path -LiteralPath $InnoSetupCompiler)) {
            throw "Inno Setup compiler was not found at $InnoSetupCompiler."
        }

        return (Resolve-Path $InnoSetupCompiler).Path
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidatePaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath) {
            return $candidatePath
        }
    }

    throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup 6 or pass -InnoSetupCompiler."
}

function Require-PublishedFile {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description was not found at $Path. The installer must package the complete app, including native dependencies."
    }
}

$iscc = Find-InnoSetupCompiler

& $publishScript -VcpkgRoot $VcpkgRoot
if ($LASTEXITCODE -ne 0) {
    throw "publish-windows.ps1 failed with exit code $LASTEXITCODE."
}

Require-PublishedFile -Path (Join-Path $publishDir "RDP.Client.exe") -Description "Published app executable"
Require-PublishedFile -Path (Join-Path $publishDir "freerdp_wrapper.dll") -Description "Native RDP wrapper DLL"
Require-PublishedFile -Path (Join-Path $publishDir "freerdp3.dll") -Description "FreeRDP runtime DLL"
Require-PublishedFile -Path (Join-Path $publishDir "freerdp-client3.dll") -Description "FreeRDP client runtime DLL"
Require-PublishedFile -Path (Join-Path $publishDir "winpr3.dll") -Description "WinPR runtime DLL"

$dependencyDlls = @(Get-ChildItem -LiteralPath $publishDir -Filter "*.dll" -File)
if ($dependencyDlls.Count -lt 4) {
    throw "Published app contains only $($dependencyDlls.Count) DLL(s). Expected the native wrapper and app-local dependency DLLs in $publishDir."
}

if (-not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

& $iscc `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$outputDir" `
    "/DAppVersion=$AppVersion" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}

"Installer created at $(Join-Path $outputDir 'RDP.Client-Setup-win-x64.exe')"
