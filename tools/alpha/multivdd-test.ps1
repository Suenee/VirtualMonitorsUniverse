[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runner = Join-Path $PSScriptRoot 'multivdd-reflow-v12.ps1'
if (-not (Test-Path -LiteralPath $runner)) {
    throw "Missing multi-VDD reflow runner: $runner"
}

& $runner
exit $LASTEXITCODE
