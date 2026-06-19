param(
    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg"),

    [string]$AppVersion = "0.1.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$projectPath = Join-Path $repoRoot "RDPilot.Client\RDPilot.Client.csproj"
$toolchain = Join-Path $VcpkgRoot "scripts\buildsystems\vcpkg.cmake"

if (-not (Test-Path -LiteralPath $toolchain)) {
    throw "vcpkg toolchain file was not found at $toolchain. Run scripts/setup-windows-vcpkg.ps1 first."
}

$vcpkgRootForMsBuild = $VcpkgRoot -replace '\\', '/'
$project = [xml](Get-Content -LiteralPath $projectPath)
$targetFramework = $project.Project.PropertyGroup.TargetFramework | Select-Object -First 1

if (-not $targetFramework) {
    throw "TargetFramework was not found in $projectPath."
}

if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "AppVersion '$AppVersion' is not valid. Use major.minor.patch, for example 0.1.0."
}

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    "/p:Version=$AppVersion" `
    "/p:AssemblyVersion=$AppVersion.0" `
    "/p:FileVersion=$AppVersion.0" `
    "/p:InformationalVersion=$AppVersion" `
    "/p:NativeWrapperUseVcpkg=true" `
    "/p:NativeWrapperVcpkgRoot=$vcpkgRootForMsBuild"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

"Published to $(Join-Path $repoRoot "RDPilot.Client\bin\Release\$targetFramework\win-x64\publish")"
