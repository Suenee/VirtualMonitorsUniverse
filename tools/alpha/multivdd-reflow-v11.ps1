[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function global:Get-Mode {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [Alias('Name')]
        [string]$DeviceName
    )

    if ([string]::IsNullOrWhiteSpace($DeviceName)) { throw 'Cannot read display mode because the GDI display name is empty.' }
    if (-not ('Vmu.ReflowV10Api' -as [type])) { throw 'Vmu.ReflowV10Api is not initialized yet.' }

    $source = @([Vmu.ReflowV10Api]::ActiveSources()) | Where-Object { $_.GdiName -eq $DeviceName } | Select-Object -First 1
    if ($null -eq $source) { throw "Cannot read $DeviceName mode." }

    return [pscustomobject]@{
        dmPositionX          = [int]$source.X
        dmPositionY          = [int]$source.Y
        dmPelsWidth          = [uint32]$source.Width
        dmPelsHeight         = [uint32]$source.Height
        dmDisplayFrequency   = [uint32]60
        dmDisplayOrientation = [uint32]0
        dmDisplayFixedOutput = [uint32]0
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logsDir = Join-Path $repoRoot 'logs'
$runtimeDir = Join-Path $repoRoot '.runtime\alpha'
$sourceRunner = Join-Path $PSScriptRoot 'multivdd-reflow-v10.ps1'
$runtimeRunner = Join-Path $runtimeDir 'multivdd-reflow.runtime.ps1'

if (-not (Test-Path -LiteralPath $sourceRunner)) { throw "Missing anchor-aware multi-VDD runner: $sourceRunner" }
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

try {
    $source = Get-Content -LiteralPath $sourceRunner -Raw -Encoding UTF8
    $oldLog = '$log_path = Join-Path $repo_root ''multivddtest.log'''
    $newLog = @'
$logs_dir = Join-Path $repo_root 'logs'
New-Item -ItemType Directory -Path $logs_dir -Force | Out-Null
$log_path = Join-Path $logs_dir 'multivddtest.log'
'@
    if (-not $source.Contains($oldLog)) { throw 'Could not locate multi-VDD log-path declaration.' }
    $source = $source.Replace($oldLog, $newLog.TrimEnd())
    Set-Content -LiteralPath $runtimeRunner -Value $source -Encoding UTF8

    & $runtimeRunner
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimeRunner -Force -ErrorAction SilentlyContinue
}
