[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$RuntimeRoot = Join-Path $RepoRoot '.runtime\alpha'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $PSCommandPath)) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -Wait -PassThru
    exit $process.ExitCode
}

$devices = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' })
$infPaths = @()
foreach ($device in $devices) {
    try {
        $inf = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction Stop).Data
        if ($inf) { $infPaths += [string]$inf }
    } catch {}
}
$infPaths = @($infPaths | Sort-Object -Unique)

foreach ($device in $devices) {
    Write-Host "Removing VDD device node: $($device.InstanceId)" -ForegroundColor Yellow
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -ArgumentList @('/remove-device',('"{0}"' -f $device.InstanceId)) -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "Could not remove $($device.InstanceId)." }
}

foreach ($inf in $infPaths) {
    Write-Host "Removing VDD driver package: $inf" -ForegroundColor Yellow
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -ArgumentList @('/delete-driver',$inf,'/uninstall','/force') -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) { throw "Could not remove driver package $inf." }
}

if (Test-Path -LiteralPath $RuntimeRoot) {
    Write-Host "Removing repository-local ALPHA runtime: $RuntimeRoot" -ForegroundColor Yellow
    Remove-Item -LiteralPath $RuntimeRoot -Recurse -Force
}

Write-Host 'Targeted VDD cleanup completed. No unrelated display registry keys or directories were touched.' -ForegroundColor Green
exit 0
