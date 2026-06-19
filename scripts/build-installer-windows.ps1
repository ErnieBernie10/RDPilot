param(
    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg"),

    [string]$InnoSetupCompiler,

    [string]$AppVersion = "0.1.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$projectPath = Join-Path $repoRoot "RDPilot.Client\RDPilot.Client.csproj"
$publishScript = Join-Path $PSScriptRoot "publish-windows.ps1"
$project = [xml](Get-Content -LiteralPath $projectPath)
$targetFramework = $project.Project.PropertyGroup.TargetFramework | Select-Object -First 1

if (-not $targetFramework) {
    throw "TargetFramework was not found in $projectPath."
}

$publishDir = Join-Path $repoRoot "RDPilot.Client\bin\Release\$targetFramework\win-x64\publish"
$installerScript = Join-Path $repoRoot "installer\RDPilot.Client.iss"
$outputDir = Join-Path $repoRoot "artifacts\installer"
$installerBaseName = "RDPilot-Setup-$AppVersion-win-x64"

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

if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "AppVersion '$AppVersion' is not valid. Use major.minor.patch, for example 0.1.0."
}

& $publishScript -VcpkgRoot $VcpkgRoot -AppVersion $AppVersion
if ($LASTEXITCODE -ne 0) {
    throw "publish-windows.ps1 failed with exit code $LASTEXITCODE."
}

Require-PublishedFile -Path (Join-Path $publishDir "RDPilot.Client.exe") -Description "Published app executable"
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
    "/DOutputBaseFilename=$installerBaseName" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}

"Installer created at $(Join-Path $outputDir "$installerBaseName.exe")"
