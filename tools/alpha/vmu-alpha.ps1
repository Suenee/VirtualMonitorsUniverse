[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('--on', '--off', '--status', '--list', '--help')]
    [string]$Command
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PipeName = 'MTTVirtualDisplayPipe'
$VddPnpPrefix = 'ROOT\MTTVDD'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Administrator {
    if (-not (Test-IsAdministrator)) {
        throw 'This command requires an elevated terminal (Run as administrator).'
    }
}

function Get-VddDevices {
    Get-CimInstance Win32_PnPEntity |
        Where-Object { $_.PNPDeviceID -and $_.PNPDeviceID.ToUpperInvariant().StartsWith($VddPnpPrefix) }
}

function Get-ActiveScreens {
    Add-Type -AssemblyName System.Windows.Forms
    return [System.Windows.Forms.Screen]::AllScreens
}

function Show-Screens {
    $screens = @(Get-ActiveScreens)
    foreach ($screen in $screens) {
        $bounds = $screen.Bounds
        [pscustomobject]@{
            DeviceName = $screen.DeviceName
            Resolution = "{0}x{1}" -f $bounds.Width, $bounds.Height
            Position   = "{0},{1}" -f $bounds.X, $bounds.Y
            Primary    = $screen.Primary
        }
    } | Format-Table -AutoSize
}

function Test-VddPipe {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(500)
        return $pipe.IsConnected
    }
    catch {
        return $false
    }
    finally {
        $pipe.Dispose()
    }
}

function Send-VddCommand {
    param([Parameter(Mandatory = $true)][string]$Text)

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $payload = [System.Text.Encoding]::Unicode.GetBytes($Text)
        $pipe.Write($payload, 0, $payload.Length)
        $pipe.Flush()
    }
    finally {
        $pipe.Dispose()
    }
}

function Invoke-PnpUtil {
    param([string[]]$Arguments)

    $pnputil = Join-Path $env:SystemRoot 'System32\pnputil.exe'
    $process = Start-Process -FilePath $pnputil -ArgumentList $Arguments -Wait -NoNewWindow -PassThru
    if ($process.ExitCode -ne 0) {
        throw "pnputil failed with exit code $($process.ExitCode)."
    }
}

function Wait-ForPipe {
    $deadline = (Get-Date).AddSeconds(15)
    do {
        if (Test-VddPipe) { return $true }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Enable-Vdd {
    Assert-Administrator
    $devices = @(Get-VddDevices)
    if ($devices.Count -eq 0) {
        throw 'Virtual Display Driver was not found. Install VirtualDrivers/Virtual-Display-Driver first.'
    }

    foreach ($device in $devices) {
        Invoke-PnpUtil -Arguments @('/enable-device', $device.PNPDeviceID)
    }

    if (-not (Wait-ForPipe)) {
        throw 'The VDD was enabled, but its named pipe did not become available.'
    }

    Send-VddCommand -Text 'SETDISPLAYCOUNT 1'
    Start-Sleep -Seconds 2
}

function Disable-Vdd {
    Assert-Administrator
    $devices = @(Get-VddDevices)
    foreach ($device in $devices) {
        Invoke-PnpUtil -Arguments @('/disable-device', $device.PNPDeviceID)
    }
    Start-Sleep -Seconds 2
}

function Show-Status {
    $devices = @(Get-VddDevices)
    Write-Host "Installed: $($(if ($devices.Count -gt 0) { 'YES' } else { 'NO' }))"
    foreach ($device in $devices) {
        Write-Host "Device:    $($device.Name)"
        Write-Host "PNP ID:    $($device.PNPDeviceID)"
        Write-Host "Status:    $($device.Status)"
    }
    Write-Host "Pipe:      $($(if (Test-VddPipe) { 'AVAILABLE' } else { 'UNAVAILABLE' }))"
    Write-Host ''
    Show-Screens
}

if ($env:OS -ne 'Windows_NT') {
    throw 'VMU ALPHA is supported only on Windows.'
}

switch ($Command) {
    '--on'     { Enable-Vdd; Show-Status }
    '--off'    { Disable-Vdd; Show-Status }
    '--status' { Show-Status }
    '--list'   { Show-Screens }
    '--help'   { Write-Host 'Usage: vmu --on | --off | --status | --list | --help' }
}
