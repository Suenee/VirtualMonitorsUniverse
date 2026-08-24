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
$MinimumAdjacencyPixels = 64

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
    do {
        if(& $Condition){ Write-Log ("READY after {0} ms: {1}" -f $sw.ElapsedMilliseconds,$Description) DarkGray; return $true }
        Start-Sleep -Milliseconds 100
    } while($sw.ElapsedMilliseconds -lt $TimeoutMs)
    Write-Log ("TIMEOUT after {0} ms: {1}" -f $sw.ElapsedMilliseconds,$Description) Yellow
    return $false
}

function Ask-User {
    param([Parameter(Mandatory=$true)][string]$Prompt)
    while($true){
        $answer=(Read-Host "$Prompt [Y/N]").Trim().ToUpperInvariant()
        if($answer -eq 'Y'){ Write-Log "USER CONFIRMATION: YES - $Prompt" Green; return $true }
        if($answer -eq 'N'){ Write-Log "USER CONFIRMATION: NO - $Prompt" Red; return $false }
        Write-Host 'Please enter Y or N.' -ForegroundColor Yellow
    }
}

function Read-WindowsDisplayNumber {
    param([Parameter(Mandatory=$true)][string]$Label,[Parameter(Mandatory=$true)][string]$GdiName)
    while($true){
        $raw=(Read-Host "Enter the Windows Settings display number for $Label ($GdiName)").Trim()
        $number=0
        if([int]::TryParse($raw,[ref]$number) -and $number -gt 0){
            Write-Log "USER DISPLAY MAPPING: $Label = Windows display $number ($GdiName)" Cyan
            return $number
        }
        Write-Host 'Enter a positive display number, for example 7.' -ForegroundColor Yellow
    }
}

function Get-Vdds {
    return @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq 'Virtual Display Driver' })
}

function Ensure-DisplayModeApi {
    if('Vmu.MultiDisplayModeApi' -as [type]){ return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Vmu {
 public static class MultiDisplayModeApi {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DEVMODE {
   [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName; public ushort dmSpecVersion,dmDriverVersion,dmSize,dmDriverExtra; public uint dmFields; public int dmPositionX,dmPositionY; public uint dmDisplayOrientation,dmDisplayFixedOutput; public short dmColor,dmDuplex,dmYResolution,dmTTOption,dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName; public ushort dmLogPixels; public uint dmBitsPerPel,dmPelsWidth,dmPelsHeight,dmDisplayFlags,dmDisplayFrequency,dmICMMethod,dmICMIntent,dmMediaType,dmDitherType,dmReserved1,dmReserved2,dmPanningWidth,dmPanningHeight;
  }
  public const int ENUM_CURRENT_SETTINGS=-1;
  [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="EnumDisplaySettingsW")] public static extern bool EnumDisplaySettings(string n,int i,ref DEVMODE m);
 }
}
'@
}

function Ensure-CcdAtomicApi {
    if('Vmu.CcdAtomicModeApi' -as [type]){ return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Vmu {
 public static class CcdAtomicModeApi {
  [StructLayout(LayoutKind.Sequential)] public struct LUID { public UInt32 LowPart; public Int32 HighPart; }
  [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public UInt32 Numerator; public UInt32 Denominator; }
  [StructLayout(LayoutKind.Sequential)] public struct POINTL { public Int32 x; public Int32 y; }
  [StructLayout(LayoutKind.Sequential)] public struct SOURCE_MODE { public UInt32 width; public UInt32 height; public UInt32 pixelFormat; public POINTL position; }
  [StructLayout(LayoutKind.Explicit, Size=48)] public struct MODE_UNION { [FieldOffset(0)] public SOURCE_MODE sourceMode; }
  [StructLayout(LayoutKind.Sequential)] public struct MODE { public UInt32 infoType; public UInt32 id; public LUID adapterId; public MODE_UNION modeInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH_SOURCE { public LUID adapterId; public UInt32 id; public UInt32 modeInfoIdx; public UInt32 statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH_TARGET { public LUID adapterId; public UInt32 id; public UInt32 modeInfoIdx; public UInt32 outputTechnology; public UInt32 rotation; public UInt32 scaling; public RATIONAL refreshRate; public UInt32 scanLineOrdering; [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public UInt32 statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public UInt32 flags; }

  private const UInt32 QDC_ONLY_ACTIVE_PATHS=2;
  private const UInt32 SDC_USE_SUPPLIED_DISPLAY_CONFIG=0x20;
  private const UInt32 SDC_VALIDATE=0x40;
  private const UInt32 SDC_APPLY=0x80;
  private const UInt32 SDC_SAVE_TO_DATABASE=0x200;
  private const UInt32 MODE_IDX_INVALID=0xFFFFFFFF;
  private const UInt32 MODE_TYPE_SOURCE=1;

  [DllImport("user32.dll")] private static extern Int32 GetDisplayConfigBufferSizes(UInt32 flags,out UInt32 pathCount,out UInt32 modeCount);
  [DllImport("user32.dll")] private static extern Int32 QueryDisplayConfig(UInt32 flags,ref UInt32 pathCount,[Out] PATH[] paths,ref UInt32 modeCount,[Out] MODE[] modes,IntPtr topologyId);
  [DllImport("user32.dll")] private static extern Int32 SetDisplayConfig(UInt32 pathCount,PATH[] paths,UInt32 modeCount,MODE[] modes,UInt32 flags);

  private static string LuidText(LUID value){ return value.HighPart.ToString("X8")+":"+value.LowPart.ToString("X8"); }
  private static bool SameLuid(LUID value,string text){ return LuidText(value).Equals(text,StringComparison.OrdinalIgnoreCase); }

  public static Int32 SetExactSourceMode(string sourceLuid,UInt32 sourceId,string targetLuid,UInt32 targetId,UInt32 width,UInt32 height,Int32 x,Int32 y){
   UInt32 pc,mc;
   Int32 result=GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,out pc,out mc);
   if(result!=0) return result;
   PATH[] paths=new PATH[pc]; MODE[] modes=new MODE[mc];
   result=QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,ref pc,paths,ref mc,modes,IntPtr.Zero);
   if(result!=0) return result;

   Int32 match=-1;
   for(Int32 i=0;i<pc;i++){
    if(SameLuid(paths[i].sourceInfo.adapterId,sourceLuid) && paths[i].sourceInfo.id==sourceId && SameLuid(paths[i].targetInfo.adapterId,targetLuid) && paths[i].targetInfo.id==targetId){
     if(match!=-1) return 87;
     match=i;
    }
   }
   if(match<0) return 1168;

   UInt32 sourceModeIndex=paths[match].sourceInfo.modeInfoIdx;
   if(sourceModeIndex==MODE_IDX_INVALID || sourceModeIndex>=mc) return 87;
   if(modes[sourceModeIndex].infoType!=MODE_TYPE_SOURCE || modes[sourceModeIndex].id!=sourceId || !SameLuid(modes[sourceModeIndex].adapterId,sourceLuid)) return 87;

   SOURCE_MODE source=modes[sourceModeIndex].modeInfo.sourceMode;
   source.width=width; source.height=height; source.position.x=x; source.position.y=y;
   MODE_UNION union=modes[sourceModeIndex].modeInfo;
   union.sourceMode=source;
   modes[sourceModeIndex].modeInfo=union;

   // Let Windows choose a compatible target timing for this VDD only. All other paths and modes remain supplied exactly as queried.
   paths[match].targetInfo.modeInfoIdx=MODE_IDX_INVALID;

   result=SetDisplayConfig(pc,paths,mc,modes,SDC_USE_SUPPLIED_DISPLAY_CONFIG|SDC_VALIDATE);
   if(result!=0) return result;
   return SetDisplayConfig(pc,paths,mc,modes,SDC_USE_SUPPLIED_DISPLAY_CONFIG|SDC_APPLY|SDC_SAVE_TO_DATABASE);
  }
 }
}
'@
}

function Get-Mode {
    param([Parameter(Mandatory=$true,Position=0)][Alias('Name')][string]$DeviceName)
    if([string]::IsNullOrWhiteSpace($DeviceName)){ throw 'Cannot read display mode because the GDI display name is empty.' }
    Ensure-DisplayModeApi
    $m=New-Object Vmu.MultiDisplayModeApi+DEVMODE
    $m.dmSize=[Runtime.InteropServices.Marshal]::SizeOf($m)
    if(-not [Vmu.MultiDisplayModeApi]::EnumDisplaySettings($DeviceName,-1,[ref]$m)){ throw "Cannot read $DeviceName mode." }
    return $m
}

function Mode-Key {
    param([string]$Name)
    $m=Get-Mode $Name
    return "pos=$($m.dmPositionX),$($m.dmPositionY);mode=$($m.dmPelsWidth)x$($m.dmPelsHeight)@$($m.dmDisplayFrequency);orientation=$($m.dmDisplayOrientation)"
}

function Get-DisplaySnapshot {
    Add-Type -AssemblyName System.Windows.Forms
    $items=@()
    foreach($s in [System.Windows.Forms.Screen]::AllScreens){
        try {
            $m=Get-Mode $s.DeviceName
            $items += [pscustomobject]@{DeviceName=$s.DeviceName;X=[int]$m.dmPositionX;Y=[int]$m.dmPositionY;Width=[int]$m.dmPelsWidth;Height=[int]$m.dmPelsHeight;Refresh=[int]$m.dmDisplayFrequency}
        } catch {}
    }
    return @($items)
}

function Get-Overlap {
    param([int]$A1,[int]$A2,[int]$B1,[int]$B2)
    return [Math]::Max(0,[Math]::Min($A2,$B2)-[Math]::Max($A1,$B1))
}

function Get-AdjacencyAnchor {
    param([string]$DeviceName)
    $rects=@(Get-DisplaySnapshot)
    $t=$rects|Where-Object DeviceName -eq $DeviceName|Select-Object -First 1
    if($null -eq $t){ throw "Cannot determine desktop position for $DeviceName." }
    $c=@()
    foreach($n in @($rects|Where-Object DeviceName -ne $DeviceName)){
        $tr=$t.X+$t.Width; $tb=$t.Y+$t.Height; $nr=$n.X+$n.Width; $nb=$n.Y+$n.Height
        $v=Get-Overlap $t.Y $tb $n.Y $nb; $h=Get-Overlap $t.X $tr $n.X $nr
        if($t.X -eq $nr -and $v -gt 0){$c+=[pscustomobject]@{Neighbor=$n;Side='Left';Overlap=$v}}
        if($tr -eq $n.X -and $v -gt 0){$c+=[pscustomobject]@{Neighbor=$n;Side='Right';Overlap=$v}}
        if($t.Y -eq $nb -and $h -gt 0){$c+=[pscustomobject]@{Neighbor=$n;Side='Above';Overlap=$h}}
        if($tb -eq $n.Y -and $h -gt 0){$c+=[pscustomobject]@{Neighbor=$n;Side='Below';Overlap=$h}}
    }
    return $c|Sort-Object Overlap -Descending|Select-Object -First 1
}

function Set-CcdResolutionAtomic {
    param([Parameter(Mandatory=$true)]$Identity,[uint32]$Width,[uint32]$Height)
    $snapshot=@(Get-DisplaySnapshot)
    $target=$snapshot|Where-Object DeviceName -eq $Identity.GdiName|Select-Object -First 1
    if($null -eq $target){ throw "Cannot find target display $($Identity.GdiName) in active desktop snapshot." }
    $anchor=Get-AdjacencyAnchor $Identity.GdiName
    if($null -eq $anchor){ throw "Refusing resolution change: $($Identity.GdiName) has no edge-adjacent monitor." }

    $x=$target.X; $y=$target.Y; $n=$anchor.Neighbor
    switch($anchor.Side){
        'Left'  { $x=$n.X+$n.Width }
        'Right' { $x=$n.X-[int]$Width }
        'Above' { $y=$n.Y+$n.Height }
        'Below' { $y=$n.Y-[int]$Height }
    }

    Write-Log ("CCD atomic mode request: only {0} changes to {1}x{2} at ({3},{4}); anchor={5} of {6}." -f $Identity.GdiName,$Width,$Height,$x,$y,$anchor.Side,$n.DeviceName) Cyan
    Ensure-CcdAtomicApi
    $result=[Vmu.CcdAtomicModeApi]::SetExactSourceMode($Identity.SourceLuid,[uint32]$Identity.SourceId,$Identity.TargetLuid,[uint32]$Identity.TargetId,$Width,$Height,[int]$x,[int]$y)
    if($result -ne 0){ throw "CCD SetDisplayConfig validate/apply failed with result $result." }
    Write-Log 'CCD SetDisplayConfig applied the supplied active desktop configuration.' Green
}

function Assert-OtherDisplaysUnchanged {
    param([object[]]$Before,[string]$ChangedDevice)
    $after=@(Get-DisplaySnapshot)
    foreach($b in @($Before|Where-Object DeviceName -ne $ChangedDevice)){
        $a=$after|Where-Object DeviceName -eq $b.DeviceName|Select-Object -First 1
        if($null -eq $a){ throw "Collateral display change: $($b.DeviceName) disappeared." }
        if($a.X-ne$b.X -or $a.Y-ne$b.Y -or $a.Width-ne$b.Width -or $a.Height-ne$b.Height){
            throw "Collateral display change: $($b.DeviceName) geometry changed from ($($b.X),$($b.Y)) $($b.Width)x$($b.Height) to ($($a.X),$($a.Y)) $($a.Width)x$($a.Height)."
        }
    }
    Write-Log 'PASS: geometry of every other active display remained unchanged.' Green
}

function Test-TargetAdjacency {
    param([string]$DeviceName)
    $a=Get-AdjacencyAnchor $DeviceName
    return ($null -ne $a -and $a.Overlap -ge $MinimumAdjacencyPixels)
}

function Remove-OneVdd {
    param([string]$InstanceId)
    Invoke-Native "$env:SystemRoot\System32\pnputil.exe" @('/remove-device',('"{0}"' -f $InstanceId)) | Out-Null
    return (Wait-Until { @(Get-Vdds | Where-Object InstanceId -eq $InstanceId).Count -eq 0 } "VDD $InstanceId removed" 5000)
}

function Resolve-LiveIdentity {
    param([Parameter(Mandatory=$true)][string]$InstanceId,[Parameter(Mandatory=$true)][string]$Label)
    $device=Get-Vdds|Where-Object{$_.InstanceId-eq$InstanceId}|Select-Object -First 1
    if($null-eq$device){ throw "$Label PnP instance $InstanceId is no longer present." }
    $identity=Resolve-VmuVddIdentity -Device $device
    if($null-eq$identity -or [string]::IsNullOrWhiteSpace([string]$identity.GdiName)){ throw "$Label identity resolved without a GDI display name." }
    Write-Log ("LIVE IDENTITY {0}: instance={1}; gdi={2}; source={3}/{4}; target={5}/{6}" -f $Label,$identity.InstanceId,$identity.GdiName,$identity.SourceLuid,$identity.SourceId,$identity.TargetLuid,$identity.TargetId) DarkGray
    return $identity
}

Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
. $TopologyHelper
Write-Log 'Virtual Monitors Universe - MULTI-VDD isolation acceptance test'
Write-Log 'Runner: multivdd-isolation-v7-ccd-source-mode' Cyan
Write-Log 'Safety rule: QueryDisplayConfig supplies the complete active topology; only the selected VDD source mode is changed before SetDisplayConfig.' Cyan
Write-Log 'SDC_ALLOW_CHANGES is intentionally not used: Windows must not alter other supplied display paths or modes.' Cyan
Write-Log 'VMU does not move or resize application windows in this ALPHA test.' Cyan
Write-Log 'Future shrink rule: only windows belonging to the shrinking VDD and extending beyond its new work area may be minimally fitted.' DarkGray

try {
    if(@(Get-Vdds).Count-ne 0){ throw 'Multi-VDD test requires a clean baseline.' }
    foreach($required in @($InfPath,$CatPath,$NefConExe)){ if(-not(Test-Path -LiteralPath $required)){ throw "Required cached ALPHA payload missing: $required" } }

    $certs=New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    $certs.Import([IO.File]::ReadAllBytes($CatPath))
    foreach($cert in $certs){
        if(-not(Get-ChildItem 'Cert:\LocalMachine\TrustedPublisher'|Where-Object Thumbprint -eq $cert.Thumbprint)){
            $cer=Join-Path $WorkDir ($cert.Thumbprint+'.cer')
            [IO.File]::WriteAllBytes($cer,$cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher'|Out-Null
        }
    }

    Write-Log 'Installing VDD A and VDD B...' Cyan
    Invoke-Native $NefConExe @('install',('"{0}"'-f$InfPath),'Root\MttVDD')|Out-Null
    if(-not(Wait-Until{@(Get-Vdds).Count-eq 1}'VDD A available')){ throw 'VDD A did not appear.' }
    $aDevice=@(Get-Vdds)[0]
    Invoke-Native $NefConExe @('install',('"{0}"'-f$InfPath),'Root\MttVDD')|Out-Null
    if(-not(Wait-Until{@(Get-Vdds).Count-eq 2}'VDD A and VDD B available')){ throw 'VDD B did not appear.' }

    $devices=@(Get-Vdds|Sort-Object InstanceId)
    $a=$devices|Where-Object InstanceId -eq $aDevice.InstanceId|Select-Object -First 1
    $b=$devices|Where-Object InstanceId -ne $aDevice.InstanceId|Select-Object -First 1
    $aInstanceId=$a.InstanceId; $bInstanceId=$b.InstanceId
    $ia=Resolve-LiveIdentity $aInstanceId 'VDD-A'
    $ib=Resolve-LiveIdentity $bInstanceId 'VDD-B'
    Write-Log "A: $($ia.InstanceId) -> $($ia.GdiName), target $($ia.TargetId)" Green
    Write-Log "B: $($ib.InstanceId) -> $($ib.GdiName), target $($ib.TargetId)" Green

    Start-Process 'ms-settings:display'|Out-Null
    Write-Host ''
    Write-Host 'Windows display-number mapping' -ForegroundColor Cyan
    $aNumber=Read-WindowsDisplayNumber 'VDD-A' $ia.GdiName
    $bNumber=Read-WindowsDisplayNumber 'VDD-B' $ib.GdiName
    if($aNumber-eq$bNumber){ throw 'VDD display numbers must differ.' }
    Write-Log "DISPLAY MAP: VDD-A = monitor $aNumber; VDD-B = monitor $bNumber" Green

    $bBefore=Mode-Key $ib.GdiName
    $desktopBefore=@(Get-DisplaySnapshot)
    Set-CcdResolutionAtomic -Identity $ia -Width 3840 -Height 2160
    Start-Sleep -Milliseconds 400
    Assert-OtherDisplaysUnchanged $desktopBefore $ia.GdiName
    if(-not(Test-TargetAdjacency $ia.GdiName)){ throw 'VDD-A lost usable edge adjacency after CCD resolution change.' }
    $ib=Resolve-LiveIdentity $bInstanceId 'VDD-B after A resolution change'
    if($bBefore-ne(Mode-Key $ib.GdiName)){ throw 'Changing A modified B.' }
    Write-Log "PASS: monitor $aNumber changed to UHD without changing any other display geometry." Green
    if(-not(Ask-User "Verify monitor $aNumber (VDD-A) is UHD, monitor $bNumber (VDD-B) is unchanged, and no windows on other monitors moved or resized")){ throw 'User rejected safe resolution isolation.' }

    $ia=Resolve-LiveIdentity $aInstanceId 'VDD-A before disconnect'
    $ib=Resolve-LiveIdentity $bInstanceId 'VDD-B before A disconnect'
    Write-Log ("DISCONNECT TARGET: monitor {0}; instance={1}; gdi={2}; source={3}/{4}; target={5}/{6}"-f$aNumber,$ia.InstanceId,$ia.GdiName,$ia.SourceLuid,$ia.SourceId,$ia.TargetLuid,$ia.TargetId) Cyan
    Invoke-VmuDisconnectExact -Identity $ia
    if(-not(Wait-Until{(Test-VmuVddActive $aInstanceId $false)-and(Test-VmuVddActive $bInstanceId $true)}"monitor $aNumber inactive while monitor $bNumber remains active" 5000)){ throw 'Disconnect isolation failed.' }
    $ib=Resolve-LiveIdentity $bInstanceId 'VDD-B after A disconnect'
    if((Mode-Key $ib.GdiName)-ne$bBefore){ throw 'Disconnecting A modified B configuration.' }
    Write-Log "PASS: VDD-A (monitor $aNumber) disconnected; VDD-B (monitor $bNumber) remained active and unchanged." Green
    if(-not(Ask-User "Verify monitor $aNumber (VDD-A) is disconnected and monitor $bNumber (VDD-B) is still active")){ throw 'User rejected disconnect isolation.' }

    Invoke-VmuReconnectSaved
    if(-not(Wait-Until{(Test-VmuVddActive $aInstanceId $true)-and(Test-VmuVddActive $bInstanceId $true)}"monitor $aNumber reconnected while monitor $bNumber remains active" 5000)){ throw 'Reconnect isolation failed.' }
    $ib=Resolve-LiveIdentity $bInstanceId 'VDD-B after A reconnect'
    if((Mode-Key $ib.GdiName)-ne$bBefore){ throw 'Reconnecting A modified B configuration.' }
    Write-Log "PASS: VDD-A (monitor $aNumber) reconnected; VDD-B (monitor $bNumber) remained unchanged." Green
    if(-not(Ask-User "Verify monitor $aNumber (VDD-A) returned and monitor $bNumber (VDD-B) is unchanged")){ throw 'User rejected reconnect isolation.' }

    if(-not(Remove-OneVdd $aInstanceId)){ throw 'Could not uninstall A on first attempt.' }
    if(@(Get-Vdds|Where-Object InstanceId -eq $bInstanceId).Count-ne 1 -or -not(Test-VmuVddActive $bInstanceId $true)){ throw 'Uninstalling A affected B.' }
    $ib=Resolve-LiveIdentity $bInstanceId 'VDD-B after A uninstall'
    if((Mode-Key $ib.GdiName)-ne$bBefore){ throw 'Uninstalling A modified B configuration.' }
    Write-Log "PASS: uninstalling VDD-A (monitor $aNumber) did not affect VDD-B (monitor $bNumber)." Green
    if(-not(Ask-User "Verify monitor $aNumber (VDD-A) is gone and monitor $bNumber (VDD-B) still works")){ throw 'User rejected uninstall isolation.' }

    if(-not(Remove-OneVdd $bInstanceId)){ throw 'Could not uninstall B during cleanup.' }
    Write-Log 'MULTI-VDD ISOLATION: PASS' Green
    exit 0
}
catch {
    Write-Log "MULTI-VDD TEST ERROR: $($_.Exception.Message)" Red
    foreach($d in @(Get-Vdds)){ Invoke-Native "$env:SystemRoot\System32\pnputil.exe" @('/remove-device',('"{0}"'-f$d.InstanceId)) -AllowFailure|Out-Null }
    exit 1
}
