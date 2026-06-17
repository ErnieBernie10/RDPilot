param(
    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$toolchain = Join-Path $VcpkgRoot "scripts\buildsystems\vcpkg.cmake"

if (-not (Test-Path -LiteralPath $toolchain)) {
    throw "vcpkg toolchain file was not found at $toolchain. Run scripts/setup-windows-vcpkg.ps1 first."
}

$vcpkgRootForMsBuild = $VcpkgRoot -replace '\\', '/'

dotnet publish (Join-Path $repoRoot "RDP.Client\RDP.Client.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    "/p:NativeWrapperUseVcpkg=true" `
    "/p:NativeWrapperVcpkgRoot=$vcpkgRootForMsBuild"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

"Published to $(Join-Path $repoRoot 'RDP.Client\bin\Release\net9.0\win-x64\publish')"
