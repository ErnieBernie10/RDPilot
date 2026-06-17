param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

& (Join-Path $PSScriptRoot "build-windows.ps1") -Configuration $Configuration -VcpkgRoot $VcpkgRoot
if ($LASTEXITCODE -ne 0) {
    throw "build-windows.ps1 failed with exit code $LASTEXITCODE."
}

dotnet run --no-build --project (Join-Path $repoRoot "RDP.Client\RDP.Client.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet run failed with exit code $LASTEXITCODE."
}
