[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'alfatest.ps1'
$runtimePath = Join-Path $PSScriptRoot '.alfatest.runtime.ps1'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "ALPHA test source not found: $sourcePath"
}

try {
    $source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8

    # PowerShell 5.1 rejects an empty string for a mandatory string parameter
    # unless AllowEmptyString is explicitly declared. The ALPHA logger uses
    # empty messages as intentional blank lines between sections.
    $needle = '[Parameter(Mandatory = $true)][string]$Message,'
    $replacement = '[Parameter(Mandatory = $true)][AllowEmptyString()][string]$Message,'

    if ($source.Contains($needle)) {
        $source = $source.Replace($needle, $replacement)
    }
    elseif (-not $source.Contains($replacement)) {
        throw 'Could not locate the expected Write-Log parameter declaration.'
    }

    Set-Content -LiteralPath $runtimePath -Value $source -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtimePath
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
}
