[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo_root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$source_path = Join-Path $PSScriptRoot 'multivdd-reflow-v10.ps1'
$helper_path = Join-Path $PSScriptRoot 'displayconfig-topology.ps1'
$runtime_dir = Join-Path $repo_root '.runtime\alpha'
$runtime_path = Join-Path $runtime_dir 'multivdd-reflow.runtime.ps1'
$logs_dir = Join-Path $repo_root 'logs'

foreach ($required in @($source_path, $helper_path)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing multi-VDD dependency: $required"
    }
}

New-Item -ItemType Directory -Path $runtime_dir -Force | Out-Null
New-Item -ItemType Directory -Path $logs_dir -Force | Out-Null

try {
    $source = Get-Content -LiteralPath $source_path -Raw -Encoding UTF8
    $source = $source -replace "`r`n", "`n"

    $logs_literal = $logs_dir.Replace("'", "''")
    $helper_literal = $helper_path.Replace("'", "''")

    $old_log = '$log_path = Join-Path $repo_root ''multivddtest.log'''
    $new_log = '$log_path = Join-Path ''' + $logs_literal + ''' ''multivddtest.log'''
    if (-not $source.Contains($old_log)) {
        throw 'Could not locate multi-VDD log-path anchor.'
    }
    $source = $source.Replace($old_log, $new_log)

    $old_helper = '$topology_helper = Join-Path $PSScriptRoot ''displayconfig-topology.ps1'''
    $new_helper = '$topology_helper = ''' + $helper_literal + ''''
    if (-not $source.Contains($old_helper)) {
        throw 'Could not locate multi-VDD topology-helper anchor.'
    }
    $source = $source.Replace($old_helper, $new_helper)

    # displayconfig-topology.ps1 calls Get-Mode during disconnect/reconnect.
    # The generated runtime is launched in a fresh PowerShell process, so a
    # function defined only in this wrapper would not be inherited. Inject the
    # compatibility adapter directly into the generated runtime instead.
    $init_anchor = '$ErrorActionPreference = ''Stop'''
    if (-not $source.Contains($init_anchor)) {
        throw 'Could not locate multi-VDD initialization anchor.'
    }

    $get_mode_adapter = @'

function Get-Mode {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [Alias('Name')]
        [string]$DeviceName
    )

    if ([string]::IsNullOrWhiteSpace($DeviceName)) {
        throw 'Cannot read display mode because the GDI display name is empty.'
    }

    if (-not ('Vmu.ReflowV10Api' -as [type])) {
        throw 'Vmu.ReflowV10Api is not initialized yet.'
    }

    $display_source = @([Vmu.ReflowV10Api]::ActiveSources()) |
        Where-Object { $_.GdiName -eq $DeviceName } |
        Select-Object -First 1

    if ($null -eq $display_source) {
        throw "Cannot read $DeviceName mode."
    }

    return [pscustomobject]@{
        dmPositionX          = [int]$display_source.X
        dmPositionY          = [int]$display_source.Y
        dmPelsWidth          = [uint32]$display_source.Width
        dmPelsHeight         = [uint32]$display_source.Height
        dmDisplayFrequency   = [uint32]60
        dmDisplayOrientation = [uint32]0
        dmDisplayFixedOutput = [uint32]0
    }
}
'@

    $source = $source.Replace($init_anchor, ($init_anchor + $get_mode_adapter))

    $source = $source.Replace(
        "Write-Log 'Runner: multivdd-isolation-v10-anchor-aware-reflow' Cyan",
        "Write-Log 'Runner: multivdd-isolation-v13-runtime-get-mode' Cyan"
    )

    $source = $source -replace "(?<!`r)`n", "`r`n"
    Set-Content -LiteralPath $runtime_path -Value $source -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtime_path
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtime_path -Force -ErrorAction SilentlyContinue
}
