param(
    [string]$ExePath,
    [string]$VcpkgRoot,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$FreeRdpArgs
)

$ErrorActionPreference = 'Stop'

if (-not $VcpkgRoot) {
    $repoParent = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $VcpkgRoot = Join-Path $repoParent 'vcpkg'
}

$candidates = New-Object System.Collections.Generic.List[string]

if ($ExePath) {
    $candidates.Add($ExePath)
}

$command = Get-Command wfreerdp.exe -ErrorAction SilentlyContinue
if ($command) {
    $candidates.Add($command.Source)
}

$vcpkgInstalledTool = Join-Path $VcpkgRoot 'installed\x64-windows\tools\freerdp\wfreerdp.exe'
$candidates.Add($vcpkgInstalledTool)


$exe = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $exe) {
    throw "wfreerdp.exe was not found. Pass -ExePath or add it to PATH."
}

$toolDir = Split-Path -Parent $exe
$env:PATH = "$toolDir;$env:PATH"
& $exe @FreeRdpArgs
