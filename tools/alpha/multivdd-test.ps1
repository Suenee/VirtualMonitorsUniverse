[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LogPath = Join-Path $RepoRoot 'multivddtest.log'
$RuntimeRoot = Join-Path $RepoRoot '.runtime\alpha'
$DriverSourceDir = Join-Path $RuntimeRoot 'vdd-source'
$WorkDir = Join-Path $RuntimeRoot 'work'
$InfPath = Join-Path $DriverSourceDir 'MttVDD.inf'
$CatPath = Join-Path $DriverSourceDir 'mttvdd.cat'
$NefConExe = Join-Path $WorkDir 'nefcon\x64\nefconw.exe'
$TopologyHelper = Join-Path $PSScriptRoot 'displayconfig-topology.ps1'

function Write-Log {
    param([Parameter(Mandatory=$true)][AllowEmptyString()][string]$Message,[ConsoleColor]$Color=[ConsoleColor]::Gray)
    $line='[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'),$Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    Write-Host $Message -ForegroundColor $Color
}

function Invoke-Native {
    param([string]$FilePath,[string[]]$Arguments,[switch]$AllowFailure)
    Write-Log ("RUN: {0} {1}" -f $FilePath,($Arguments -join ' ')) DarkGray
    $p=Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    Write-Log "EXIT CODE: $($p.ExitCode)" DarkGray
    if(-not $AllowFailure -and $p.ExitCode -ne 0){ throw "$FilePath failed with exit code $($p.ExitCode)." }
    return $p.ExitCode
}

function Wait-Until {
    param([scriptblock]$Condition,[string]$Description,[int]$TimeoutMs=10000)
    $sw=[Diagnostics.Stopwatch]::StartNew()
    do { if(& $Condition){ Write-Log ("READY after {0} ms: {1}" -f $sw.ElapsedMilliseconds,$Description) DarkGray; return $true }; Start-Sleep -Milliseconds 100 } while($sw.ElapsedMilliseconds -lt $TimeoutMs)
    Write-Log ("TIMEOUT after {0} ms: {1}" -f $sw.ElapsedMilliseconds,$Description) Yellow
    return $false
}

function Get-Vdds { return @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' }) }

function Ensure-DisplayApi {
    if('Vmu.MultiDisplayModeApi' -as [type]){ return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Vmu {
 public static class MultiDisplayModeApi {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DEVMODE {
   [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName; public ushort dmSpecVersion,dmDriverVersion,dmSize,dmDriverExtra; public uint dmFields; public int dmPositionX,dmPositionY; public uint dmDisplayOrientation,dmDisplayFixedOutput; public short dmColor,dmDuplex,dmYResolution,dmTTOption,dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName; public ushort dmLogPixels; public uint dmBitsPerPel,dmPelsWidth,dmPelsHeight,dmDisplayFlags,dmDisplayFrequency,dmICMMethod,dmICMIntent,dmMediaType,dmDitherType,dmReserved1,dmReserved2,dmPanningWidth,dmPanningHeight;
  }
  public const int ENUM_CURRENT_SETTINGS=-1; public const uint DM_PELSWIDTH=0x00080000,DM_PELSHEIGHT=0x00100000,DM_DISPLAYFREQUENCY=0x00400000,CDS_UPDATEREGISTRY=1;
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool EnumDisplaySettings(string n,int i,ref DEVMODE m);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int ChangeDisplaySettingsEx(string n,ref DEVMODE m,IntPtr h,uint f,IntPtr l);
 }
}
'@
}

function Get-Mode([string]$Name){ Ensure-DisplayApi; $m=New-Object Vmu.MultiDisplayModeApi+DEVMODE; $m.dmSize=[Runtime.InteropServices.Marshal]::SizeOf($m); if(-not [Vmu.MultiDisplayModeApi]::EnumDisplaySettings($Name,-1,[ref]$m)){throw "Cannot read $Name mode."}; return $m }
function Mode-Key([string]$Name){ $m=Get-Mode $Name; return "pos=$($m.dmPositionX),$($m.dmPositionY);mode=$($m.dmPelsWidth)x$($m.dmPelsHeight)@$($m.dmDisplayFrequency);orientation=$($m.dmDisplayOrientation)" }
function Set-Resolution([string]$Name,[uint32]$W,[uint32]$H){ $m=Get-Mode $Name; $m.dmPelsWidth=$W; $m.dmPelsHeight=$H; $m.dmDisplayFrequency=60; $m.dmFields=[Vmu.MultiDisplayModeApi]::DM_PELSWIDTH -bor [Vmu.MultiDisplayModeApi]::DM_PELSHEIGHT -bor [Vmu.MultiDisplayModeApi]::DM_DISPLAYFREQUENCY; $r=[Vmu.MultiDisplayModeApi]::ChangeDisplaySettingsEx($Name,[ref]$m,[IntPtr]::Zero,1,[IntPtr]::Zero); if($r -ne 0){throw "Resolution change failed for $Name: $r"} }

function Remove-OneVdd([string]$InstanceId){ Invoke-Native "$env:SystemRoot\System32\pnputil.exe" @('/remove-device',('"{0}"' -f $InstanceId)) | Out-Null; return (Wait-Until { @(Get-Vdds | Where-Object InstanceId -eq $InstanceId).Count -eq 0 } "VDD $InstanceId removed" 5000) }

Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
. $TopologyHelper
Write-Log 'Virtual Monitors Universe - MULTI-VDD isolation acceptance test'
Write-Log 'Runner: multivdd-isolation-v1' Cyan

try {
    if(@(Get-Vdds).Count -ne 0){ throw 'Multi-VDD test requires the preceding ALPHA test to leave a clean baseline.' }
    foreach($required in @($InfPath,$CatPath,$NefConExe)){ if(-not (Test-Path -LiteralPath $required)){ throw "Required cached ALPHA payload missing: $required" } }

    $certs=New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    $certs.Import([IO.File]::ReadAllBytes($CatPath))
    foreach($cert in $certs){ if(-not (Get-ChildItem 'Cert:\LocalMachine\TrustedPublisher' | Where-Object Thumbprint -eq $cert.Thumbprint)){ $cer=Join-Path $WorkDir ($cert.Thumbprint+'.cer'); [IO.File]::WriteAllBytes($cer,$cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)); Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null } }

    Write-Log 'Installing VDD A and VDD B...' Cyan
    Invoke-Native $NefConExe @('install',('"{0}"' -f $InfPath),'Root\MttVDD') | Out-Null
    if(-not (Wait-Until { @(Get-Vdds).Count -eq 1 } 'VDD A available')){ throw 'VDD A did not appear.' }
    $aDevice=@(Get-Vdds)[0]
    Invoke-Native $NefConExe @('install',('"{0}"' -f $InfPath),'Root\MttVDD') | Out-Null
    if(-not (Wait-Until { @(Get-Vdds).Count -eq 2 } 'VDD A and VDD B available')){ throw 'VDD B did not appear.' }
    $devices=@(Get-Vdds | Sort-Object InstanceId)
    $a=$devices | Where-Object InstanceId -eq $aDevice.InstanceId | Select-Object -First 1
    $b=$devices | Where-Object InstanceId -ne $aDevice.InstanceId | Select-Object -First 1
    $ia=Resolve-VmuVddIdentity $a; $ib=Resolve-VmuVddIdentity $b
    Write-Log "A: $($ia.InstanceId) -> $($ia.GdiName), target $($ia.TargetId)" Green
    Write-Log "B: $($ib.InstanceId) -> $($ib.GdiName), target $($ib.TargetId)" Green
    if($ia.InstanceId -eq $ib.InstanceId -or $ia.GdiName -eq $ib.GdiName){ throw 'VDD identities are not unique.' }

    $bBefore=Mode-Key $ib.GdiName
    Set-Resolution $ia.GdiName 3840 2160
    Start-Sleep -Milliseconds 300
    $bAfterResolution=Mode-Key $ib.GdiName
    if($bBefore -ne $bAfterResolution){ throw "Changing A modified B: $bBefore -> $bAfterResolution" }
    Write-Log 'PASS: changing resolution of A did not modify B.' Green

    Invoke-VmuDisconnectExact $ia
    if(-not (Wait-Until { (Test-VmuVddActive $ia.InstanceId $false) -and (Test-VmuVddActive $ib.InstanceId $true) } 'A inactive while B remains active' 5000)){ throw 'Disconnect isolation failed.' }
    if((Mode-Key $ib.GdiName) -ne $bBefore){ throw 'Disconnecting A modified B configuration.' }
    Write-Log 'PASS: disconnecting A left B active and unchanged.' Green
    Invoke-VmuReconnectSaved
    if(-not (Wait-Until { (Test-VmuVddActive $ia.InstanceId $true) -and (Test-VmuVddActive $ib.InstanceId $true) } 'A reconnected while B remains active' 5000)){ throw 'Reconnect isolation failed.' }
    if((Mode-Key $ib.GdiName) -ne $bBefore){ throw 'Reconnecting A modified B configuration.' }
    Write-Log 'PASS: reconnecting A left B active and unchanged.' Green

    if(-not (Remove-OneVdd $ia.InstanceId)){ throw 'Could not uninstall A on first attempt.' }
    if(@(Get-Vdds | Where-Object InstanceId -eq $ib.InstanceId).Count -ne 1 -or -not (Test-VmuVddActive $ib.InstanceId $true)){ throw 'Uninstalling A affected B.' }
    if((Mode-Key $ib.GdiName) -ne $bBefore){ throw 'Uninstalling A modified B configuration.' }
    Write-Log 'PASS: uninstalling A did not remove, disconnect, or reconfigure B.' Green

    if(-not (Remove-OneVdd $ib.InstanceId)){ throw 'Could not uninstall B during cleanup.' }
    $inf=(Get-PnpDeviceProperty -InstanceId $b.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
    Write-Log 'MULTI-VDD ISOLATION: PASS' Green
    exit 0
}
catch {
    Write-Log "MULTI-VDD TEST ERROR: $($_.Exception.Message)" Red
    foreach($d in @(Get-Vdds)){ Invoke-Native "$env:SystemRoot\System32\pnputil.exe" @('/remove-device',('"{0}"' -f $d.InstanceId)) -AllowFailure | Out-Null }
    exit 1
}
