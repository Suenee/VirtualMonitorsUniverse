[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'alfatest-v2.ps1'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runtimeDir = Join-Path $repoRoot '.runtime\alpha'
$runtimePath = Join-Path $runtimeDir 'alfatest.runtime.ps1'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "ALPHA test source not found: $sourcePath"
}

New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

try {
    $source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8
    $source = $source -replace "`r`n", "`n"
    $source = $source.Replace('"Using cached $Label: $Path"', '"Using cached ${Label}: $Path"')

    # Exact identity chain:
    # PnP VDD instance -> CCD adapter path -> adapter LUID/source/target -> GDI display name.
    # The same identity is also used to verify active/inactive topology state.
    $identityCode = @'
function Ensure-DisplayConfigIdentityApi {
    if ('Vmu.DisplayConfigIdentityApi' -as [type]) { return }

    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Vmu {
 public static class DisplayConfigIdentityApi {
  [StructLayout(LayoutKind.Sequential)] public struct LUID { public UInt32 LowPart; public Int32 HighPart; }
  [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public UInt32 Numerator; public UInt32 Denominator; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH_SOURCE { public LUID adapterId; public UInt32 id; public UInt32 modeInfoIdx; public UInt32 statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH_TARGET { public LUID adapterId; public UInt32 id; public UInt32 modeInfoIdx; public UInt32 outputTechnology; public UInt32 rotation; public UInt32 scaling; public RATIONAL refreshRate; public UInt32 scanLineOrdering; [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public UInt32 statusFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PATH { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public UInt32 flags; }
  [StructLayout(LayoutKind.Sequential)] public struct MODE { public UInt32 infoType; public UInt32 id; public LUID adapterId; [MarshalAs(UnmanagedType.ByValArray, SizeConst=64)] public byte[] data; }
  [StructLayout(LayoutKind.Sequential)] public struct HEADER { public UInt32 type; public UInt32 size; public LUID adapterId; public UInt32 id; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct SOURCE_NAME { public HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string viewGdiDeviceName; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct TARGET_NAME { public HEADER header; public UInt32 flags; public UInt32 outputTechnology; public UInt16 edidManufactureId; public UInt16 edidProductCodeId; public UInt32 connectorInstance; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string monitorFriendlyDeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string monitorDevicePath; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct ADAPTER_NAME { public HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string adapterDevicePath; }

  const UInt32 QDC_ALL_PATHS=1, GET_SOURCE=1, GET_TARGET=2, GET_ADAPTER=4;
  [DllImport("user32.dll")] static extern Int32 GetDisplayConfigBufferSizes(UInt32 flags, out UInt32 paths, out UInt32 modes);
  [DllImport("user32.dll")] static extern Int32 QueryDisplayConfig(UInt32 flags, ref UInt32 paths, [Out] PATH[] pathArray, ref UInt32 modes, [Out] MODE[] modeArray, IntPtr topologyId);
  [DllImport("user32.dll")] static extern Int32 DisplayConfigGetDeviceInfo(ref SOURCE_NAME packet);
  [DllImport("user32.dll")] static extern Int32 DisplayConfigGetDeviceInfo(ref TARGET_NAME packet);
  [DllImport("user32.dll")] static extern Int32 DisplayConfigGetDeviceInfo(ref ADAPTER_NAME packet);

  static string Luid(LUID v) { return v.HighPart.ToString("X8") + ":" + v.LowPart.ToString("X8"); }
  public static string[] Snapshot() {
   UInt32 pc,mc; int r=GetDisplayConfigBufferSizes(QDC_ALL_PATHS,out pc,out mc); if(r!=0) throw new Exception("GetDisplayConfigBufferSizes="+r);
   var p=new PATH[pc]; var m=new MODE[mc]; r=QueryDisplayConfig(QDC_ALL_PATHS,ref pc,p,ref mc,m,IntPtr.Zero); if(r!=0) throw new Exception("QueryDisplayConfig="+r);
   var lines=new List<string>();
   for(int i=0;i<pc;i++) {
    var s=new SOURCE_NAME(); s.header.type=GET_SOURCE; s.header.size=(UInt32)Marshal.SizeOf(typeof(SOURCE_NAME)); s.header.adapterId=p[i].sourceInfo.adapterId; s.header.id=p[i].sourceInfo.id; int sr=DisplayConfigGetDeviceInfo(ref s);
    var t=new TARGET_NAME(); t.header.type=GET_TARGET; t.header.size=(UInt32)Marshal.SizeOf(typeof(TARGET_NAME)); t.header.adapterId=p[i].targetInfo.adapterId; t.header.id=p[i].targetInfo.id; int tr=DisplayConfigGetDeviceInfo(ref t);
    var a=new ADAPTER_NAME(); a.header.type=GET_ADAPTER; a.header.size=(UInt32)Marshal.SizeOf(typeof(ADAPTER_NAME)); a.header.adapterId=p[i].targetInfo.adapterId; a.header.id=p[i].targetInfo.id; int ar=DisplayConfigGetDeviceInfo(ref a);
    lines.Add("active="+((p[i].flags&1)!=0)+";sourceLuid="+Luid(p[i].sourceInfo.adapterId)+";sourceId="+p[i].sourceInfo.id+";targetLuid="+Luid(p[i].targetInfo.adapterId)+";targetId="+p[i].targetInfo.id+";gdi="+(sr==0?s.viewGdiDeviceName:"")+";friendly="+(tr==0?t.monitorFriendlyDeviceName:"")+";monitorPath="+(tr==0?t.monitorDevicePath:"")+";adapterPath="+(ar==0?a.adapterDevicePath:"")+";sr="+sr+";tr="+tr+";ar="+ar);
   }
   return lines.ToArray();
  }
 }
}
"@
}

function Get-DisplayConfigIdentitySnapshot {
    Ensure-DisplayConfigIdentityApi
    return @([Vmu.DisplayConfigIdentityApi]::Snapshot())
}

function Get-CcdField {
    param([string]$Line, [string]$Name)
    foreach ($part in ($Line -split ';')) {
        $pair = $part -split '=', 2
        if ($pair.Count -eq 2 -and $pair[0] -eq $Name) { return $pair[1] }
    }
    return ''
}

function Get-VddCcdPaths {
    param([Parameter(Mandatory = $true)][string]$InstanceId)
    $instanceToken = $InstanceId.Replace('\', '#')
    return @((Get-DisplayConfigIdentitySnapshot) | Where-Object {
        $adapterPath = Get-CcdField $_ 'adapterPath'
        -not [string]::IsNullOrWhiteSpace($adapterPath) -and
        $adapterPath.IndexOf($instanceToken, [StringComparison]::OrdinalIgnoreCase) -ge 0
    })
}

function Resolve-VddDisplayIdentity {
    param([Parameter(Mandatory = $true)]$Device)
    $adapterMatches = @(Get-VddCcdPaths -InstanceId $Device.InstanceId)
    if ($adapterMatches.Count -eq 0) { throw "No CCD adapter path maps to PnP VDD instance $($Device.InstanceId)." }
    $activeMatches = @($adapterMatches | Where-Object { (Get-CcdField $_ 'active') -eq 'True' })
    if ($activeMatches.Count -ne 1) {
        foreach ($line in $adapterMatches) { Write-Log "VDD CCD CANDIDATE: $line" Yellow }
        throw "Expected exactly one active CCD path for $($Device.InstanceId), found $($activeMatches.Count)."
    }
    $line = $activeMatches[0]
    $gdi = Get-CcdField $line 'gdi'
    if ([string]::IsNullOrWhiteSpace($gdi)) { throw "The active CCD path for $($Device.InstanceId) has no GDI display name." }
    return [pscustomobject]@{
        InstanceId=$Device.InstanceId; GdiName=$gdi; FriendlyName=(Get-CcdField $line 'friendly');
        SourceLuid=(Get-CcdField $line 'sourceLuid'); SourceId=(Get-CcdField $line 'sourceId');
        TargetLuid=(Get-CcdField $line 'targetLuid'); TargetId=(Get-CcdField $line 'targetId');
        MonitorPath=(Get-CcdField $line 'monitorPath'); AdapterPath=(Get-CcdField $line 'adapterPath'); Raw=$line
    }
}

function Test-VddCcdActive {
    param([Parameter(Mandatory = $true)][string]$InstanceId, [Parameter(Mandatory = $true)][bool]$Expected)
    $paths = @(Get-VddCcdPaths -InstanceId $InstanceId)
    $activeCount = @($paths | Where-Object { (Get-CcdField $_ 'active') -eq 'True' }).Count
    if ($Expected) { return ($activeCount -eq 1) }
    return ($activeCount -eq 0)
}
'@

    $insertPoint = 'function Get-Mode {'
    if (-not $source.Contains($insertPoint)) { throw 'Could not locate Get-Mode insertion point.' }
    $source = $source.Replace($insertPoint, ($identityCode + "`n" + $insertPoint))

    $preflightPattern = '(?s)(Write-Section ''PRE-FLIGHT: CLEAN BASELINE AND ONE FRESH VDD''\n).*?(\n    Write-Section ''TEST 1: DYNAMIC RESOLUTION'')'
    $preflightReplacement = @'
$1    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    if ($existing.Count -gt 0) {
        Write-Log 'Previous ALPHA test VDD remnants detected; removing them before the new test.' Yellow
        if (-not (Remove-VddInstallation)) { throw 'Could not establish clean baseline.' }
    }

    Install-Vdd
    $vddDevices = @(Get-VddDevices)
    if ($vddDevices.Count -ne 1) { throw "Expected one VDD device for identity mapping, found $($vddDevices.Count)." }
    $vddInstanceId = $vddDevices[0].InstanceId
    $identity = Resolve-VddDisplayIdentity -Device $vddDevices[0]
    $name = $identity.GdiName
    Write-Log ("VDD IDENTITY: instance={0};gdi={1};friendly={2};source={3}/{4};target={5}/{6};monitorPath={7};adapterPath={8}" -f $identity.InstanceId,$identity.GdiName,$identity.FriendlyName,$identity.SourceLuid,$identity.SourceId,$identity.TargetLuid,$identity.TargetId,$identity.MonitorPath,$identity.AdapterPath) Green
    Write-Log 'PASS: exact PnP VDD instance mapped to exactly one active CCD/GDI display.' Green
    $Results.Preflight = 'PASS'
$2
'@
    $patched = [regex]::Replace($source, $preflightPattern, $preflightReplacement, 1)
    if ($patched -eq $source) { throw 'Could not locate the ALPHA pre-flight section for VDD identity patching.' }
    $source = $patched

    # The VDD must remain installed while its CCD path becomes inactive. Screen.AllScreens
    # is not authoritative for this state, so replace only the verification logic.
    $oldDisconnectWait = 'if (-not (Wait-Until -Description "$DeviceName disconnected from desktop" -TimeoutMs 5000 -Condition { Test-DisplayAttached $DeviceName $false })) {'
    $newDisconnectWait = 'if (-not (Wait-Until -Description "$DeviceName CCD path inactive while PnP VDD remains installed" -TimeoutMs 5000 -Condition { (Test-VddCcdActive -InstanceId $script:VddInstanceId -Expected $false) -and (@(Get-VddDevices | Where-Object { $_.InstanceId -eq $script:VddInstanceId }).Count -eq 1) })) {'
    if (-not $source.Contains($oldDisconnectWait)) { throw 'Could not locate disconnect verification block.' }
    $source = $source.Replace($oldDisconnectWait, $newDisconnectWait)
    $source = $source.Replace('throw "Timed out waiting for $DeviceName to disconnect."', 'throw "Timed out waiting for $DeviceName CCD path to become inactive while VDD remained installed."')

    $oldReconnectWait = 'if (-not (Wait-Until -Description "$DeviceName reconnected to desktop" -TimeoutMs 5000 -Condition { Test-DisplayAttached $DeviceName $true })) {'
    $newReconnectWait = 'if (-not (Wait-Until -Description "$DeviceName exact VDD CCD path active again" -TimeoutMs 5000 -Condition { Test-VddCcdActive -InstanceId $script:VddInstanceId -Expected $true })) {'
    if (-not $source.Contains($oldReconnectWait)) { throw 'Could not locate reconnect verification block.' }
    $source = $source.Replace($oldReconnectWait, $newReconnectWait)
    $source = $source.Replace('throw "Timed out waiting for $DeviceName to reconnect."', 'throw "Timed out waiting for the exact VDD CCD path to become active again."')

    # Publish the exact instance identity to Disconnect/Reconnect helper functions.
    $source = $source.Replace("    `$Results.Preflight = 'PASS'`n", "    `$script:VddInstanceId = `$vddInstanceId`n    `$Results.Preflight = 'PASS'`n")

    $source = $source -replace "(?<!`r)`n", "`r`n"
    Set-Content -LiteralPath $runtimePath -Value $source -Encoding UTF8
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtimePath
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
}
