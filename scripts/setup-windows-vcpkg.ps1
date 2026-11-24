param(
    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg")
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$VcpkgRoot = [System.IO.Path]::GetFullPath($VcpkgRoot)
$OverlayPorts = Join-Path $RepoRoot "vcpkg-overlay-ports"

if (-not (Test-Path -LiteralPath (Join-Path $OverlayPorts "freerdp\portfile.cmake"))) {
    throw "The FreeRDP vcpkg overlay was not found at $OverlayPorts."
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git is required to clone vcpkg."
}

$parent = Split-Path -Parent $VcpkgRoot
if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent | Out-Null
}

if (-not (Test-Path -LiteralPath $VcpkgRoot)) {
    git clone https://github.com/microsoft/vcpkg.git $VcpkgRoot
}

$vcpkgExe = Join-Path $VcpkgRoot "vcpkg.exe"
if (-not (Test-Path -LiteralPath $vcpkgExe)) {
    $bootstrap = Join-Path $VcpkgRoot "bootstrap-vcpkg.bat"
    if (-not (Test-Path -LiteralPath $bootstrap)) {
        throw "vcpkg bootstrap script was not found at $bootstrap."
    }

    & $bootstrap -disableMetrics
    if ($LASTEXITCODE -ne 0) {
        throw "vcpkg bootstrap failed with exit code $LASTEXITCODE."
    }
}

Push-Location $RepoRoot
try {
    & $vcpkgExe install "--overlay-ports=$OverlayPorts"
    if ($LASTEXITCODE -ne 0) {
        throw "vcpkg install failed with exit code $LASTEXITCODE."
    }

    $pinnedOpenSsl = (Get-Content -Raw -LiteralPath (Join-Path $OverlayPorts "openssl\vcpkg.json") | ConvertFrom-Json).version
    $opensslHeader = Join-Path $RepoRoot "vcpkg_installed\x64-windows\include\openssl\opensslv.h"
    $installedOpenSsl = (Select-String -LiteralPath $opensslHeader -Pattern 'OPENSSL_VERSION_STR "([^"]+)"').Matches[0].Groups[1].Value
    if ($installedOpenSsl -ne $pinnedOpenSsl) {
        throw "vcpkg installed OpenSSL $installedOpenSsl, but the overlay pins $pinnedOpenSsl."
    }
    "OpenSSL $installedOpenSsl from vcpkg-overlay-ports"
}
finally {
    Pop-Location
}

"vcpkg is ready at $VcpkgRoot"
