[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo_root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$log_path = Join-Path $repo_root 'multivddtest.log'
$runtime_root = Join-Path $repo_root '.runtime\alpha'
$inf_path = Join-Path $runtime_root 'vdd-source\MttVDD.inf'
$cat_path = Join-Path $runtime_root 'vdd-source\mttvdd.cat'
$nefcon_exe = Join-Path $runtime_root 'work\nefcon\x64\nefconw.exe'
$topology_helper = Join-Path $PSScriptRoot 'displayconfig-topology.ps1'

function Write-Log {
    param([Parameter(Mandatory=$true)][AllowEmptyString()][string]$Message,[ConsoleColor]$Color=[ConsoleColor]::Gray)
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message
    Add-Content -LiteralPath $log_path -Value $line -Encoding UTF8
    Write-Host $Message -ForegroundColor $Color
}

function Invoke-Native {
    param([string]$FilePath,[string[]]$Arguments,[switch]$AllowFailure)
    Write-Log ("RUN: {0} {1}" -f $FilePath,($Arguments -join ' ')) DarkGray
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    Write-Log "EXIT CODE: $($process.ExitCode)" DarkGray
    if(-not $AllowFailure -and $process.ExitCode -ne 0){ throw "$FilePath failed with exit code $($process.ExitCode)." }
    return $process.ExitCode
}

function Wait-Until {
    param([scriptblock]$Condition,[string]$Description,[int]$TimeoutMs=10000)
    $stopwatch=[Diagnostics.Stopwatch]::StartNew()
    do {
        if(& $Condition){ Write-Log ("READY after {0} ms: {1}" -f $stopwatch.ElapsedMilliseconds,$Description) DarkGray; return $true }
        Start-Sleep -Milliseconds 100
    } while($stopwatch.ElapsedMilliseconds -lt $TimeoutMs)
    Write-Log ("TIMEOUT after {0} ms: {1}" -f $stopwatch.ElapsedMilliseconds,$Description) Yellow
    return $false
}

function Ask-User {
    param([string]$Prompt)
    while($true){
        $answer=(Read-Host "$Prompt [Y/N]").Trim().ToUpperInvariant()
        if($answer -eq 'Y'){ Write-Log "USER CONFIRMATION: YES - $Prompt" Green; return $true }
        if($answer -eq 'N'){ Write-Log "USER CONFIRMATION: NO - $Prompt" Red; return $false }
    }
}

function Get-Vdds { return @(Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object FriendlyName -eq 'Virtual Display Driver') }

function Ensure-VmuApi {
    if('Vmu.ReflowApi' -as [type]){ return }
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    Add-Type -ReferencedAssemblies @('System.Windows.Forms.dll','System.Drawing.dll') -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace Vmu {
 public static class ReflowApi {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string dmDeviceName; public ushort dmSpecVersion,dmDriverVersion,dmSize,dmDriverExtra; public uint dmFields; public int dmPositionX,dmPositionY; public uint dmDisplayOrientation,dmDisplayFixedOutput; public short dmColor,dmDuplex,dmYResolution,dmTTOption,dmCollate; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string dmFormName; public ushort dmLogPixels; public uint dmBitsPerPel,dmPelsWidth,dmPelsHeight,dmDisplayFlags,dmDisplayFrequency,dmICMMethod,dmICMIntent,dmMediaType,dmDitherType,dmReserved1,dmReserved2,dmPanningWidth,dmPanningHeight; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
  [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public uint Numerator,Denominator; }
  [StructLayout(LayoutKind.Sequential)] public struct POINTL { public int x,y; }
  [StructLayout(LayoutKind.Sequential)] public struct SOURCE_MODE { public uint width,height,pixelFormat; public POINTL position; }
  [StructLayout(LayoutKind.Explicit,Size=48)] public struct MODE_UNION { [FieldOffset(0)] public SOURCE_MODE sourceMode; }
  [StructLayout(LayoutKind.Sequential)] public struct MODE { public uint infoType,id; public LUID adapterId; public MODE_UNION modeInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH_SOURCE { public LUID adapterId; public uint id,modeInfoIdx,statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH_TARGET { public LUID adapterId; public uint id,modeInfoIdx,outputTechnology,rotation,scaling; public RATIONAL refreshRate; public uint scanLineOrdering; [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public uint statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }
  public class WindowState { public IntPtr Hwnd; public string Title; public RECT Rect; public string Screen; public int RelativeX,RelativeY,Width,Height; }
  const uint QDC_ONLY_ACTIVE_PATHS=2, SDC_USE_SUPPLIED_DISPLAY_CONFIG=0x20, SDC_VALIDATE=0x40, SDC_APPLY=0x80, SDC_SAVE_TO_DATABASE=0x200, MODE_TYPE_SOURCE=1;
  const int ENUM_CURRENT_SETTINGS=-1;
  delegate bool EnumWindowsProc(IntPtr hwnd,IntPtr lParam);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplaySettings(string n,int i,ref DEVMODE m);
  [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint f,out uint pc,out uint mc);
  [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint f,ref uint pc,[Out] PATH[] p,ref uint mc,[Out] MODE[] m,IntPtr t);
  [DllImport("user32.dll")] static extern int SetDisplayConfig(uint pc,PATH[] p,uint mc,MODE[] m,uint f);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc p,IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
  static string Luid(LUID v){return v.HighPart.ToString("X8")+":"+v.LowPart.ToString("X8");}
  public static DEVMODE Mode(string name){var m=new DEVMODE();m.dmSize=(ushort)Marshal.SizeOf(typeof(DEVMODE));if(!EnumDisplaySettings(name,ENUM_CURRENT_SETTINGS,ref m))throw new InvalidOperationException("Cannot read "+name);return m;}
  public static WindowState[] Windows(){var list=new List<WindowState>();EnumWindows(delegate(IntPtr h,IntPtr l){if(!IsWindowVisible(h)||IsIconic(h))return true;RECT r;if(!GetWindowRect(h,out r)||r.Right<=r.Left||r.Bottom<=r.Top)return true;var sb=new StringBuilder(256);GetWindowText(h,sb,sb.Capacity);var screen=System.Windows.Forms.Screen.FromHandle(h);list.Add(new WindowState{Hwnd=h,Title=sb.ToString(),Rect=r,Screen=screen.DeviceName,RelativeX=r.Left-screen.Bounds.Left,RelativeY=r.Top-screen.Bounds.Top,Width=r.Right-r.Left,Height=r.Bottom-r.Top});return true;},IntPtr.Zero);return list.ToArray();}
  public static int ApplyReflow(string changedLuid,uint changedSource,uint width,uint height,string[] names,int[] xs,int[] ys,uint[] widths,uint[] heights){uint pc,mc;int r=GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,out pc,out mc);if(r!=0)return r;var paths=new PATH[pc];var modes=new MODE[mc];r=QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,ref pc,paths,ref mc,modes,IntPtr.Zero);if(r!=0)return r;for(int i=0;i<pc;i++){uint mi=paths[i].sourceInfo.modeInfoIdx;if(mi>=mc)continue;if(modes[mi].infoType!=MODE_TYPE_SOURCE)continue;string key=Luid(paths[i].sourceInfo.adapterId)+"/"+paths[i].sourceInfo.id;int index=-1;for(int j=0;j<names.Length;j++){if(names[j]==key){index=j;break;}}if(index<0)continue;var s=modes[mi].modeInfo.sourceMode;s.position.x=xs[index];s.position.y=ys[index];s.width=widths[index];s.height=heights[index];var u=modes[mi].modeInfo;u.sourceMode=s;modes[mi].modeInfo=u;}r=SetDisplayConfig(pc,paths,mc,modes,SDC_USE_SUPPLIED_DISPLAY_CONFIG|SDC_VALIDATE);if(r!=0)return r;return SetDisplayConfig(pc,paths,mc,modes,SDC_USE_SUPPLIED_DISPLAY_CONFIG|SDC_APPLY|SDC_SAVE_TO_DATABASE);}
 }
}
'@
}

function Get-DisplaySnapshot {
    Ensure-VmuApi
    $items=@()
    foreach($screen in [System.Windows.Forms.Screen]::AllScreens){
        try {
            $mode=[Vmu.ReflowApi]::Mode($screen.DeviceName)
            $items += [pscustomobject]@{DeviceName=$screen.DeviceName;X=[int]$mode.dmPositionX;Y=[int]$mode.dmPositionY;Width=[int]$mode.dmPelsWidth;Height=[int]$mode.dmPelsHeight}
        } catch {}
    }
    return @($items)
}

function Get-ModeKey([string]$DeviceName){ $m=[Vmu.ReflowApi]::Mode($DeviceName); return "pos=$($m.dmPositionX),$($m.dmPositionY);mode=$($m.dmPelsWidth)x$($m.dmPelsHeight)@$($m.dmDisplayFrequency)" }
function Get-Overlap([int]$a1,[int]$a2,[int]$b1,[int]$b2){ return [Math]::Max(0,[Math]::Min($a2,$b2)-[Math]::Max($a1,$b1)) }
function Test-RectsOverlap($a,$b){ return ((Get-Overlap $a.X ($a.X+$a.Width) $b.X ($b.X+$b.Width))-gt0 -and (Get-Overlap $a.Y ($a.Y+$a.Height) $b.Y ($b.Y+$b.Height))-gt0) }

function New-ReflowPlan {
    param([object[]]$Snapshot,[string]$Target,[int]$NewWidth,[int]$NewHeight)

    $plan=@()
    foreach($d in $Snapshot){
        $plan += [pscustomobject]@{DeviceName=$d.DeviceName;X=[int]$d.X;Y=[int]$d.Y;Width=[int]$d.Width;Height=[int]$d.Height;DeltaX=0;DeltaY=0;OriginalX=[int]$d.X;OriginalY=[int]$d.Y;OriginalWidth=[int]$d.Width;OriginalHeight=[int]$d.Height}
    }
    $target=$plan|Where-Object DeviceName -eq $Target|Select-Object -First 1
    if($null-eq$target){throw "Target $Target missing from topology."}

    $old_width=$target.Width
    $old_height=$target.Height
    $delta_width=$NewWidth-$old_width
    $delta_height=$NewHeight-$old_height
    $old_right=$target.X+$old_width
    $old_bottom=$target.Y+$old_height
    $target.Width=$NewWidth
    $target.Height=$NewHeight

    # Horizontal reflow. The target keeps its top-left anchor. A monitor belongs to the
    # downstream block when it was at/touching the old right side, or when the expanded
    # target would collide with it. Once one monitor moves, any monitor that would collide
    # with that moved rectangle is pushed as well. The same calculation works with a
    # negative delta when shrinking, closing the gap instead of creating one.
    if($delta_width-ne0){
        $queue=New-Object System.Collections.Queue
        $selected=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($d in $plan|Where-Object DeviceName -ne $Target){
            $vertical_union=Get-Overlap $target.Y ($target.Y+[Math]::Max($old_height,$NewHeight)) $d.Y ($d.Y+$d.Height)
            $was_downstream=($d.OriginalX-ge$old_right)
            $would_collide=(Test-RectsOverlap $target $d)
            if($vertical_union-gt0 -and ($was_downstream -or $would_collide)){
                [void]$selected.Add($d.DeviceName);$queue.Enqueue($d)
            }
        }
        while($queue.Count-gt0){
            $d=$queue.Dequeue()
            if($d.DeltaX-eq0){$d.X += $delta_width;$d.DeltaX += $delta_width}
            foreach($other in $plan|Where-Object {$_.DeviceName-ne$Target -and $_.DeviceName-ne$d.DeviceName}){
                if($selected.Contains($other.DeviceName)){continue}
                $old_touch=($other.OriginalX-ge($d.OriginalX+$d.OriginalWidth)) -and ((Get-Overlap $d.OriginalY ($d.OriginalY+$d.OriginalHeight) $other.OriginalY ($other.OriginalY+$other.OriginalHeight))-gt0)
                $collision=(Test-RectsOverlap $d $other)
                if($old_touch -or $collision){[void]$selected.Add($other.DeviceName);$queue.Enqueue($other)}
            }
        }
    }

    # Vertical reflow follows the same rule for the bottom edge.
    if($delta_height-ne0){
        $queue=New-Object System.Collections.Queue
        $selected=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($d in $plan|Where-Object DeviceName -ne $Target){
            $horizontal_union=Get-Overlap $target.X ($target.X+[Math]::Max($old_width,$NewWidth)) $d.X ($d.X+$d.Width)
            $was_downstream=($d.OriginalY-ge$old_bottom)
            $would_collide=(Test-RectsOverlap $target $d)
            if($horizontal_union-gt0 -and ($was_downstream -or $would_collide)){
                [void]$selected.Add($d.DeviceName);$queue.Enqueue($d)
            }
        }
        while($queue.Count-gt0){
            $d=$queue.Dequeue()
            if($d.DeltaY-eq0){$d.Y += $delta_height;$d.DeltaY += $delta_height}
            foreach($other in $plan|Where-Object {$_.DeviceName-ne$Target -and $_.DeviceName-ne$d.DeviceName}){
                if($selected.Contains($other.DeviceName)){continue}
                $old_touch=($other.OriginalY-ge($d.OriginalY+$d.OriginalHeight)) -and ((Get-Overlap $d.OriginalX ($d.OriginalX+$d.OriginalWidth) $other.OriginalX ($other.OriginalX+$other.OriginalWidth))-gt0)
                $collision=(Test-RectsOverlap $d $other)
                if($old_touch -or $collision){[void]$selected.Add($other.DeviceName);$queue.Enqueue($other)}
            }
        }
    }

    return @($plan)
}

function Assert-PlanNoOverlap {
    param([object[]]$Plan)
    for($i=0;$i-lt$Plan.Count;$i++){
        for($j=$i+1;$j-lt$Plan.Count;$j++){
            if(Test-RectsOverlap $Plan[$i] $Plan[$j]){throw "Reflow plan overlaps $($Plan[$i].DeviceName) and $($Plan[$j].DeviceName)."}
        }
    }
}

function Resolve-LiveIdentity([string]$InstanceId,[string]$Label){$device=Get-Vdds|Where-Object InstanceId -eq $InstanceId|Select-Object -First 1;if($null-eq$device){throw "$Label missing."};$identity=Resolve-VmuVddIdentity -Device $device;if($null-eq$identity){throw "$Label identity unavailable."};Write-Log ("LIVE IDENTITY {0}: instance={1}; gdi={2}; source={3}/{4}; target={5}/{6}"-f$Label,$identity.InstanceId,$identity.GdiName,$identity.SourceLuid,$identity.SourceId,$identity.TargetLuid,$identity.TargetId) DarkGray;return $identity}
function Remove-OneVdd([string]$InstanceId){Invoke-Native "$env:SystemRoot\System32\pnputil.exe" @('/remove-device',('"{0}"'-f$InstanceId))|Out-Null;return Wait-Until {@(Get-Vdds|Where-Object InstanceId -eq $InstanceId).Count-eq0} "VDD $InstanceId removed" 5000}

function Test-ReflowWindows {
    param([object[]]$BeforeWindows,[object[]]$Plan,[string]$Target)
    Start-Sleep -Milliseconds 500
    $after=@([Vmu.ReflowApi]::Windows())
    foreach($p in $Plan|Where-Object {($_.DeltaX-ne0-or$_.DeltaY-ne0)-and$_.DeviceName-ne$Target}){
        foreach($w in $BeforeWindows|Where-Object Screen -eq $p.DeviceName){
            $a=$after|Where-Object Hwnd -eq $w.Hwnd|Select-Object -First 1
            if($null-eq$a){continue}
            if($a.Screen-ne$p.DeviceName){throw "Window '$($w.Title)' moved to another monitor during reflow."}
            if($a.RelativeX-ne$w.RelativeX-or$a.RelativeY-ne$w.RelativeY-or$a.Width-ne$w.Width-or$a.Height-ne$w.Height){throw "Window '$($w.Title)' changed relative geometry on $($p.DeviceName)."}
        }
    }
    Write-Log 'PASS: windows on reflowed monitors retained relative position and size.' Green
}

Remove-Item -LiteralPath $log_path -Force -ErrorAction SilentlyContinue
. $topology_helper
Ensure-VmuApi
Write-Log 'Virtual Monitors Universe - MULTI-VDD topology reflow acceptance test'
Write-Log 'Runner: multivdd-isolation-v9-collision-aware-reflow' Cyan
Write-Log 'Rule: resizing a VDD reflows the complete downstream block for both grow and shrink; monitor-relative window geometry must remain unchanged.' Cyan

try {
    if(@(Get-Vdds).Count-ne0){throw 'Multi-VDD test requires a clean baseline.'}
    foreach($required in @($inf_path,$cat_path,$nefcon_exe)){if(-not(Test-Path $required)){throw "Missing ALPHA payload: $required"}}
    Write-Log 'Installing VDD A and VDD B...' Cyan
    Invoke-Native $nefcon_exe @('install',('"{0}"'-f$inf_path),'Root\MttVDD')|Out-Null
    if(-not(Wait-Until {@(Get-Vdds).Count-eq1} 'VDD A available')){throw 'VDD A did not appear.'}
    $a_device=@(Get-Vdds)[0]
    Invoke-Native $nefcon_exe @('install',('"{0}"'-f$inf_path),'Root\MttVDD')|Out-Null
    if(-not(Wait-Until {@(Get-Vdds).Count-eq2} 'VDD A and VDD B available')){throw 'VDD B did not appear.'}
    $devices=@(Get-Vdds|Sort-Object InstanceId);$a=$devices|Where-Object InstanceId -eq $a_device.InstanceId|Select-Object -First 1;$b=$devices|Where-Object InstanceId -ne $a_device.InstanceId|Select-Object -First 1
    $a_id=$a.InstanceId;$b_id=$b.InstanceId;$ia=Resolve-LiveIdentity $a_id 'VDD-A';$ib=Resolve-LiveIdentity $b_id 'VDD-B'
    Write-Log "VDD-A = $($ia.GdiName); VDD-B = $($ib.GdiName)" Green
    Write-Log 'No Windows display number is required; stable VDD identities are used.' Cyan

    $before=@(Get-DisplaySnapshot)
    foreach($d in $before){Write-Log ("BEFORE: {0}: ({1},{2}) {3}x{4}"-f$d.DeviceName,$d.X,$d.Y,$d.Width,$d.Height) DarkGray}
    $windows=@([Vmu.ReflowApi]::Windows())
    $plan=@(New-ReflowPlan $before $ia.GdiName 3840 2160)
    foreach($p in $plan){Write-Log ("REFLOW-GROW: {0}: ({1},{2}) {3}x{4}; delta=({5},{6})"-f$p.DeviceName,$p.X,$p.Y,$p.Width,$p.Height,$p.DeltaX,$p.DeltaY) DarkGray}
    Assert-PlanNoOverlap $plan

    $moved=@($plan|Where-Object {$_.DeltaX-ne0-or$_.DeltaY-ne0})
    foreach($p in $moved){if($p.DeviceName-ne$ia.GdiName-and$p.DeviceName-ne$ib.GdiName){throw "SAFE STOP: reflow requires moving physical display $($p.DeviceName); deterministic GDI-to-CCD mapping is not implemented yet."}}
    $names=@();$xs=@();$ys=@();$widths=@();$heights=@();foreach($p in $plan){$identity=$null;if($p.DeviceName-eq$ia.GdiName){$identity=$ia}elseif($p.DeviceName-eq$ib.GdiName){$identity=$ib}else{continue};$names += "$($identity.SourceLuid)/$($identity.SourceId)";$xs += [int]$p.X;$ys += [int]$p.Y;$widths += [uint32]$p.Width;$heights += [uint32]$p.Height}
    $result=[Vmu.ReflowApi]::ApplyReflow($ia.SourceLuid,[uint32]$ia.SourceId,3840,2160,$names,$xs,$ys,$widths,$heights);if($result-ne0){throw "SetDisplayConfig reflow failed: $result"}
    Test-ReflowWindows $windows $plan $ia.GdiName
    if(-not(Ask-User 'Verify the resized VDD and any shifted VDD kept their windows in the same relative places')){throw 'User rejected grow reflow.'}

    $grown=@(Get-DisplaySnapshot)
    foreach($d in $grown){Write-Log ("GROWN: {0}: ({1},{2}) {3}x{4}"-f$d.DeviceName,$d.X,$d.Y,$d.Width,$d.Height) DarkGray}
    $windows2=@([Vmu.ReflowApi]::Windows())
    $original=$before|Where-Object DeviceName -eq $ia.GdiName|Select-Object -First 1
    $plan2=@(New-ReflowPlan $grown $ia.GdiName $original.Width $original.Height)
    foreach($p in $plan2){Write-Log ("REFLOW-SHRINK: {0}: ({1},{2}) {3}x{4}; delta=({5},{6})"-f$p.DeviceName,$p.X,$p.Y,$p.Width,$p.Height,$p.DeltaX,$p.DeltaY) DarkGray}
    Assert-PlanNoOverlap $plan2
    $names=@();$xs=@();$ys=@();$widths=@();$heights=@();foreach($p in $plan2){$identity=$null;if($p.DeviceName-eq$ia.GdiName){$identity=$ia}elseif($p.DeviceName-eq$ib.GdiName){$identity=$ib}else{continue};$names += "$($identity.SourceLuid)/$($identity.SourceId)";$xs += [int]$p.X;$ys += [int]$p.Y;$widths += [uint32]$p.Width;$heights += [uint32]$p.Height}
    $result=[Vmu.ReflowApi]::ApplyReflow($ia.SourceLuid,[uint32]$ia.SourceId,[uint32]$original.Width,[uint32]$original.Height,$names,$xs,$ys,$widths,$heights);if($result-ne0){throw "SetDisplayConfig shrink reflow failed: $result"}
    Test-ReflowWindows $windows2 $plan2 $ia.GdiName
    Write-Log 'PASS: grow and shrink reflow are symmetric and window-relative geometry is preserved.' Green

    $ia=Resolve-LiveIdentity $a_id 'VDD-A before disconnect';Invoke-VmuDisconnectExact -Identity $ia
    if(-not(Wait-Until {(Test-VmuVddActive $a_id $false)-and(Test-VmuVddActive $b_id $true)} 'A disconnected while B remains active' 5000)){throw 'Disconnect isolation failed.'}
    Invoke-VmuReconnectSaved
    if(-not(Wait-Until {(Test-VmuVddActive $a_id $true)-and(Test-VmuVddActive $b_id $true)} 'A reconnected while B remains active' 5000)){throw 'Reconnect isolation failed.'}
    if(-not(Remove-OneVdd $a_id)){throw 'Could not uninstall A on first attempt.'};if(-not(Test-VmuVddActive $b_id $true)){throw 'Uninstalling A affected B.'};if(-not(Remove-OneVdd $b_id)){throw 'Could not uninstall B.'}
    Write-Log 'MULTI-VDD REFLOW ISOLATION: PASS' Green
    exit 0
}
catch {
    Write-Log "MULTI-VDD TEST ERROR: $($_.Exception.Message)" Red
    foreach($d in @(Get-Vdds)){Invoke-Native "$env:SystemRoot\System32\pnputil.exe" @('/remove-device',('"{0}"'-f$d.InstanceId)) -AllowFailure|Out-Null}
    exit 1
}
