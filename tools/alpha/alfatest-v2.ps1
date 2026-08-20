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

$script:SavedModes = @{}

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
        if ($answer -eq 'Y') {
            Write-Log "USER CONFIRMATION: YES - $Prompt" Green
            return $true
        }
        if ($answer -eq 'N') {
            Write-Log "USER CONFIRMATION: NO - $Prompt" Red
            return $false
        }
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
            Write-Log "Using cached ${Label}: $Path" DarkGray
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
    if ('Vmu.DisplayModeApi' -as [type]) { return }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Vmu
{
    public static class DisplayModeApi
    {
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

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int ENUM_REGISTRY_SETTINGS = -2;
        public const uint DM_POSITION = 0x00000020;
        public const uint DM_PELSWIDTH = 0x00080000;
        public const uint DM_PELSHEIGHT = 0x00100000;
        public const uint DM_DISPLAYFREQUENCY = 0x00400000;
        public const uint CDS_UPDATEREGISTRY = 0x00000001;
        public const int DISP_CHANGE_SUCCESSFUL = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);
    }
}
'@
}

function Get-Displays {
    Add-Type -AssemblyName System.Windows.Forms
    return @(
        [System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
            [pscustomobject]@{
                DeviceName = $_.DeviceName
                Primary = $_.Primary
                Bounds = $_.Bounds
            }
        }
    )
}

function Get-Mode {
    param([string]$DeviceName, [switch]$Registry)

    Ensure-DisplayApi
    $mode = New-Object Vmu.DisplayModeApi+DEVMODE
    $mode.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($mode)
    $modeIndex = if ($Registry) { [Vmu.DisplayModeApi]::ENUM_REGISTRY_SETTINGS } else { [Vmu.DisplayModeApi]::ENUM_CURRENT_SETTINGS }

    if (-not [Vmu.DisplayModeApi]::EnumDisplaySettings($DeviceName, $modeIndex, [ref]$mode)) {
        throw "Cannot read display mode for $DeviceName."
    }
    return $mode
}

function Test-Mode {
    param([string]$DeviceName, [uint32]$Width, [uint32]$Height)

    try {
        $mode = Get-Mode -DeviceName $DeviceName
        return ($mode.dmPelsWidth -eq $Width -and $mode.dmPelsHeight -eq $Height)
    }
    catch {
        return $false
    }
}

function Set-Mode {
    param([string]$DeviceName, [uint32]$Width, [uint32]$Height, [uint32]$RefreshRate)

    $mode = Get-Mode -DeviceName $DeviceName
    $mode.dmPelsWidth = $Width
    $mode.dmPelsHeight = $Height
    $mode.dmDisplayFrequency = $RefreshRate
    $mode.dmFields = [Vmu.DisplayModeApi]::DM_PELSWIDTH -bor [Vmu.DisplayModeApi]::DM_PELSHEIGHT -bor [Vmu.DisplayModeApi]::DM_DISPLAYFREQUENCY

    $result = [Vmu.DisplayModeApi]::ChangeDisplaySettingsEx($DeviceName, [ref]$mode, [IntPtr]::Zero, [Vmu.DisplayModeApi]::CDS_UPDATEREGISTRY, [IntPtr]::Zero)
    if ($result -ne [Vmu.DisplayModeApi]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows rejected ${Width}x${Height}@${RefreshRate} on $DeviceName (result $result)."
    }

    if (-not (Wait-Until -Description "$DeviceName mode ${Width}x${Height}" -TimeoutMs 5000 -Condition { Test-Mode $DeviceName $Width $Height })) {
        throw "Timed out waiting for ${Width}x${Height} on $DeviceName."
    }
}

function Test-DisplayAttached {
    param([string]$DeviceName, [bool]$Expected)

    $present = @((Get-Displays) | Where-Object { $_.DeviceName -eq $DeviceName }).Count -gt 0
    return ($present -eq $Expected)
}

function Disconnect-Display {
    param([string]$DeviceName)

    $current = Get-Mode -DeviceName $DeviceName
    $script:SavedModes[$DeviceName] = $current

    $mode = $current
    $mode.dmPelsWidth = 0
    $mode.dmPelsHeight = 0
    $mode.dmFields = [Vmu.DisplayModeApi]::DM_PELSWIDTH -bor [Vmu.DisplayModeApi]::DM_PELSHEIGHT

    $result = [Vmu.DisplayModeApi]::ChangeDisplaySettingsEx($DeviceName, [ref]$mode, [IntPtr]::Zero, [Vmu.DisplayModeApi]::CDS_UPDATEREGISTRY, [IntPtr]::Zero)
    if ($result -ne [Vmu.DisplayModeApi]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows rejected disconnect for $DeviceName (result $result)."
    }

    if (-not (Wait-Until -Description "$DeviceName disconnected from desktop" -TimeoutMs 5000 -Condition { Test-DisplayAttached $DeviceName $false })) {
        throw "Timed out waiting for $DeviceName to disconnect."
    }
}

function Reconnect-Display {
    param([string]$DeviceName)

    if ($script:SavedModes.ContainsKey($DeviceName)) {
        $mode = $script:SavedModes[$DeviceName]
    }
    else {
        $mode = Get-Mode -DeviceName $DeviceName -Registry
        if ($mode.dmPelsWidth -eq 0 -or $mode.dmPelsHeight -eq 0) {
            $mode.dmPelsWidth = 1920
            $mode.dmPelsHeight = 1080
            $mode.dmDisplayFrequency = 60
        }
    }

    $mode.dmFields = [Vmu.DisplayModeApi]::DM_POSITION -bor [Vmu.DisplayModeApi]::DM_PELSWIDTH -bor [Vmu.DisplayModeApi]::DM_PELSHEIGHT -bor [Vmu.DisplayModeApi]::DM_DISPLAYFREQUENCY
    $result = [Vmu.DisplayModeApi]::ChangeDisplaySettingsEx($DeviceName, [ref]$mode, [IntPtr]::Zero, [Vmu.DisplayModeApi]::CDS_UPDATEREGISTRY, [IntPtr]::Zero)
    if ($result -ne [Vmu.DisplayModeApi]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows rejected reconnect for $DeviceName (result $result)."
    }

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
        Write-Log 'Previous ALPHA test VDD remnants detected; removing them before the new test.' Yellow
        if (-not (Remove-VddInstallation)) {
            throw 'Could not establish clean baseline.'
        }
    }

    $baselineDisplays = @(Get-Displays)
    $baselineNames = @($baselineDisplays | ForEach-Object { $_.DeviceName })
    Write-Log ("Baseline Windows displays before VDD install: {0}" -f ($baselineNames -join ', '))

    if ($baselineNames.Count -eq 0) {
        throw 'Windows display enumeration returned zero physical/active displays before VDD installation.'
    }

    Install-Vdd

    $newDisplay = $null
    $found = Wait-Until -Description 'new Windows display created by VDD' -TimeoutMs 10000 -Condition {
        $current = @(Get-Displays)
        $new = @($current | Where-Object { $baselineNames -notcontains $_.DeviceName })
        if ($new.Count -eq 1) {
            $script:DetectedNewDisplay = $new[0]
            return $true
        }
        return $false
    }

    if (-not $found -or $null -eq $script:DetectedNewDisplay) {
        $currentNames = @((Get-Displays) | ForEach-Object { $_.DeviceName })
        Write-Log ("Windows displays after VDD install: {0}" -f ($currentNames -join ', ')) Yellow
        throw 'Could not identify exactly one new Windows display after VDD installation.'
    }

    $newDisplay = $script:DetectedNewDisplay
    $name = $newDisplay.DeviceName
    Write-Log "Virtual Windows display identified as: $name" Green
    $Results.Preflight = 'PASS'

    Write-Section 'TEST 1: DYNAMIC RESOLUTION'
    Set-Mode -DeviceName $name -Width 1920 -Height 1080 -RefreshRate 60
    Write-Log 'Set 1920x1080 @ 60 Hz.' Green

    Set-Mode -DeviceName $name -Width 3840 -Height 2160 -RefreshRate 60
    Write-Log 'Set 3840x2160 @ 60 Hz.' Green
    Open-DisplaySettings

    if (Ask-User 'Does the virtual monitor now show 3840x2160 and remain usable?') {
        $Results.DynamicResolution = 'PASS'
    }
    else {
        $Results.DynamicResolution = 'FAIL'
    }

    Set-Mode -DeviceName $name -Width 1920 -Height 1080 -RefreshRate 60
    Write-Log 'Returned to 1920x1080 @ 60 Hz.' Green

    Write-Section 'TEST 2: DISCONNECT / RECONNECT WITHOUT UNINSTALL'
    Disconnect-Display -DeviceName $name
    Open-DisplaySettings
    $disconnectOk = Ask-User 'Is the virtual monitor still known by Windows but disconnected from the desktop (not Extend/Clone)?'

    Reconnect-Display -DeviceName $name
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
