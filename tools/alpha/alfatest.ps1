[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RunnerVersion = 'standalone-setdisplayconfig-v2-adjacency'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LogPath = Join-Path $RepoRoot 'alfatest.log'
$RuntimeRoot = Join-Path $RepoRoot '.runtime\alpha'
$CacheDir = Join-Path $RuntimeRoot 'cache'
$DriverSourceDir = Join-Path $RuntimeRoot 'vdd-source'
$WorkDir = Join-Path $RuntimeRoot 'work'
$CertificateStateFile = Join-Path $RuntimeRoot 'added-certificates.txt'
$TopologyHelper = Join-Path $PSScriptRoot 'displayconfig-topology.ps1'

$DriverVersion = '25.7.23'
$DriverUrl = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
$DriverSha256 = 'e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a'
$NefConVersion = '1.14.0'
$NefConUrl = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
$NefConSha256 = 'a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669'
$MinimumAdjacencyPixels = 64

$Results = [ordered]@{
    Preflight = 'NOT RUN'
    DynamicResolution = 'NOT RUN'
    TopologyAdjacency = 'NOT RUN'
    DisconnectReconnect = 'NOT RUN'
    UninstallFirstAttempt = 'NOT RUN'
}

function Write-Log {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Message,[ConsoleColor]$Color=[ConsoleColor]::Gray)
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
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $PSCommandPath)) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -Wait -PassThru
    exit $process.ExitCode
}

function Assert-Hash {
    param([string]$Path,[string]$Expected)
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) { throw "SHA-256 mismatch for $Path." }
}

function Invoke-NativeProcess {
    param([string]$FilePath,[string[]]$Arguments,[switch]$AllowFailure)
    Write-Log ("RUN: {0} {1}" -f $FilePath,($Arguments -join ' ')) DarkGray
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    Write-Log "EXIT CODE: $($process.ExitCode)" DarkGray
    if (-not $AllowFailure -and $process.ExitCode -ne 0) { throw "$FilePath failed with exit code $($process.ExitCode)." }
    return $process.ExitCode
}

function Wait-Until {
    param([Parameter(Mandatory = $true)][scriptblock]$Condition,[Parameter(Mandatory = $true)][string]$Description,[int]$TimeoutMs=10000,[int]$PollMs=100)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { $sw.Stop(); Write-Log ("READY after {0} ms: {1}" -f $sw.ElapsedMilliseconds,$Description) DarkGray; return $true }
        Start-Sleep -Milliseconds $PollMs
    } while ($sw.ElapsedMilliseconds -lt $TimeoutMs)
    $sw.Stop(); Write-Log ("TIMEOUT after {0} ms: {1}" -f $sw.ElapsedMilliseconds,$Description) Yellow
    return $false
}

function Get-VddDevices {
    return @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' })
}

function Get-DriverInfPaths {
    param([object[]]$Devices)
    $result=@()
    foreach($device in $Devices){
        try { $v=(Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction Stop).Data; if($v){$result += [string]$v} }
        catch { Write-Log "WARNING: Cannot read INF path for $($device.InstanceId): $($_.Exception.Message)" Yellow }
    }
    return @($result | Sort-Object -Unique)
}

function Remove-RecordedCertificates {
    if (-not (Test-Path -LiteralPath $CertificateStateFile)) { return }
    foreach($thumbprint in @(Get-Content -LiteralPath $CertificateStateFile -ErrorAction SilentlyContinue)){
        if([string]::IsNullOrWhiteSpace($thumbprint)){continue}
        $certPath="Cert:\LocalMachine\TrustedPublisher\$thumbprint"
        if(Test-Path -LiteralPath $certPath){ Write-Log "Removing certificate added by this VMU ALPHA run: $thumbprint"; Remove-Item -LiteralPath $certPath -Force }
    }
    Remove-Item -LiteralPath $CertificateStateFile -Force -ErrorAction SilentlyContinue
}

function Remove-VddInstallation {
    $devices=@(Get-VddDevices)
    $infPaths=@(Get-DriverInfPaths -Devices $devices)
    Write-Log "VDD device nodes before removal: $($devices.Count)"
    foreach($device in $devices){
        Write-Log "Removing VDD device node: $($device.InstanceId) [$($device.Status)]"
        Invoke-NativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/remove-device',('"{0}"' -f $device.InstanceId)) -AllowFailure | Out-Null
    }
    if(-not (Wait-Until -Description 'all VDD device nodes removed' -TimeoutMs 5000 -Condition { @(Get-VddDevices).Count -eq 0 })){ return $false }
    foreach($infPath in $infPaths){
        Write-Log "Removing VDD driver package: $infPath"
        Invoke-NativeProcess -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/delete-driver',$infPath,'/uninstall','/force') -AllowFailure | Out-Null
    }
    Remove-RecordedCertificates
    return (@(Get-VddDevices).Count -eq 0)
}

function Get-CachedPayload {
    param([string]$Url,[string]$Path,[string]$Sha256,[string]$Label)
    if(Test-Path -LiteralPath $Path){
        try { Assert-Hash -Path $Path -Expected $Sha256; Write-Log "Using cached ${Label}: $Path" DarkGray; return }
        catch { Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue }
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
    $driverZip=Join-Path $CacheDir "vdd-$DriverVersion.zip"; $nefconZip=Join-Path $CacheDir "nefcon-$NefConVersion.zip"
    Write-Log "Runtime root: $RuntimeRoot"
    Get-CachedPayload -Url $DriverUrl -Path $driverZip -Sha256 $DriverSha256 -Label "VDD $DriverVersion"
    Get-CachedPayload -Url $NefConUrl -Path $nefconZip -Sha256 $NefConSha256 -Label "NefCon $NefConVersion"
    $driverExtract=Join-Path $WorkDir 'driver'; $nefconExtract=Join-Path $WorkDir 'nefcon'
    Expand-Archive -LiteralPath $driverZip -DestinationPath $driverExtract -Force
    Expand-Archive -LiteralPath $nefconZip -DestinationPath $nefconExtract -Force
    Copy-Item -LiteralPath (Join-Path $driverExtract 'VirtualDisplayDriver') -Destination $DriverSourceDir -Recurse -Force
    $infPath=Join-Path $DriverSourceDir 'MttVDD.inf'; $catPath=Join-Path $DriverSourceDir 'mttvdd.cat'; $nefconExe=Join-Path $nefconExtract 'x64\nefconw.exe'
    foreach($required in @($infPath,$catPath,$nefconExe)){ if(-not (Test-Path -LiteralPath $required)){ throw "Required installation file not found: $required" } }
    $catalogBytes=[System.IO.File]::ReadAllBytes($catPath)
    $certificates=New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    $certificates.Import($catalogBytes); $added=@()
    foreach($certificate in $certificates){
        $existing=Get-ChildItem 'Cert:\LocalMachine\TrustedPublisher' | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } | Select-Object -First 1
        if(-not $existing){
            $certPath=Join-Path $WorkDir ($certificate.Thumbprint + '.cer')
            [System.IO.File]::WriteAllBytes($certPath,$certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
            $added += $certificate.Thumbprint
        }
    }
    Set-Content -LiteralPath $CertificateStateFile -Value $added -Encoding ASCII
    Write-Log 'Installing exactly one Virtual Display Driver device...' Cyan
    Invoke-NativeProcess -FilePath $nefconExe -Arguments @('install',('"{0}"' -f $infPath),'Root\MttVDD') | Out-Null
    if(-not (Wait-Until -Description 'exactly one VDD device available' -TimeoutMs 10000 -Condition { @(Get-VddDevices).Count -eq 1 })){ throw "Expected exactly one VDD device after installation, found $(@(Get-VddDevices).Count)." }
    $devices=@(Get-VddDevices); Write-Log "PASS: one VDD device installed: $($devices[0].InstanceId)" Green
}

function Ensure-DisplayApi {
    if('Vmu.DisplayModeApi' -as [type]){ return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Vmu {
 public static class DisplayModeApi {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DEVMODE {
   [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName; public ushort dmSpecVersion,dmDriverVersion,dmSize,dmDriverExtra; public uint dmFields; public int dmPositionX,dmPositionY; public uint dmDisplayOrientation,dmDisplayFixedOutput; public short dmColor,dmDuplex,dmYResolution,dmTTOption,dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName; public ushort dmLogPixels; public uint dmBitsPerPel,dmPelsWidth,dmPelsHeight,dmDisplayFlags,dmDisplayFrequency,dmICMMethod,dmICMIntent,dmMediaType,dmDitherType,dmReserved1,dmReserved2,dmPanningWidth,dmPanningHeight;
  }
  public const int ENUM_CURRENT_SETTINGS=-1;
  public const uint DM_POSITION=0x00000020,DM_PELSWIDTH=0x00080000,DM_PELSHEIGHT=0x00100000,DM_DISPLAYFREQUENCY=0x00400000,CDS_UPDATEREGISTRY=1;
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool EnumDisplaySettings(string deviceName,int modeNum,ref DEVMODE devMode);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string deviceName,ref DEVMODE devMode,IntPtr hwnd,uint flags,IntPtr lParam);
 }
}
'@
}

function Get-Mode {
    param([string]$DeviceName)
    Ensure-DisplayApi
    $mode=New-Object Vmu.DisplayModeApi+DEVMODE; $mode.dmSize=[Runtime.InteropServices.Marshal]::SizeOf($mode)
    if(-not [Vmu.DisplayModeApi]::EnumDisplaySettings($DeviceName,[Vmu.DisplayModeApi]::ENUM_CURRENT_SETTINGS,[ref]$mode)){ throw "Cannot read display mode for $DeviceName." }
    return $mode
}

function Get-ActiveDisplayRects {
    Ensure-DisplayApi
    Add-Type -AssemblyName System.Windows.Forms
    $result=@()
    foreach($screen in [System.Windows.Forms.Screen]::AllScreens){
        try {
            $m=Get-Mode -DeviceName $screen.DeviceName
            $result += [pscustomobject]@{ DeviceName=$screen.DeviceName; X=[int]$m.dmPositionX; Y=[int]$m.dmPositionY; Width=[int]$m.dmPelsWidth; Height=[int]$m.dmPelsHeight; Right=[int]($m.dmPositionX+$m.dmPelsWidth); Bottom=[int]($m.dmPositionY+$m.dmPelsHeight) }
        } catch {}
    }
    return @($result)
}

function Get-OverlapLength {
    param([int]$A1,[int]$A2,[int]$B1,[int]$B2)
    return [Math]::Max(0,[Math]::Min($A2,$B2)-[Math]::Max($A1,$B1))
}

function Get-AdjacencyAnchor {
    param([string]$DeviceName)
    $rects=@(Get-ActiveDisplayRects)
    $target=$rects | Where-Object { $_.DeviceName -eq $DeviceName } | Select-Object -First 1
    if($null -eq $target){ throw "Cannot determine desktop position for $DeviceName." }
    $candidates=@()
    foreach($n in @($rects | Where-Object { $_.DeviceName -ne $DeviceName })){
        $vertical=Get-OverlapLength $target.Y $target.Bottom $n.Y $n.Bottom
        $horizontal=Get-OverlapLength $target.X $target.Right $n.X $n.Right
        if($target.X -eq $n.Right -and $vertical -gt 0){ $candidates += [pscustomobject]@{Neighbor=$n;Side='Left';Overlap=$vertical} }
        if($target.Right -eq $n.X -and $vertical -gt 0){ $candidates += [pscustomobject]@{Neighbor=$n;Side='Right';Overlap=$vertical} }
        if($target.Y -eq $n.Bottom -and $horizontal -gt 0){ $candidates += [pscustomobject]@{Neighbor=$n;Side='Above';Overlap=$horizontal} }
        if($target.Bottom -eq $n.Y -and $horizontal -gt 0){ $candidates += [pscustomobject]@{Neighbor=$n;Side='Below';Overlap=$horizontal} }
    }
    $best=$candidates | Sort-Object Overlap -Descending | Select-Object -First 1
    if($null -eq $best){
        Write-Log "WARNING: $DeviceName has no edge-sharing neighbor before mode change; topology repair will use the nearest active display." Yellow
        $others=@($rects | Where-Object { $_.DeviceName -ne $DeviceName })
        if($others.Count -eq 0){ return $null }
        $bestNeighbor=$others | Sort-Object @{Expression={ [Math]::Abs($_.X-$target.X)+[Math]::Abs($_.Y-$target.Y) }} | Select-Object -First 1
        $best=[pscustomobject]@{Neighbor=$bestNeighbor;Side='Right';Overlap=0}
    }
    Write-Log ("Adjacency anchor: {0} is {1} of {2}; shared edge={3}px" -f $DeviceName,$best.Side,$best.Neighbor.DeviceName,$best.Overlap) DarkGray
    return $best
}

function Get-ClampedPerpendicularPosition {
    param([int]$Original,[int]$NewSize,[int]$NeighborStart,[int]$NeighborEnd,[int]$MinimumOverlap)
    $min=$NeighborStart-$NewSize+$MinimumOverlap
    $max=$NeighborEnd-$MinimumOverlap
    if($min -gt $max){ return $NeighborStart }
    return [Math]::Max($min,[Math]::Min($Original,$max))
}

function Test-UsableAdjacency {
    param([string]$DeviceName,[int]$MinimumOverlap=$MinimumAdjacencyPixels)
    $rects=@(Get-ActiveDisplayRects)
    $target=$rects | Where-Object { $_.DeviceName -eq $DeviceName } | Select-Object -First 1
    if($null -eq $target){ return $false }
    foreach($n in @($rects | Where-Object { $_.DeviceName -ne $DeviceName })){
        $vertical=Get-OverlapLength $target.Y $target.Bottom $n.Y $n.Bottom
        $horizontal=Get-OverlapLength $target.X $target.Right $n.X $n.Right
        if((($target.X -eq $n.Right -or $target.Right -eq $n.X) -and $vertical -ge $MinimumOverlap) -or (($target.Y -eq $n.Bottom -or $target.Bottom -eq $n.Y) -and $horizontal -ge $MinimumOverlap)){ return $true }
    }
    return $false
}

function Test-Mode {
    param([string]$DeviceName,[uint32]$Width,[uint32]$Height)
    try { $m=Get-Mode -DeviceName $DeviceName; return ($m.dmPelsWidth -eq $Width -and $m.dmPelsHeight -eq $Height) } catch { return $false }
}

function Set-ModePreserveAdjacency {
    param([string]$DeviceName,[uint32]$Width,[uint32]$Height,[uint32]$RefreshRate)
    $current=Get-Mode -DeviceName $DeviceName
    $anchor=Get-AdjacencyAnchor -DeviceName $DeviceName
    $newX=[int]$current.dmPositionX; $newY=[int]$current.dmPositionY
    if($null -ne $anchor){
        $n=$anchor.Neighbor
        switch($anchor.Side){
            'Left'  { $newX=$n.Right; $newY=Get-ClampedPerpendicularPosition $newY ([int]$Height) $n.Y $n.Bottom $MinimumAdjacencyPixels }
            'Right' { $newX=$n.X-[int]$Width; $newY=Get-ClampedPerpendicularPosition $newY ([int]$Height) $n.Y $n.Bottom $MinimumAdjacencyPixels }
            'Above' { $newY=$n.Bottom; $newX=Get-ClampedPerpendicularPosition $newX ([int]$Width) $n.X $n.Right $MinimumAdjacencyPixels }
            'Below' { $newY=$n.Y-[int]$Height; $newX=Get-ClampedPerpendicularPosition $newX ([int]$Width) $n.X $n.Right $MinimumAdjacencyPixels }
        }
    }
    $mode=$current; $mode.dmPelsWidth=$Width; $mode.dmPelsHeight=$Height; $mode.dmDisplayFrequency=$RefreshRate; $mode.dmPositionX=$newX; $mode.dmPositionY=$newY
    $mode.dmFields=[Vmu.DisplayModeApi]::DM_POSITION -bor [Vmu.DisplayModeApi]::DM_PELSWIDTH -bor [Vmu.DisplayModeApi]::DM_PELSHEIGHT -bor [Vmu.DisplayModeApi]::DM_DISPLAYFREQUENCY
    Write-Log ("Applying mode ${Width}x${Height}@${RefreshRate} with preserved position relation at ({0},{1})." -f $newX,$newY) DarkGray
    $result=[Vmu.DisplayModeApi]::ChangeDisplaySettingsEx($DeviceName,[ref]$mode,[IntPtr]::Zero,[Vmu.DisplayModeApi]::CDS_UPDATEREGISTRY,[IntPtr]::Zero)
    if($result -ne 0){ throw "Windows rejected ${Width}x${Height}@${RefreshRate} on $DeviceName (result $result)." }
    if(-not (Wait-Until -Description "$DeviceName mode ${Width}x${Height}" -TimeoutMs 5000 -Condition { Test-Mode $DeviceName $Width $Height })){ throw "Timed out waiting for ${Width}x${Height} on $DeviceName." }
    if($null -ne $anchor -and -not (Wait-Until -Description "$DeviceName has usable edge adjacency" -TimeoutMs 2000 -Condition { Test-UsableAdjacency -DeviceName $DeviceName })){ throw "$DeviceName became active without usable adjacency to the desktop." }
}

function Open-DisplaySettings { Start-Process 'ms-settings:display' | Out-Null }

if(-not (Test-Path -LiteralPath $TopologyHelper)){ throw "Topology helper missing: $TopologyHelper" }
. $TopologyHelper

Restart-AsAdministrator
Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $RuntimeRoot -Force | Out-Null

Write-Log 'Virtual Monitors Universe - ALPHA acceptance test'
Write-Log "ALPHA runner: $RunnerVersion" Cyan
Write-Log "Repository runtime: $RuntimeRoot"
Write-Log "Computer: $env:COMPUTERNAME"
Write-Log "Windows: $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"
Write-Log "PowerShell: $($PSVersionTable.PSVersion)"

try {
    Write-Section 'PRE-FLIGHT: CLEAN BASELINE AND ONE FRESH VDD'
    $existing=@(Get-VddDevices); Write-Log "Existing VDD device nodes: $($existing.Count)"
    if($existing.Count -gt 0){ Write-Log 'Previous ALPHA test VDD remnants detected; removing them before the new test.' Yellow; if(-not (Remove-VddInstallation)){ throw 'Could not establish clean baseline.' } }
    Install-Vdd
    $vddDevices=@(Get-VddDevices); if($vddDevices.Count -ne 1){ throw "Expected one VDD device for identity mapping, found $($vddDevices.Count)." }
    $identity=Resolve-VmuVddIdentity -Device $vddDevices[0]; $name=$identity.GdiName
    Write-Log ("VDD IDENTITY: instance={0};gdi={1};friendly={2};source={3}/{4};target={5}/{6};monitorPath={7};adapterPath={8}" -f $identity.InstanceId,$identity.GdiName,$identity.FriendlyName,$identity.SourceLuid,$identity.SourceId,$identity.TargetLuid,$identity.TargetId,$identity.MonitorPath,$identity.AdapterPath) Green
    Write-Log 'PASS: exact PnP VDD instance mapped to exactly one active CCD/GDI display.' Green
    $Results.Preflight='PASS'

    Write-Section 'TEST 1: DYNAMIC RESOLUTION + TOPOLOGY ADJACENCY'
    Set-ModePreserveAdjacency -DeviceName $name -Width 1920 -Height 1080 -RefreshRate 60; Write-Log 'Set 1920x1080 @ 60 Hz with adjacency preservation.' Green
    Set-ModePreserveAdjacency -DeviceName $name -Width 3840 -Height 2160 -RefreshRate 60; Write-Log 'Set 3840x2160 @ 60 Hz with adjacency preservation.' Green
    Open-DisplaySettings
    $resolutionOk=Ask-User 'Does the virtual monitor now show 3840x2160 and remain usable?'
    Set-ModePreserveAdjacency -DeviceName $name -Width 1920 -Height 1080 -RefreshRate 60; Write-Log 'Returned to 1920x1080 @ 60 Hz with adjacency preservation.' Green
    $adjacencyAutomatic=Test-UsableAdjacency -DeviceName $name
    Open-DisplaySettings
    $adjacencyUser=Ask-User 'After UHD to FHD, is the virtual monitor still properly adjacent and reachable by mouse without a corner-only connection?'
    $Results.DynamicResolution=if($resolutionOk){'PASS'}else{'FAIL'}
    $Results.TopologyAdjacency=if($adjacencyAutomatic -and $adjacencyUser){'PASS'}else{'FAIL'}

    Write-Section 'TEST 2: DISCONNECT / RECONNECT WITHOUT UNINSTALL'
    Write-Log 'Disconnect method: SetDisplayConfig, exact CCD path only. No 0x0 mode is used.' Cyan
    if(-not (Test-VmuVddActive -InstanceId $identity.InstanceId -Expected $true)){ throw 'Safety check failed: exact VDD CCD path is not uniquely active before disconnect.' }
    Invoke-VmuDisconnectExact -Identity $identity
    if(-not (Wait-Until -Description "$name exact CCD path inactive; PnP device retained" -TimeoutMs 5000 -Condition { (Test-VmuVddActive -InstanceId $identity.InstanceId -Expected $false) -and (@(Get-VddDevices | Where-Object { $_.InstanceId -eq $identity.InstanceId }).Count -eq 1) })){ throw 'SetDisplayConfig returned success but the exact VDD CCD path did not become inactive.' }
    Write-Log 'PASS: exact VDD path is inactive while its PnP device remains installed.' Green
    Open-DisplaySettings
    $disconnectOk=Ask-User 'Is this virtual monitor now disconnected from the desktop while still known by Windows?'
    Invoke-VmuReconnectSaved
    if(-not (Wait-Until -Description "$name exact CCD path active again" -TimeoutMs 5000 -Condition { Test-VmuVddActive -InstanceId $identity.InstanceId -Expected $true })){ throw 'The exact VDD CCD path did not become active after reconnect.' }
    Write-Log 'PASS: the same exact VDD CCD path is active again.' Green
    Open-DisplaySettings
    $reconnectOk=Ask-User 'Was the same virtual monitor reconnected without reinstalling the driver?'
    $Results.DisconnectReconnect=if($disconnectOk -and $reconnectOk){'PASS'}else{'FAIL'}

    Write-Section 'TEST 3: ONE-SHOT UNINSTALL'
    $before=@(Get-VddDevices); Write-Log "VDD device nodes immediately before uninstall: $($before.Count)"
    $uninstallOk=Remove-VddInstallation
    Open-DisplaySettings
    $userOk=Ask-User 'After one uninstall attempt, is the virtual monitor completely gone from Windows display settings?'
    $Results.UninstallFirstAttempt=if($uninstallOk -and $userOk){'PASS'}else{'FAIL'}
}
catch {
    Write-Log "TEST ERROR: $($_.Exception.Message)" Red
}
finally {
    Write-Section 'FINAL RESULT'
    foreach($entry in $Results.GetEnumerator()){
        $color=if($entry.Value -eq 'PASS'){'Green'}elseif($entry.Value -eq 'FAIL'){'Red'}else{'Yellow'}
        Write-Log ("{0}: {1}" -f $entry.Key,$entry.Value) $color
    }
    Write-Log "Log file: $LogPath"
    Write-Log 'Development payload remains only under the repository .runtime directory.'
}

if($Results.Values -contains 'FAIL' -or $Results.Values -contains 'NOT RUN'){ exit 1 }
exit 0
