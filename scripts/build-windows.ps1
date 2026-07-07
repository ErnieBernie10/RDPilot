param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

if (-not (Test-Path -LiteralPath (Join-Path $VcpkgRoot "scripts\buildsystems\vcpkg.cmake"))) {
    throw "vcpkg was not found at $VcpkgRoot. Run scripts/setup-windows-vcpkg.ps1 first, or pass -VcpkgRoot."
}

$dotnetArgs = @(
    "build",
    (Join-Path $repoRoot "RDPilot.slnx"),
    "-c",
    $Configuration,
    "/p:NativeWrapperUseVcpkg=true",
    "/p:NativeWrapperVcpkgRoot=$($VcpkgRoot -replace '\\', '/')"
)


dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}
