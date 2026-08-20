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

    # ALPHA identity probe. This deliberately does not guess from DISPLAY numbers.
    # It enumerates CCD paths and records stable source/target adapter LUIDs,
    # source/target IDs, GDI source name, target monitor device path and adapter path.
    # The next step is to correlate those paths with the exact PnP VDD instance.
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

function Write-IdentitySnapshot {
    param([string]$Label)
    Write-Log "DISPLAYCONFIG IDENTITY SNAPSHOT: $Label" Cyan
    foreach ($line in @(Get-DisplayConfigIdentitySnapshot)) { Write-Log "CCD: $line" DarkGray }
    foreach ($device in @(Get-VddDevices)) {
        $hw = ''
        try { $hw = ((Get-PnpDeviceProperty -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction Stop).Data -join ',') } catch {}
        Write-Log ("PNP VDD: instance={0};status={1};hardwareIds={2}" -f $device.InstanceId,$device.Status,$hw) DarkGray
    }
}
'@

    $insertPoint = 'function Get-Mode {'
    if (-not $source.Contains($insertPoint)) { throw 'Could not locate Get-Mode insertion point.' }
    $source = $source.Replace($insertPoint, ($identityCode + "`n" + $insertPoint))

    # Replace pre-flight with a deterministic identity diagnostic. We do not proceed
    # to destructive per-monitor operations until the exact VDD<->CCD mapping is proven.
    $preflightPattern = '(?s)(Write-Section ''PRE-FLIGHT: CLEAN BASELINE AND ONE FRESH VDD''\n).*?(\n    Write-Section ''TEST 1: DYNAMIC RESOLUTION'')'
    $preflightReplacement = @'
$1    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    if ($existing.Count -gt 0) {
        Write-Log 'Previous ALPHA test VDD remnants detected; removing them before the new test.' Yellow
        if (-not (Remove-VddInstallation)) { throw 'Could not establish clean baseline.' }
    }

    Write-IdentitySnapshot 'BEFORE VDD INSTALL'
    Install-Vdd
    Write-IdentitySnapshot 'AFTER VDD INSTALL'

    $Results.Preflight = 'PASS'
    Write-Log 'IDENTITY PROBE COMPLETE: no display was modified because exact VDD-to-CCD mapping must be proven first.' Yellow
    throw 'IDENTITY_PROBE_COMPLETE'
$2
'@

    $patched = [regex]::Replace($source, $preflightPattern, $preflightReplacement, 1)
    if ($patched -eq $source) { throw 'Could not locate the ALPHA pre-flight section for identity probe patching.' }
    $source = $patched

    # Avoid duplicate final summaries from nested runtime patch generations.
    $source = $source -replace "(?<!`r)`n", "`r`n"
    Set-Content -LiteralPath $runtimePath -Value $source -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtimePath
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
}
