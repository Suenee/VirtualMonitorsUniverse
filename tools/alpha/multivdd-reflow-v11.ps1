[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Compatibility adapter for displayconfig-topology.ps1.
# The shared topology helper expects a Get-Mode function returning a DEVMODE-like
# object. The v10 reflow runner moved mode access behind ReflowV10Api and no
# longer exposed this legacy function name, which broke the disconnect phase
# after the reflow tests had already passed.
function global:Get-Mode {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [Alias('Name')]
        [string]$DeviceName
    )

    if ([string]::IsNullOrWhiteSpace($DeviceName)) {
        throw 'Cannot read display mode because the GDI display name is empty.'
    }

    # ReflowV10Api is initialized by the underlying runner before the topology
    # helper invokes Get-Mode during disconnect/reconnect.
    if (-not ('Vmu.ReflowV10Api' -as [type])) {
        throw 'Vmu.ReflowV10Api is not initialized yet.'
    }

    $source = @([Vmu.ReflowV10Api]::ActiveSources()) |
        Where-Object { $_.GdiName -eq $DeviceName } |
        Select-Object -First 1

    if ($null -eq $source) {
        throw "Cannot read $DeviceName mode."
    }

    # Provide the fields consumed by the shared topology helper. Keep this
    # adapter intentionally small instead of duplicating display API logic.
    return [pscustomobject]@{
        dmPositionX           = [int]$source.X
        dmPositionY           = [int]$source.Y
        dmPelsWidth           = [uint32]$source.Width
        dmPelsHeight          = [uint32]$source.Height
        dmDisplayFrequency    = [uint32]60
        dmDisplayOrientation  = [uint32]0
        dmDisplayFixedOutput  = [uint32]0
    }
}

$runner = Join-Path $PSScriptRoot 'multivdd-reflow-v10.ps1'
if (-not (Test-Path -LiteralPath $runner)) {
    throw "Missing anchor-aware multi-VDD runner: $runner"
}

& $runner
exit $LASTEXITCODE
