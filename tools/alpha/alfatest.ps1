[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LogPath = Join-Path $RepoRoot 'alfatest.log'
$InstallDir = 'C:\VirtualDisplayDriver'
$DriverVersion = '25.7.23'
$DriverUrl = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
$DriverSha256 = 'e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a'
$NefConVersion = '1.14.0'
$NefConUrl = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
$NefConSha256 = 'a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669'
$TempDir = Join-Path $env:TEMP 'VMU-AlphaTest'

$Results = [ordered]@{
    Preflight = 'NOT RUN'
    DynamicResolution = 'NOT RUN'
    DisconnectReconnect = 'NOT RUN'
    UninstallFirstAttempt = 'NOT RUN'
}

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    Write-Host $Message -ForegroundColor $Color
}

function Write-Section {
    param([Parameter(Mandatory = $true)][string]$Title)
    Write-Log ''
    Write-Log ('=' * 72) DarkGray
    Write-Log $Title Cyan
    Write-Log ('=' * 72) DarkGray
}

function Ask-User {
    param([Parameter(Mandatory = $true)][string]$Prompt)

    while ($true) {
        Write-Host ''
        $answer = Read-Host "$Prompt [Y/N]"
        switch ($answer.Trim().ToUpperInvariant()) {
            'Y' { Write-Log "USER CONFIRMATION: YES - $Prompt" Green; return $true }
            'N' { Write-Log "USER CONFIRMATION: NO - $Prompt" Red; return $false }
            default { Write-Host 'Please enter Y or N.' -ForegroundColor Yellow }
        }
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-AsAdministrator {
    if (Test-IsAdministrator) { return }

    Write-Host 'Administrator rights are required. Opening UAC prompt...' -ForegroundColor Yellow
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath)
    ) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $process.ExitCode
}

function Assert-Hash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path. Expected $Expected, got $actual."
    }
}

function Invoke-NativeProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    Write-Log ("RUN: {0} {1}" -f $FilePath, ($Arguments -join ' ')) DarkGray
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    Write-Log "EXIT CODE: $($process.ExitCode)" DarkGray

    if (-not $AllowFailure -and $process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode)."
    }

    return $process.ExitCode
}

function Get-VddDevices {
    $devices = @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object {
        $_.FriendlyName -eq 'Virtual Display Driver'
    })

    if ($devices.Count -eq 0) {
        $devices = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            $hardwareId = $null
            try {
                $hardwareId = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data
            }
            catch {
                $hardwareId = $null
            }
            $hardwareId -contains 'Root\MttVDD'
        })
    }

    return $devices
}

function Get-DriverInfPaths {
    param([object[]]$Devices)

    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($device in $Devices) {
        try {
            $property = Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction Stop
            if ($property.Data) {
                $paths.Add([string]$property.Data)
            }
        }
        catch {
            Write-Log "WARNING: Could not read driver INF path for $($device.InstanceId): $($_.Exception.Message)" Yellow
        }
    }

    return @($paths | Sort-Object -Unique)
}

function Remove-VddInstallation {
    param([switch]$RemoveConfig)

    $devices = @(Get-VddDevices)
    $infPaths = @(Get-DriverInfPaths -Devices $devices)

    Write-Log "VDD device nodes before removal: $($devices.Count)"
    foreach ($device in $devices) {
        Write-Log "Removing VDD device node: $($device.InstanceId) [$($device.Status)]"
        Invoke-NativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/remove-device', ('"{0}"' -f $device.InstanceId)) -AllowFailure | Out-Null
    }

    Start-Sleep -Seconds 2

    foreach ($infPath in $infPaths) {
        Write-Log "Removing VDD driver package: $infPath"
        Invoke-NativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/delete-driver', $infPath, '/uninstall', '/force') -AllowFailure | Out-Null
    }

    if ($RemoveConfig -and (Test-Path -LiteralPath $InstallDir)) {
        Write-Log "Removing VDD configuration directory: $InstallDir"
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Start-Sleep -Seconds 3
    return @(Get-VddDevices).Count -eq 0
}

function Install-Vdd {
    Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

    try {
        $driverZip = Join-Path $TempDir 'vdd.zip'
        $nefconZip = Join-Path $TempDir 'nefcon.zip'

        Write-Log "Downloading VDD $DriverVersion..." Cyan
        Invoke-WebRequest -Uri $DriverUrl -OutFile $driverZip -UseBasicParsing
        Assert-Hash -Path $driverZip -Expected $DriverSha256

        Write-Log "Downloading NefCon $NefConVersion..." Cyan
        Invoke-WebRequest -Uri $NefConUrl -OutFile $nefconZip -UseBasicParsing
        Assert-Hash -Path $nefconZip -Expected $NefConSha256

        $driverExtract = Join-Path $TempDir 'driver'
        $nefconExtract = Join-Path $TempDir 'nefcon'
        Expand-Archive -LiteralPath $driverZip -DestinationPath $driverExtract -Force
        Expand-Archive -LiteralPath $nefconZip -DestinationPath $nefconExtract -Force

        $driverSource = Join-Path $driverExtract 'VirtualDisplayDriver'
        $infPath = Join-Path $driverSource 'MttVDD.inf'
        $catPath = Join-Path $driverSource 'mttvdd.cat'
        $nefconExe = Join-Path $nefconExtract 'x64\nefconw.exe'

        foreach ($required in @($infPath, $catPath, $nefconExe)) {
            if (-not (Test-Path -LiteralPath $required)) {
                throw "Required installation file not found: $required"
            }
        }

        Write-Log 'Installing trusted publisher certificate from the signed driver catalog...' Cyan
        $catalogBytes = [System.IO.File]::ReadAllBytes($catPath)
        $certificates = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
        $certificates.Import($catalogBytes)

        $certDir = Join-Path $TempDir 'certificates'
        New-Item -ItemType Directory -Path $certDir -Force | Out-Null
        foreach ($certificate in $certificates) {
            $certPath = Join-Path $certDir ($certificate.Thumbprint + '.cer')
            [System.IO.File]::WriteAllBytes(
                $certPath,
                $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
            )
            Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
        }

        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Copy-Item -Path (Join-Path $driverSource '*') -Destination $InstallDir -Recurse -Force
        $installedInf = Join-Path $InstallDir 'MttVDD.inf'

        Write-Log 'Installing exactly one Virtual Display Driver device...' Cyan
        Invoke-NativeProcess -FilePath $nefconExe -Arguments @('install', ('"{0}"' -f $installedInf), 'Root\MttVDD') | Out-Null
        Start-Sleep -Seconds 8

        $devices = @(Get-VddDevices)
        if ($devices.Count -ne 1) {
            throw "Expected exactly one VDD device after installation, found $($devices.Count)."
        }

        Write-Log "PASS: exactly one VDD device is installed: $($devices[0].InstanceId)" Green
    }
    finally {
        Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-NativeDisplayApi {
    if ('Vmu.AlphaDisplayApi' -as [type]) { return }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Vmu
{
    public static class AlphaDisplayApi
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        public const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int ENUM_REGISTRY_SETTINGS = -2;
        public const uint DM_POSITION = 0x00000020;
        public const uint DM_PELSWIDTH = 0x00080000;
        public const uint DM_PELSHEIGHT = 0x00100000;
        public const uint DM_DISPLAYFREQUENCY = 0x00400000;
        public const uint CDS_UPDATEREGISTRY = 0x00000001;
        public const uint CDS_TEST = 0x00000002;
        public const int DISP_CHANGE_SUCCESSFUL = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);
    }
}
'@
}

function Get-GdiDisplays {
    Ensure-NativeDisplayApi
    $result = New-Object System.Collections.Generic.List[object]
    [uint32]$index = 0

    while ($true) {
        $device = New-Object Vmu.AlphaDisplayApi+DISPLAY_DEVICE
        $device.cb = [Runtime.InteropServices.Marshal]::SizeOf($device)
        if (-not [Vmu.AlphaDisplayApi]::EnumDisplayDevices($null, $index, [ref]$device, 0)) {
            break
        }

        $result.Add([pscustomobject]@{
            DeviceName = $device.DeviceName
            DeviceString = $device.DeviceString
            StateFlags = $device.StateFlags
            Attached = (($device.StateFlags -band [Vmu.AlphaDisplayApi]::DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) -ne 0)
            Primary = (($device.StateFlags -band [Vmu.AlphaDisplayApi]::DISPLAY_DEVICE_PRIMARY_DEVICE) -ne 0)
        })
        $index++
    }

    return @($result)
}

function Get-DisplayMode {
    param(
        [Parameter(Mandatory = $true)][string]$DeviceName,
        [switch]$Registry
    )

    Ensure-NativeDisplayApi
    $mode = New-Object Vmu.AlphaDisplayApi+DEVMODE
    $mode.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($mode)
    $modeNumber = if ($Registry) { [Vmu.AlphaDisplayApi]::ENUM_REGISTRY_SETTINGS } else { [Vmu.AlphaDisplayApi]::ENUM_CURRENT_SETTINGS }

    if (-not [Vmu.AlphaDisplayApi]::EnumDisplaySettings($DeviceName, $modeNumber, [ref]$mode)) {
        throw "Cannot read display mode for $DeviceName."
    }

    return $mode
}

function Set-DisplayMode {
    param(
        [Parameter(Mandatory = $true)][string]$DeviceName,
        [Parameter(Mandatory = $true)][uint32]$Width,
        [Parameter(Mandatory = $true)][uint32]$Height,
        [Parameter(Mandatory = $true)][uint32]$RefreshRate
    )

    $mode = Get-DisplayMode -DeviceName $DeviceName
    $mode.dmPelsWidth = $Width
    $mode.dmPelsHeight = $Height
    $mode.dmDisplayFrequency = $RefreshRate
    $mode.dmFields = [Vmu.AlphaDisplayApi]::DM_PELSWIDTH -bor [Vmu.AlphaDisplayApi]::DM_PELSHEIGHT -bor [Vmu.AlphaDisplayApi]::DM_DISPLAYFREQUENCY

    $testResult = [Vmu.AlphaDisplayApi]::ChangeDisplaySettingsEx($DeviceName, [ref]$mode, [IntPtr]::Zero, [Vmu.AlphaDisplayApi]::CDS_TEST, [IntPtr]::Zero)
    if ($testResult -ne [Vmu.AlphaDisplayApi]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows rejected test mode ${Width}x${Height} @ ${RefreshRate} Hz for $DeviceName (result $testResult)."
    }

    $result = [Vmu.AlphaDisplayApi]::ChangeDisplaySettingsEx($DeviceName, [ref]$mode, [IntPtr]::Zero, [Vmu.AlphaDisplayApi]::CDS_UPDATEREGISTRY, [IntPtr]::Zero)
    if ($result -ne [Vmu.AlphaDisplayApi]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows rejected ${Width}x${Height} @ ${RefreshRate} Hz for $DeviceName (result $result)."
    }

    Start-Sleep -Seconds 3
    $actual = Get-DisplayMode -DeviceName $DeviceName
    if ($actual.dmPelsWidth -ne $Width -or $actual.dmPelsHeight -ne $Height) {
        throw "Mode verification failed on $DeviceName. Expected ${Width}x${Height}, got $($actual.dmPelsWidth)x$($actual.dmPelsHeight)."
    }

    Write-Log "PASS: $DeviceName is ${Width}x${Height} @ $($actual.dmDisplayFrequency) Hz." Green
}

function Detach-Display {
    param([Parameter(Mandatory = $true)][string]$DeviceName)

    $mode = Get-DisplayMode -DeviceName $DeviceName
    $mode.dmPelsWidth = 0
    $mode.dmPelsHeight = 0
    $mode.dmPositionX = 0
    $mode.dmPositionY = 0
    $mode.dmFields = [Vmu.AlphaDisplayApi]::DM_POSITION -bor [Vmu.AlphaDisplayApi]::DM_PELSWIDTH -bor [Vmu.AlphaDisplayApi]::DM_PELSHEIGHT

    $result = [Vmu.AlphaDisplayApi]::ChangeDisplaySettingsEx($DeviceName, [ref]$mode, [IntPtr]::Zero, [Vmu.AlphaDisplayApi]::CDS_UPDATEREGISTRY, [IntPtr]::Zero)
    if ($result -ne [Vmu.AlphaDisplayApi]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows failed to detach $DeviceName (result $result)."
    }

    Start-Sleep -Seconds 3
}

function Reconnect-Display {
    param(
        [Parameter(Mandatory = $true)][string]$DeviceName,
        [Parameter(Mandatory = $true)]$SavedMode
    )

    $SavedMode.dmFields = [Vmu.AlphaDisplayApi]::DM_POSITION -bor [Vmu.AlphaDisplayApi]::DM_PELSWIDTH -bor [Vmu.AlphaDisplayApi]::DM_PELSHEIGHT -bor [Vmu.AlphaDisplayApi]::DM_DISPLAYFREQUENCY
    $result = [Vmu.AlphaDisplayApi]::ChangeDisplaySettingsEx($DeviceName, [ref]$SavedMode, [IntPtr]::Zero, [Vmu.AlphaDisplayApi]::CDS_UPDATEREGISTRY, [IntPtr]::Zero)
    if ($result -ne [Vmu.AlphaDisplayApi]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows failed to reconnect $DeviceName (result $result)."
    }

    Start-Sleep -Seconds 3
}

function Get-NewAttachedDisplay {
    param([string[]]$BaselineNames)

    $newDisplays = @(Get-GdiDisplays | Where-Object {
        $_.Attached -and $_.DeviceName -notin $BaselineNames
    })

    if ($newDisplays.Count -ne 1) {
        Write-Log "Expected exactly one newly attached display, found $($newDisplays.Count)." Red
        foreach ($display in (Get-GdiDisplays)) {
            Write-Log ("DISPLAY: {0} | {1} | attached={2} | primary={3}" -f $display.DeviceName, $display.DeviceString, $display.Attached, $display.Primary) DarkGray
        }
        throw 'Unable to identify the single VMU test display.'
    }

    return $newDisplays[0]
}

function Open-DisplaySettings {
    try {
        Start-Process 'ms-settings:display' | Out-Null
    }
    catch {
        Write-Log "WARNING: Could not open Windows Display Settings: $($_.Exception.Message)" Yellow
    }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'VMU ALPHA test supports Windows only.'
}

Restart-AsAdministrator

Set-Content -LiteralPath $LogPath -Value '' -Encoding UTF8
Write-Log 'Virtual Monitors Universe - ALPHA acceptance test' Cyan
Write-Log "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Log "Computer: $env:COMPUTERNAME"
Write-Log "Windows: $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"
Write-Log "PowerShell: $($PSVersionTable.PSVersion)"

$virtualDeviceName = $null
$baselineNames = @()
$testCanContinue = $true

try {
    Write-Section 'PRE-FLIGHT: CLEAN BASELINE AND ONE FRESH VDD'

    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    foreach ($device in $existing) {
        Write-Log "EXISTING: $($device.InstanceId) | status=$($device.Status)"
    }

    if ($existing.Count -gt 0) {
        Write-Log 'Removing all existing VDD device nodes/packages to obtain a deterministic clean baseline...' Yellow
        if (-not (Remove-VddInstallation -RemoveConfig)) {
            throw 'Pre-flight cleanup failed: VDD device nodes are still present.'
        }
    }

    $baselineDisplays = @(Get-GdiDisplays | Where-Object { $_.Attached })
    $baselineNames = @($baselineDisplays | ForEach-Object { $_.DeviceName })
    Write-Log "Attached physical/baseline displays: $($baselineNames.Count)"

    Install-Vdd
    $newDisplay = Get-NewAttachedDisplay -BaselineNames $baselineNames
    $virtualDeviceName = $newDisplay.DeviceName
    Write-Log "VMU test display identified as $virtualDeviceName ($($newDisplay.DeviceString))." Green
    $Results.Preflight = 'PASS'
}
catch {
    $Results.Preflight = 'FAIL'
    $testCanContinue = $false
    Write-Log "PRE-FLIGHT FAILED: $($_.Exception.Message)" Red
}

if ($testCanContinue) {
    Write-Section 'TEST 1: DYNAMIC RESOLUTION SWITCHING'
    try {
        Set-DisplayMode -DeviceName $virtualDeviceName -Width 1920 -Height 1080 -RefreshRate 60
        Set-DisplayMode -DeviceName $virtualDeviceName -Width 3840 -Height 2160 -RefreshRate 60
        Set-DisplayMode -DeviceName $virtualDeviceName -Width 1920 -Height 1080 -RefreshRate 60

        Open-DisplaySettings
        $manualOk = Ask-User 'Confirm that the SAME virtual monitor survived FullHD -> 4K -> FullHD and is now 1920x1080'
        if ($manualOk) {
            $Results.DynamicResolution = 'PASS'
        }
        else {
            $Results.DynamicResolution = 'FAIL (USER)'
        }
    }
    catch {
        $Results.DynamicResolution = 'FAIL'
        Write-Log "TEST 1 FAILED: $($_.Exception.Message)" Red
    }

    Write-Section 'TEST 2: DISCONNECT WITHOUT UNINSTALL + RECONNECT'
    try {
        $savedMode = Get-DisplayMode -DeviceName $virtualDeviceName
        Write-Log ("Saved mode before detach: {0}x{1} @ {2} Hz, position {3},{4}" -f $savedMode.dmPelsWidth, $savedMode.dmPelsHeight, $savedMode.dmDisplayFrequency, $savedMode.dmPositionX, $savedMode.dmPositionY)

        Detach-Display -DeviceName $virtualDeviceName

        $pnpCountWhileDetached = @(Get-VddDevices).Count
        $gdiDetached = Get-GdiDisplays | Where-Object { $_.DeviceName -eq $virtualDeviceName } | Select-Object -First 1
        Write-Log "PnP VDD devices while detached: $pnpCountWhileDetached"
        if ($gdiDetached) {
            Write-Log "GDI display while detached: present=yes attached=$($gdiDetached.Attached)"
        }
        else {
            Write-Log 'GDI display while detached: present=no' Yellow
        }

        if ($pnpCountWhileDetached -lt 1) {
            throw 'The VDD device disappeared from PnP while only a desktop detach was requested.'
        }
        if ($gdiDetached -and $gdiDetached.Attached) {
            throw 'The virtual display is still attached to the desktop after detach.'
        }

        Open-DisplaySettings
        $manualDetached = Ask-User 'Confirm that the virtual monitor still exists in Windows but is disconnected from the desktop (not Extend/Clone)'

        Reconnect-Display -DeviceName $virtualDeviceName -SavedMode $savedMode
        $afterReconnect = Get-GdiDisplays | Where-Object { $_.DeviceName -eq $virtualDeviceName -and $_.Attached } | Select-Object -First 1
        if (-not $afterReconnect) {
            throw 'The virtual display did not reattach to the desktop.'
        }

        Open-DisplaySettings
        $manualReconnected = Ask-User 'Confirm that the same virtual monitor is connected again and usable as an extended desktop'

        if ($manualDetached -and $manualReconnected) {
            $Results.DisconnectReconnect = 'PASS'
        }
        else {
            $Results.DisconnectReconnect = 'FAIL (USER)'
        }
    }
    catch {
        $Results.DisconnectReconnect = 'FAIL'
        Write-Log "TEST 2 FAILED: $($_.Exception.Message)" Red
    }
}

Write-Section 'TEST 3: ONE-SHOT UNINSTALL'
try {
    $devicesBeforeUninstall = @(Get-VddDevices)
    Write-Log "VDD device nodes immediately before uninstall: $($devicesBeforeUninstall.Count)"

    $uninstallOk = Remove-VddInstallation -RemoveConfig
    $devicesAfterUninstall = @(Get-VddDevices)
    Write-Log "VDD device nodes immediately after ONE uninstall call: $($devicesAfterUninstall.Count)"

    $virtualStillAttached = $false
    if ($virtualDeviceName) {
        $virtualStillAttached = [bool](Get-GdiDisplays | Where-Object { $_.DeviceName -eq $virtualDeviceName -and $_.Attached } | Select-Object -First 1)
    }
    Write-Log "Previously identified virtual display still attached: $virtualStillAttached"

    Open-DisplaySettings
    $manualUninstall = Ask-User 'Confirm that no VMU/VDD virtual monitor remains in Windows Display Settings after this single uninstall attempt'

    if ($uninstallOk -and $devicesAfterUninstall.Count -eq 0 -and -not $virtualStillAttached -and $manualUninstall) {
        $Results.UninstallFirstAttempt = 'PASS'
    }
    else {
        $Results.UninstallFirstAttempt = 'FAIL'
    }
}
catch {
    $Results.UninstallFirstAttempt = 'FAIL'
    Write-Log "TEST 3 FAILED: $($_.Exception.Message)" Red
}

Write-Section 'FINAL RESULT'
$failed = $false
foreach ($entry in $Results.GetEnumerator()) {
    Write-Log ("{0}: {1}" -f $entry.Key, $entry.Value) $(if ($entry.Value -eq 'PASS') { 'Green' } else { 'Red' })
    if ($entry.Value -ne 'PASS') { $failed = $true }
}

Write-Log ''
Write-Log "Finished: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Log "Log file: $LogPath" Cyan
Write-Host ''
Write-Host 'Please send alfatest.log back for analysis.' -ForegroundColor Cyan

if ($failed) { exit 1 }
exit 0
