param(
    [string]$VcpkgRoot = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "vcpkg")
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$OverlayPorts = Join-Path $RepoRoot "vcpkg-overlay-ports"

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

$installedFreeRdp = & $vcpkgExe list "freerdp:x64-windows"
if ($installedFreeRdp -and ($installedFreeRdp -notmatch "3\.26\.0#1")) {
    & $vcpkgExe remove "freerdp:x64-windows" --recurse
    if ($LASTEXITCODE -ne 0) {
        throw "vcpkg remove failed with exit code $LASTEXITCODE."
    }
}

& $vcpkgExe install "freerdp[client]:x64-windows" "--overlay-ports=$OverlayPorts"
if ($LASTEXITCODE -ne 0) {
    throw "vcpkg install failed with exit code $LASTEXITCODE."
}

"vcpkg is ready at $VcpkgRoot"
