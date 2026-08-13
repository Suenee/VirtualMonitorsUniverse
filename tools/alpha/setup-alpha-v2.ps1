[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$LegacySetup = Join-Path $PSScriptRoot 'setup-alpha.ps1'

function Test-VddInstalled {
    $device = Get-CimInstance Win32_PnPEntity |
        Where-Object { $_.PNPDeviceID -and $_.PNPDeviceID.ToUpperInvariant().StartsWith('ROOT\MTTVDD') } |
        Select-Object -First 1
    return $null -ne $device
}

if (Test-VddInstalled) {
    Write-Host 'Virtual Display Driver is already installed. Installation step skipped.' -ForegroundColor Green
    exit 0
}

if (-not (Test-Path $LegacySetup)) {
    throw "Required setup script not found: $LegacySetup"
}

# The original installer can successfully install the driver but, under StrictMode,
# may fail while reading an undefined LASTEXITCODE afterwards. Run it as a child
# process and verify the actual installation state instead of trusting that final check.
$process = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f $LegacySetup)
) -Wait -PassThru -NoNewWindow

if (-not (Test-VddInstalled)) {
    throw "Virtual Display Driver installation did not complete successfully (child exit code $($process.ExitCode))."
}

Write-Host 'Virtual Display Driver installation verified.' -ForegroundColor Green
exit 0
