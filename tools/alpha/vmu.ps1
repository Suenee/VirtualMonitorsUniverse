[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('--on', '--off', '--status', '--list', '--help')]
    [string]$Command
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PipeName = 'MTTVirtualDisplayPipe'
$VddPnpPrefix = 'ROOT\\MTTVDD'
$TargetWidth = 1920
$TargetHeight = 1080
$TargetRefresh = 60

function Write-Section {
    param([string]$Title)
    Write-Host "`n=== $Title ==="
}

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
    if ($screens.Count -eq 0) {
        Write-Host 'No active Windows displays were reported.'
        return
    }

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
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None
    )

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
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,
        [int]$TimeoutMs = 5000
    )

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None
    )

    try {
        $pipe.Connect($TimeoutMs)
        if (-not $pipe.IsConnected) {
            throw 'Could not connect to the Virtual Display Driver named pipe.'
        }

        # The upstream VDD reads commands as wchar_t values, therefore UTF-16 LE is required.
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

    $process = Start-Process -FilePath "$env:SystemRoot\\System32\\pnputil.exe" `
        -ArgumentList $Arguments `
        -Wait `
        -NoNewWindow `
        -PassThru

    if ($process.ExitCode -ne 0) {
        throw "pnputil failed with exit code $($process.ExitCode)."
    }
}

function Wait-ForPipe {
    param([int]$TimeoutSeconds = 15)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-VddPipe) {
            return $true
        }
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
        Write-Host "Enabling VDD device: $($device.PNPDeviceID)"
        Invoke-PnpUtil -Arguments @('/enable-device', $device.PNPDeviceID)
    }

    if (-not (Wait-ForPipe)) {
        throw 'The VDD device was enabled, but its named pipe did not become available.'
    }

    # Current upstream command name verified against the driver source.
    Send-VddCommand -Text 'SETDISPLAYCOUNT 1'
    Start-Sleep -Seconds 2
}

function Disable-Vdd {
    Assert-Administrator
    $devices = @(Get-VddDevices)

    if ($devices.Count -eq 0) {
        Write-Host 'Virtual Display Driver is not installed. Nothing to disable.'
        return
    }

    foreach ($device in $devices) {
        Write-Host "Disabling VDD device: $($device.PNPDeviceID)"
        Invoke-PnpUtil -Arguments @('/disable-device', $device.PNPDeviceID)
    }

    Start-Sleep -Seconds 2
}

function Show-Status {
    Write-Section 'Virtual Display Driver'
    $devices = @(Get-VddDevices)
    if ($devices.Count -eq 0) {
        Write-Host 'Installed: NO'
    }
    else {
        Write-Host 'Installed: YES'
        foreach ($device in $devices) {
            Write-Host ("Device:    {0}" -f $device.Name)
            Write-Host ("PNP ID:    {0}" -f $device.PNPDeviceID)
            Write-Host ("Status:    {0}" -f $device.Status)
        }
    }

    Write-Host ("Pipe:      {0}" -f $(if (Test-VddPipe) { 'AVAILABLE' } else { 'UNAVAILABLE' }))

    Write-Section 'Active Windows displays'
    Show-Screens
}

function Show-Help {
    @'
Virtual Monitors Universe - ALPHA driver proof of concept

Usage:
  vmu --on       Enable the installed VDD and request one virtual display.
  vmu --off      Disable the VDD device so its virtual displays disappear.
  vmu --status   Show VDD state and active Windows displays.
  vmu --list     List active Windows displays.
  vmu --help     Show this help.

Important:
  --on and --off require an elevated terminal.
  The third-party Virtual Display Driver must already be installed.
  ALPHA targets one 1920x1080 @ 60 Hz virtual monitor. The upstream driver
  advertises this mode, but Windows may retain another previously selected mode;
  verify the final mode in Windows Display Settings during the ALPHA test.
'@ | Write-Host
}

if ($env:OS -ne 'Windows_NT') {
    throw 'VMU ALPHA is supported only on Windows.'
}

switch ($Command) {
    '--on' {
        Enable-Vdd
        Write-Host "Requested one virtual display ($TargetWidth x $TargetHeight @ $TargetRefresh Hz target)."
        Show-Status
    }
    '--off' {
        Disable-Vdd
        Write-Host 'Virtual Display Driver disabled.'
        Show-Status
    }
    '--status' {
        Show-Status
    }
    '--list' {
        Show-Screens
    }
    '--help' {
        Show-Help
    }
}
