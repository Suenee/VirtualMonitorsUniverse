[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'alfatest-v2.ps1'
$runtimeDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path '.runtime\alpha'
$runtimePath = Join-Path $runtimeDir 'alfatest-v2.runtime.ps1'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "ALPHA test source not found: $sourcePath"
}

New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

try {
    $source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8

    # PowerShell 5.1 interprets a colon immediately following a variable name
    # as a scoped/drive-qualified variable reference. Delimit the variable
    # explicitly so strings like "Using cached <label>: <path>" parse safely.
    $source = $source.Replace('"Using cached $Label: $Path"', '"Using cached ${Label}: $Path"')

    Set-Content -LiteralPath $runtimePath -Value $source -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtimePath
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
}
