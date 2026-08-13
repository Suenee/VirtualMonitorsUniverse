[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$VddPnpPrefix = 'ROOT\\MTTVDD'
$VddConfigPath = 'C:\VirtualDisplayDriver'
$VddRegistryPath = 'HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver'
$WingetPackageId = 'VirtualDrivers.Virtual-Display-Driver'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-VddDevices {
    Get-CimInstance Win32_PnPEntity |
        Where-Object { $_.PNPDeviceID -and $_.PNPDeviceID.ToUpperInvariant().StartsWith($VddPnpPrefix) }
}

function Show-Plan {
    Write-Host 'VMU ALPHA cleanup plan:'
    $devices = @(Get-VddDevices)

    if ($devices.Count -eq 0) {
        Write-Host '  VDD PnP devices: none found'
    }
    else {
        foreach ($device in $devices) {
            Write-Host "  VDD PnP device: $($device.PNPDeviceID) [$($device.Status)]"
        }
    }

    Write-Host ("  Config directory: {0} ({1})" -f $VddConfigPath, $(if (Test-Path -LiteralPath $VddConfigPath) { 'present' } else { 'absent' }))
    Write-Host ("  Registry key:     {0} ({1})" -f $VddRegistryPath, $(if (Test-Path -LiteralPath $VddRegistryPath) { 'present' } else { 'absent' }))
    Write-Host "  Winget package:   $WingetPackageId"
}

if ($env:OS -ne 'Windows_NT') {
    throw 'VMU ALPHA cleanup is supported only on Windows.'
}

Show-Plan

if (-not $Execute) {
    Write-Host "`nDRY RUN ONLY. Nothing was changed."
    Write-Host 'Run from an elevated terminal with: .\cleanup-vdd.ps1 -Execute'
    exit 0
}

if (-not (Test-IsAdministrator)) {
    throw 'Cleanup requires an elevated terminal (Run as administrator).'
}

Write-Host "`nStarting targeted VDD cleanup..."

$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
if ($winget) {
    Write-Host 'Requesting official package uninstall through winget...'
    & $winget.Source uninstall --id $WingetPackageId -e --silent --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'winget did not complete the uninstall successfully. Continuing with targeted residual cleanup only.'
    }
}
else {
    Write-Warning 'winget was not found. Skipping package-manager uninstall.'
}

# Remove only device nodes whose PNP ID belongs to the selected VDD hardware ID.
$devices = @(Get-VddDevices)
foreach ($device in $devices) {
    if ($PSCmdlet.ShouldProcess($device.PNPDeviceID, 'Remove VDD PnP device')) {
        Write-Host "Removing VDD device: $($device.PNPDeviceID)"
        & "$env:SystemRoot\System32\pnputil.exe" /remove-device $device.PNPDeviceID
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "pnputil could not remove $($device.PNPDeviceID)."
        }
    }
}

# These locations are exact upstream VDD-owned paths; no wildcard registry cleanup is used.
if (Test-Path -LiteralPath $VddRegistryPath) {
    if ($PSCmdlet.ShouldProcess($VddRegistryPath, 'Remove VDD registry key')) {
        Remove-Item -LiteralPath $VddRegistryPath -Recurse -Force
    }
}

if (Test-Path -LiteralPath $VddConfigPath) {
    if ($PSCmdlet.ShouldProcess($VddConfigPath, 'Remove VDD configuration directory')) {
        Remove-Item -LiteralPath $VddConfigPath -Recurse -Force
    }
}

Write-Host "`nCleanup finished. Restart Windows before repeating the ALPHA installation test."
Write-Host 'The script intentionally does not delete unrelated display registry entries or arbitrary OEM driver packages.'
