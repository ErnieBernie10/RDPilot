param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$FreeRdpArgs
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolDir = 'C:\Users\arneb\Sources\vcpkg\packages\freerdp_x64-windows\tools\freerdp'
$exe = Join-Path $toolDir 'wfreerdp.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    throw "wfreerdp.exe was not found at '$exe'. Build the solution first."
}

$env:PATH = "$toolDir;$env:PATH"
& $exe @FreeRdpArgs
