[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LogPath = Join-Path $RepoRoot 'alfatest.log'
$RuntimeRoot = Join-Path $RepoRoot '.runtime\alpha'
$CacheDir = Join-Path $RuntimeRoot 'cache'
$DriverSourceDir = Join-Path $RuntimeRoot 'vdd-source'
$WorkDir = Join-Path $RuntimeRoot 'work'
$CertificateStateFile = Join-Path $RuntimeRoot 'added-certificates.txt'

$DriverVersion = '25.7.23'
$DriverUrl = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
$DriverSha256 = 'e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a'
$NefConVersion = '1.14.0'
$NefConUrl = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
$NefConSha256 = 'a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669'

$Results = [ordered]@{
    Preflight = 'NOT RUN'
    DynamicResolution = 'NOT RUN'
    DisconnectReconnect = 'NOT RUN'
    UninstallFirstAttempt = 'NOT RUN'
}

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Message,
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
        $answer = (Read-Host "$Prompt [Y/N]").Trim().ToUpperInvariant()
        if ($answer -eq 'Y') { Write-Log "USER CONFIRMATION: YES - $Prompt" Green; return $true }
        if ($answer -eq 'N') { Write-Log "USER CONFIRMATION: NO - $Prompt" Red; return $false }
        Write-Host 'Please enter Y or N.' -ForegroundColor Yellow
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
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath)) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -Wait -PassThru
    exit $process.ExitCode
}

function Assert-Hash {
    param([string]$Path, [string]$Expected)

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path."
    }
}

function Invoke-NativeProcess {
    param([string]$FilePath, [string[]]$Arguments, [switch]$AllowFailure)

    Write-Log ("RUN: {0} {1}" -f $FilePath, ($Arguments -join ' ')) DarkGray
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    Write-Log "EXIT CODE: $($process.ExitCode)" DarkGray

    if (-not $AllowFailure -and $process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode)."
    }

    return $process.ExitCode
}

function Wait-Until {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][string]$Description,
        [int]$TimeoutMs = 10000,
        [int]$PollMs = 100
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) {
            $stopwatch.Stop()
            Write-Log ("READY after {0} ms: {1}" -f $stopwatch.ElapsedMilliseconds, $Description) DarkGray
            return $true
        }
        Start-Sleep -Milliseconds $PollMs
    } while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMs)

    $stopwatch.Stop()
    Write-Log ("TIMEOUT after {0} ms: {1}" -f $stopwatch.ElapsedMilliseconds, $Description) Yellow
    return $false
}

function Get-VddDevices {
    # Deliberately query only the Display class. Never scan every PnP device.
    return @(
        Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' }
    )
}

function Get-DriverInfPaths {
    param([object[]]$Devices)

    $result = @()
    foreach ($device in $Devices) {
        try {
            $value = (Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction Stop).Data
            if ($value) { $result += [string]$value }
        }
        catch {
            Write-Log "WARNING: Cannot read INF path for $($device.InstanceId): $($_.Exception.Message)" Yellow
        }
    }
    return @($result | Sort-Object -Unique)
}

function Remove-RecordedCertificates {
    if (-not (Test-Path -LiteralPath $CertificateStateFile)) { return }

    foreach ($thumbprint in @(Get-Content -LiteralPath $CertificateStateFile -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($thumbprint)) { continue }
        $certPath = "Cert:\LocalMachine\TrustedPublisher\$thumbprint"
        if (Test-Path -LiteralPath $certPath) {
            Write-Log "Removing certificate added by this VMU ALPHA run: $thumbprint"
            Remove-Item -LiteralPath $certPath -Force
        }
    }
    Remove-Item -LiteralPath $CertificateStateFile -Force -ErrorAction SilentlyContinue
}

function Remove-VddInstallation {
    $devices = @(Get-VddDevices)
    $infPaths = @(Get-DriverInfPaths -Devices $devices)
    Write-Log "VDD device nodes before removal: $($devices.Count)"

    foreach ($device in $devices) {
        Write-Log "Removing VDD device node: $($device.InstanceId) [$($device.Status)]"
        Invoke-NativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/remove-device', ('"{0}"' -f $device.InstanceId)) -AllowFailure | Out-Null
    }

    if (-not (Wait-Until -Description 'all VDD device nodes removed' -TimeoutMs 5000 -Condition { @(Get-VddDevices).Count -eq 0 })) {
        return $false
    }

    foreach ($infPath in $infPaths) {
        Write-Log "Removing VDD driver package: $infPath"
        Invoke-NativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/delete-driver', $infPath, '/uninstall', '/force') -AllowFailure | Out-Null
    }

    Remove-RecordedCertificates
    return (@(Get-VddDevices).Count -eq 0)
}

function Get-CachedPayload {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Sha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (Test-Path -LiteralPath $Path) {
        try {
            Assert-Hash -Path $Path -Expected $Sha256
            Write-Log "Using cached $Label: $Path" DarkGray
            return
        }
        catch {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Log "Downloading $Label into repository runtime..." Cyan
    Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    Assert-Hash -Path $Path -Expected $Sha256
}

function Install-Vdd {
    New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $DriverSourceDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

    $driverZip = Join-Path $CacheDir "vdd-$DriverVersion.zip"
    $nefconZip = Join-Path $CacheDir "nefcon-$NefConVersion.zip"
    Write-Log "Runtime root: $RuntimeRoot"

    Get-CachedPayload -Url $DriverUrl -Path $driverZip -Sha256 $DriverSha256 -Label "VDD $DriverVersion"
    Get-CachedPayload -Url $NefConUrl -Path $nefconZip -Sha256 $NefConSha256 -Label "NefCon $NefConVersion"

    $driverExtract = Join-Path $WorkDir 'driver'
    $nefconExtract = Join-Path $WorkDir 'nefcon'
    Expand-Archive -LiteralPath $driverZip -DestinationPath $driverExtract -Force
    Expand-Archive -LiteralPath $nefconZip -DestinationPath $nefconExtract -Force

    $driverSource = Join-Path $driverExtract 'VirtualDisplayDriver'
    Copy-Item -LiteralPath $driverSource -Destination $DriverSourceDir -Recurse -Force
    $infPath = Join-Path $DriverSourceDir 'MttVDD.inf'
    $catPath = Join-Path $DriverSourceDir 'mttvdd.cat'
    $nefconExe = Join-Path $nefconExtract 'x64\nefconw.exe'

    foreach ($required in @($infPath, $catPath, $nefconExe)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Required installation file not found: $required"
        }
    }

    $catalogBytes = [System.IO.File]::ReadAllBytes($catPath)
    $certificates = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    $certificates.Import($catalogBytes)
    $added = @()

    foreach ($certificate in $certificates) {
        $existing = Get-ChildItem 'Cert:\LocalMachine\TrustedPublisher' | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } | Select-Object -First 1
        if (-not $existing) {
            $certPath = Join-Path $WorkDir ($certificate.Thumbprint + '.cer')
            [System.IO.File]::WriteAllBytes($certPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
            $added += $certificate.Thumbprint
        }
    }
    Set-Content -LiteralPath $CertificateStateFile -Value $added -Encoding ASCII

    Write-Log 'Installing exactly one Virtual Display Driver device...' Cyan
    Invoke-NativeProcess -FilePath $nefconExe -Arguments @('install', ('"{0}"' -f $infPath), 'Root\MttVDD') | Out-Null

    if (-not (Wait-Until -Description 'exactly one VDD device available' -TimeoutMs 10000 -Condition { @(Get-VddDevices).Count -eq 1 })) {
        throw "Expected exactly one VDD device after installation, found $(@(Get-VddDevices).Count)."
    }

    $devices = @(Get-VddDevices)
    Write-Log "PASS: one VDD device installed: $($devices[0].InstanceId)" Green
}

function Ensure-DisplayApi {
    if ('Vmu.AlphaDisplayApi2' -as [type]) { return }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Vmu {
 public static class AlphaDisplayApi2 {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string DeviceKey; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string dmDeviceName; public ushort dmSpecVersion,dmDriverVersion,dmSize,dmDriverExtra; public uint dmFields; public int dmPositionX,dmPositionY; public uint dmDisplayOrientation,dmDisplayFixedOutput; public short dmColor,dmDuplex,dmYResolution,dmTTOption,dmCollate; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string dmFormName; public ushort dmLogPixels; public uint dmBitsPerPel,dmPelsWidth,dmPelsHeight,dmDisplayFlags,dmDisplayFrequency,dmICMMethod,dmICMIntent,dmMediaType,dmDitherType,dmReserved1,dmReserved2,dmPanningWidth,dmPanningHeight; }
  public const int ATTACHED=1, ENUM_CURRENT=-1, ENUM_REGISTRY=-2, SUCCESS=0; public const uint DM_POSITION=0x20,DM_WIDTH=0x80000,DM_HEIGHT=0x100000,DM_FREQ=0x400000,CDS_UPDATE=1;
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool EnumDisplayDevices(string a,uint b,ref DISPLAY_DEVICE c,uint d);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool EnumDisplaySettings(string a,int b,ref DEVMODE c);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string a,ref DEVMODE b,IntPtr c,uint d,IntPtr e);
 }
}
'@
}

function Get-Displays {
    Ensure-DisplayApi
    $items = @()
    [uint32]$i = 0

    while ($true) {
        $d = New-Object Vmu.AlphaDisplayApi2+DISPLAY_DEVICE
        $d.cb = [Runtime.InteropServices.Marshal]::SizeOf($d)
        if (-not [Vmu.AlphaDisplayApi2]::EnumDisplayDevices($null, $i, [ref]$d, 0)) { break }
        $items += [pscustomobject]@{
            DeviceName = $d.DeviceName
            DeviceString = $d.DeviceString
            Attached = (($d.StateFlags -band 1) -ne 0)
        }
        $i++
    }
    return $items
}

function Get-Mode {
    param([string]$DeviceName, [switch]$Registry)

    Ensure-DisplayApi
    $m = New-Object Vmu.AlphaDisplayApi2+DEVMODE
    $m.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($m)
    $index = if ($Registry) { -2 } else { -1 }
    if (-not [Vmu.AlphaDisplayApi2]::EnumDisplaySettings($DeviceName, $index, [ref]$m)) {
        throw "Cannot read mode for $DeviceName"
    }
    return $m
}

function Test-Mode {
    param([string]$DeviceName, [uint32]$Width, [uint32]$Height)

    try {
        $m = Get-Mode $DeviceName
        return ($m.dmPelsWidth -eq $Width -and $m.dmPelsHeight -eq $Height)
    }
    catch {
        return $false
    }
}

function Set-Mode {
    param([string]$DeviceName, [uint32]$Width, [uint32]$Height, [uint32]$RefreshRate)

    $m = Get-Mode $DeviceName
    $m.dmPelsWidth = $Width
    $m.dmPelsHeight = $Height
    $m.dmDisplayFrequency = $RefreshRate
    $m.dmFields = 0x80000 -bor 0x100000 -bor 0x400000
    $r = [Vmu.AlphaDisplayApi2]::ChangeDisplaySettingsEx($DeviceName, [ref]$m, [IntPtr]::Zero, 1, [IntPtr]::Zero)
    if ($r -ne 0) { throw "Windows rejected ${Width}x${Height}@${RefreshRate} on $DeviceName (result $r)." }

    if (-not (Wait-Until -Description "$DeviceName mode ${Width}x${Height}" -TimeoutMs 5000 -Condition { Test-Mode $DeviceName $Width $Height })) {
        throw "Timed out waiting for ${Width}x${Height} on $DeviceName."
    }
}

function Test-DisplayAttached {
    param([string]$DeviceName, [bool]$Expected)

    $display = @(Get-Displays | Where-Object { $_.DeviceName -eq $DeviceName } | Select-Object -First 1)
    if ($display.Count -eq 0) { return $false }
    return ($display[0].Attached -eq $Expected)
}

function Disconnect-Display {
    param([string]$DeviceName)

    $m = Get-Mode $DeviceName
    $m.dmPelsWidth = 0
    $m.dmPelsHeight = 0
    $m.dmFields = 0x20 -bor 0x80000 -bor 0x100000
    $r = [Vmu.AlphaDisplayApi2]::ChangeDisplaySettingsEx($DeviceName, [ref]$m, [IntPtr]::Zero, 1, [IntPtr]::Zero)
    if ($r -ne 0) { throw "Windows rejected disconnect for $DeviceName (result $r)." }

    if (-not (Wait-Until -Description "$DeviceName disconnected from desktop" -TimeoutMs 5000 -Condition { Test-DisplayAttached $DeviceName $false })) {
        throw "Timed out waiting for $DeviceName to disconnect."
    }
}

function Reconnect-Display {
    param([string]$DeviceName)

    $m = Get-Mode $DeviceName -Registry
    if ($m.dmPelsWidth -eq 0 -or $m.dmPelsHeight -eq 0) {
        $m.dmPelsWidth = 1920
        $m.dmPelsHeight = 1080
        $m.dmDisplayFrequency = 60
    }
    $m.dmFields = 0x20 -bor 0x80000 -bor 0x100000 -bor 0x400000
    $r = [Vmu.AlphaDisplayApi2]::ChangeDisplaySettingsEx($DeviceName, [ref]$m, [IntPtr]::Zero, 1, [IntPtr]::Zero)
    if ($r -ne 0) { throw "Windows rejected reconnect for $DeviceName (result $r)." }

    if (-not (Wait-Until -Description "$DeviceName reconnected to desktop" -TimeoutMs 5000 -Condition { Test-DisplayAttached $DeviceName $true })) {
        throw "Timed out waiting for $DeviceName to reconnect."
    }
}

function Open-DisplaySettings {
    Start-Process 'ms-settings:display' | Out-Null
}

Restart-AsAdministrator
Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null
Write-Log 'Virtual Monitors Universe - ALPHA acceptance test'
Write-Log "Repository runtime: $RuntimeRoot"
Write-Log "Computer: $env:COMPUTERNAME"
Write-Log "Windows: $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"
Write-Log "PowerShell: $($PSVersionTable.PSVersion)"

try {
    Write-Section 'PRE-FLIGHT: CLEAN BASELINE AND ONE FRESH VDD'
    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    if ($existing.Count -gt 0) {
        if (-not (Remove-VddInstallation)) { throw 'Could not establish clean baseline.' }
    }

    Install-Vdd
    $Results.Preflight = 'PASS'

    $virtual = @(Get-Displays | Where-Object { $_.DeviceString -match 'Virtual|MTT|VDD' }) | Select-Object -Last 1
    if (-not $virtual) { throw 'Could not identify the virtual Windows display.' }
    $name = $virtual.DeviceName
    Write-Log "Virtual Windows display: $name / $($virtual.DeviceString)"

    Write-Section 'TEST 1: DYNAMIC RESOLUTION'
    Set-Mode $name 1920 1080 60
    Write-Log 'Set 1920x1080 @ 60 Hz.' Green
    Set-Mode $name 3840 2160 60
    Write-Log 'Set 3840x2160 @ 60 Hz.' Green
    Open-DisplaySettings
    if (Ask-User 'Does the virtual monitor now show 3840x2160 and remain usable?') {
        $Results.DynamicResolution = 'PASS'
    }
    else {
        $Results.DynamicResolution = 'FAIL'
    }
    Set-Mode $name 1920 1080 60
    Write-Log 'Returned to 1920x1080 @ 60 Hz.' Green

    Write-Section 'TEST 2: DISCONNECT / RECONNECT WITHOUT UNINSTALL'
    Disconnect-Display $name
    Open-DisplaySettings
    $disconnectOk = Ask-User 'Is the virtual monitor still known by Windows but disconnected from the desktop (not Extend/Clone)?'
    Reconnect-Display $name
    Open-DisplaySettings
    $reconnectOk = Ask-User 'Was the same virtual monitor reconnected without reinstalling the driver?'
    if ($disconnectOk -and $reconnectOk) {
        $Results.DisconnectReconnect = 'PASS'
    }
    else {
        $Results.DisconnectReconnect = 'FAIL'
    }

    Write-Section 'TEST 3: ONE-SHOT UNINSTALL'
    $before = @(Get-VddDevices)
    Write-Log "VDD device nodes immediately before uninstall: $($before.Count)"
    $uninstallOk = Remove-VddInstallation
    Open-DisplaySettings
    $userOk = Ask-User 'After one uninstall attempt, is the virtual monitor completely gone from Windows display settings?'
    if ($uninstallOk -and $userOk) {
        $Results.UninstallFirstAttempt = 'PASS'
    }
    else {
        $Results.UninstallFirstAttempt = 'FAIL'
    }
}
catch {
    Write-Log "TEST ERROR: $($_.Exception.Message)" Red
}
finally {
    Write-Section 'FINAL RESULT'
    foreach ($entry in $Results.GetEnumerator()) {
        $color = if ($entry.Value -eq 'PASS') { 'Green' } elseif ($entry.Value -eq 'FAIL') { 'Red' } else { 'Yellow' }
        Write-Log ("{0}: {1}" -f $entry.Key, $entry.Value) $color
    }
    Write-Log "Log file: $LogPath"
    Write-Log 'Development payload remains only under the repository .runtime directory.'
}

if ($Results.Values -contains 'FAIL' -or $Results.Values -contains 'NOT RUN') { exit 1 }
exit 0
