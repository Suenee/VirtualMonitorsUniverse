Set-StrictMode -Version Latest

$script:VmuSavedConfiguration = $null

function Ensure-VmuDisplayConfigTopologyApi {
    if ('Vmu.DisplayConfigTopologyApi' -as [type]) { return }

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace Vmu
{
    public static class DisplayConfigTopologyApi
    {
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

        private const UInt32 QDC_ALL_PATHS = 1;
        private const UInt32 QDC_ONLY_ACTIVE_PATHS = 2;
        private const UInt32 DISPLAYCONFIG_PATH_ACTIVE = 1;
        private const UInt32 GET_SOURCE = 1;
        private const UInt32 GET_TARGET = 2;
        private const UInt32 GET_ADAPTER = 4;
        private const UInt32 SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        private const UInt32 SDC_APPLY = 0x80;
        private const UInt32 SDC_SAVE_TO_DATABASE = 0x200;
        private const UInt32 SDC_ALLOW_CHANGES = 0x400;

        private static PATH[] savedPaths;
        private static MODE[] savedModes;
        private static UInt32 savedPathCount;
        private static UInt32 savedModeCount;

        [DllImport("user32.dll")] private static extern Int32 GetDisplayConfigBufferSizes(UInt32 flags, out UInt32 pathCount, out UInt32 modeCount);
        [DllImport("user32.dll")] private static extern Int32 QueryDisplayConfig(UInt32 flags, ref UInt32 pathCount, [Out] PATH[] paths, ref UInt32 modeCount, [Out] MODE[] modes, IntPtr topologyId);
        [DllImport("user32.dll")] private static extern Int32 SetDisplayConfig(UInt32 pathCount, PATH[] paths, UInt32 modeCount, MODE[] modes, UInt32 flags);
        [DllImport("user32.dll")] private static extern Int32 DisplayConfigGetDeviceInfo(ref SOURCE_NAME packet);
        [DllImport("user32.dll")] private static extern Int32 DisplayConfigGetDeviceInfo(ref TARGET_NAME packet);
        [DllImport("user32.dll")] private static extern Int32 DisplayConfigGetDeviceInfo(ref ADAPTER_NAME packet);

        private static string Luid(LUID value) { return value.HighPart.ToString("X8") + ":" + value.LowPart.ToString("X8"); }
        private static bool SameLuid(LUID value, String text) { return Luid(value).Equals(text, StringComparison.OrdinalIgnoreCase); }

        private static void Query(UInt32 flags, out PATH[] paths, out MODE[] modes, out UInt32 pathCount, out UInt32 modeCount)
        {
            int result = GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount);
            if (result != 0) throw new InvalidOperationException("GetDisplayConfigBufferSizes failed: " + result);
            paths = new PATH[pathCount];
            modes = new MODE[modeCount];
            result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException("QueryDisplayConfig failed: " + result);
        }

        public static string[] SnapshotAll()
        {
            PATH[] paths; MODE[] modes; UInt32 pc, mc;
            Query(QDC_ALL_PATHS, out paths, out modes, out pc, out mc);
            string[] result = new string[pc];
            for (int i = 0; i < pc; i++)
            {
                SOURCE_NAME source = new SOURCE_NAME();
                source.header.type = GET_SOURCE; source.header.size = (UInt32)Marshal.SizeOf(typeof(SOURCE_NAME)); source.header.adapterId = paths[i].sourceInfo.adapterId; source.header.id = paths[i].sourceInfo.id;
                int sr = DisplayConfigGetDeviceInfo(ref source);
                TARGET_NAME target = new TARGET_NAME();
                target.header.type = GET_TARGET; target.header.size = (UInt32)Marshal.SizeOf(typeof(TARGET_NAME)); target.header.adapterId = paths[i].targetInfo.adapterId; target.header.id = paths[i].targetInfo.id;
                int tr = DisplayConfigGetDeviceInfo(ref target);
                ADAPTER_NAME adapter = new ADAPTER_NAME();
                adapter.header.type = GET_ADAPTER; adapter.header.size = (UInt32)Marshal.SizeOf(typeof(ADAPTER_NAME)); adapter.header.adapterId = paths[i].targetInfo.adapterId; adapter.header.id = paths[i].targetInfo.id;
                int ar = DisplayConfigGetDeviceInfo(ref adapter);
                result[i] = "active=" + ((paths[i].flags & DISPLAYCONFIG_PATH_ACTIVE) != 0)
                    + ";sourceLuid=" + Luid(paths[i].sourceInfo.adapterId) + ";sourceId=" + paths[i].sourceInfo.id
                    + ";targetLuid=" + Luid(paths[i].targetInfo.adapterId) + ";targetId=" + paths[i].targetInfo.id
                    + ";gdi=" + (sr == 0 ? source.viewGdiDeviceName : "")
                    + ";friendly=" + (tr == 0 ? target.monitorFriendlyDeviceName : "")
                    + ";monitorPath=" + (tr == 0 ? target.monitorDevicePath : "")
                    + ";adapterPath=" + (ar == 0 ? adapter.adapterDevicePath : "")
                    + ";rotation=" + paths[i].targetInfo.rotation
                    + ";scaling=" + paths[i].targetInfo.scaling
                    + ";refreshNum=" + paths[i].targetInfo.refreshRate.Numerator
                    + ";refreshDen=" + paths[i].targetInfo.refreshRate.Denominator
                    + ";scanLineOrdering=" + paths[i].targetInfo.scanLineOrdering
                    + ";sr=" + sr + ";tr=" + tr + ";ar=" + ar;
            }
            return result;
        }

        public static int DisconnectExact(string sourceLuid, UInt32 sourceId, string targetLuid, UInt32 targetId)
        {
            PATH[] paths; MODE[] modes; UInt32 pc, mc;
            Query(QDC_ONLY_ACTIVE_PATHS, out paths, out modes, out pc, out mc);
            int matches = 0;
            for (int i = 0; i < pc; i++)
            {
                if (SameLuid(paths[i].sourceInfo.adapterId, sourceLuid) && paths[i].sourceInfo.id == sourceId && SameLuid(paths[i].targetInfo.adapterId, targetLuid) && paths[i].targetInfo.id == targetId)
                {
                    matches++;
                    paths[i].flags &= ~DISPLAYCONFIG_PATH_ACTIVE;
                }
            }
            if (matches != 1) throw new InvalidOperationException("Expected exactly one active CCD path to disconnect, found " + matches + ".");

            savedPaths = (PATH[])paths.Clone();
            savedModes = (MODE[])modes.Clone();
            savedPathCount = pc;
            savedModeCount = mc;
            for (int i = 0; i < pc; i++)
            {
                if (SameLuid(savedPaths[i].sourceInfo.adapterId, sourceLuid) && savedPaths[i].sourceInfo.id == sourceId && SameLuid(savedPaths[i].targetInfo.adapterId, targetLuid) && savedPaths[i].targetInfo.id == targetId)
                    savedPaths[i].flags |= DISPLAYCONFIG_PATH_ACTIVE;
            }

            UInt32 flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES;
            return SetDisplayConfig(pc, paths, mc, modes, flags);
        }

        public static int ReconnectSaved()
        {
            if (savedPaths == null || savedModes == null) throw new InvalidOperationException("No saved display topology exists for reconnect.");
            UInt32 flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES;
            return SetDisplayConfig(savedPathCount, savedPaths, savedModeCount, savedModes, flags);
        }
    }
}
"@
}

function Get-VmuCcdField {
    param([Parameter(Mandatory = $true)][string]$Line, [Parameter(Mandatory = $true)][string]$Name)
    foreach ($part in ($Line -split ';')) {
        $pair = $part -split '=', 2
        if ($pair.Count -eq 2 -and $pair[0] -eq $Name) { return $pair[1] }
    }
    return ''
}

function Get-VmuVddCcdPaths {
    param([Parameter(Mandatory = $true)][string]$InstanceId)
    Ensure-VmuDisplayConfigTopologyApi
    $token = $InstanceId.Replace('\', '#')
    return @([Vmu.DisplayConfigTopologyApi]::SnapshotAll() | Where-Object {
        $adapterPath = Get-VmuCcdField -Line $_ -Name 'adapterPath'
        -not [string]::IsNullOrWhiteSpace($adapterPath) -and $adapterPath.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -ge 0
    })
}

function Get-VmuActiveVddCcdPath {
    param([Parameter(Mandatory = $true)][string]$InstanceId)
    $active = @(Get-VmuVddCcdPaths -InstanceId $InstanceId | Where-Object { (Get-VmuCcdField -Line $_ -Name 'active') -eq 'True' })
    if ($active.Count -ne 1) { return $null }
    return $active[0]
}

function Resolve-VmuVddIdentity {
    param([Parameter(Mandatory = $true)]$Device)
    $paths = @(Get-VmuVddCcdPaths -InstanceId $Device.InstanceId)
    $active = @($paths | Where-Object { (Get-VmuCcdField -Line $_ -Name 'active') -eq 'True' })
    if ($active.Count -ne 1) { throw "Expected exactly one active CCD path for $($Device.InstanceId), found $($active.Count)." }
    $line = $active[0]
    return [pscustomobject]@{
        InstanceId = $Device.InstanceId
        GdiName = Get-VmuCcdField -Line $line -Name 'gdi'
        FriendlyName = Get-VmuCcdField -Line $line -Name 'friendly'
        SourceLuid = Get-VmuCcdField -Line $line -Name 'sourceLuid'
        SourceId = [uint32](Get-VmuCcdField -Line $line -Name 'sourceId')
        TargetLuid = Get-VmuCcdField -Line $line -Name 'targetLuid'
        TargetId = [uint32](Get-VmuCcdField -Line $line -Name 'targetId')
        MonitorPath = Get-VmuCcdField -Line $line -Name 'monitorPath'
        AdapterPath = Get-VmuCcdField -Line $line -Name 'adapterPath'
    }
}

function Test-VmuVddActive {
    param([Parameter(Mandatory = $true)][string]$InstanceId, [Parameter(Mandatory = $true)][bool]$Expected)
    $paths = @(Get-VmuVddCcdPaths -InstanceId $InstanceId)
    $activeCount = @($paths | Where-Object { (Get-VmuCcdField -Line $_ -Name 'active') -eq 'True' }).Count
    if ($Expected) { return ($activeCount -eq 1) }
    return ($activeCount -eq 0)
}

function Get-VmuConfigurationSnapshot {
    param([Parameter(Mandatory = $true)]$Identity)

    $mode = Get-Mode -DeviceName $Identity.GdiName
    $path = Get-VmuActiveVddCcdPath -InstanceId $Identity.InstanceId
    if ($null -eq $path) { throw "Cannot snapshot configuration: exact VDD CCD path is not uniquely active." }

    return [pscustomobject]@{
        InstanceId = $Identity.InstanceId
        GdiName = $Identity.GdiName
        X = [int]$mode.dmPositionX
        Y = [int]$mode.dmPositionY
        Width = [uint32]$mode.dmPelsWidth
        Height = [uint32]$mode.dmPelsHeight
        RefreshRate = [uint32]$mode.dmDisplayFrequency
        Orientation = [uint32]$mode.dmDisplayOrientation
        FixedOutput = [uint32]$mode.dmDisplayFixedOutput
        Rotation = Get-VmuCcdField -Line $path -Name 'rotation'
        Scaling = Get-VmuCcdField -Line $path -Name 'scaling'
        CcdRefreshNum = Get-VmuCcdField -Line $path -Name 'refreshNum'
        CcdRefreshDen = Get-VmuCcdField -Line $path -Name 'refreshDen'
        ScanLineOrdering = Get-VmuCcdField -Line $path -Name 'scanLineOrdering'
    }
}

function Compare-VmuConfigurationSnapshots {
    param([Parameter(Mandatory = $true)]$Before, [Parameter(Mandatory = $true)]$After)

    $fields = @('InstanceId','GdiName','X','Y','Width','Height','RefreshRate','Orientation','FixedOutput','Rotation','Scaling','CcdRefreshNum','CcdRefreshDen','ScanLineOrdering')
    $differences = @()
    foreach ($field in $fields) {
        if ([string]$Before.$field -ne [string]$After.$field) {
            $differences += ('{0}: {1} -> {2}' -f $field,$Before.$field,$After.$field)
        }
    }
    return @($differences)
}

function Invoke-VmuDisconnectExact {
    param([Parameter(Mandatory = $true)]$Identity)
    Ensure-VmuDisplayConfigTopologyApi

    $script:VmuSavedConfiguration = Get-VmuConfigurationSnapshot -Identity $Identity
    Write-Log ("CONFIG SNAPSHOT before disconnect: position=({0},{1}); mode={2}x{3}@{4}; orientation={5}; rotation={6}; scaling={7}" -f $script:VmuSavedConfiguration.X,$script:VmuSavedConfiguration.Y,$script:VmuSavedConfiguration.Width,$script:VmuSavedConfiguration.Height,$script:VmuSavedConfiguration.RefreshRate,$script:VmuSavedConfiguration.Orientation,$script:VmuSavedConfiguration.Rotation,$script:VmuSavedConfiguration.Scaling) DarkGray

    $result = [Vmu.DisplayConfigTopologyApi]::DisconnectExact($Identity.SourceLuid, [uint32]$Identity.SourceId, $Identity.TargetLuid, [uint32]$Identity.TargetId)
    if ($result -ne 0) { throw "SetDisplayConfig disconnect failed with result $result." }
}

function Invoke-VmuReconnectSaved {
    Ensure-VmuDisplayConfigTopologyApi
    $result = [Vmu.DisplayConfigTopologyApi]::ReconnectSaved()
    if ($result -ne 0) { throw "SetDisplayConfig reconnect failed with result $result." }

    if ($null -eq $script:VmuSavedConfiguration) { throw 'Reconnect completed, but no VMU configuration snapshot exists.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        if (Test-VmuVddActive -InstanceId $script:VmuSavedConfiguration.InstanceId -Expected $true) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    $identity = [pscustomobject]@{ InstanceId=$script:VmuSavedConfiguration.InstanceId; GdiName=$script:VmuSavedConfiguration.GdiName }
    $after = Get-VmuConfigurationSnapshot -Identity $identity
    $differences = @(Compare-VmuConfigurationSnapshots -Before $script:VmuSavedConfiguration -After $after)
    if ($differences.Count -gt 0) {
        foreach ($difference in $differences) { Write-Log "CONFIG DIFFERENCE after reconnect: $difference" Yellow }
        throw ('Virtual monitor configuration was not fully restored after reconnect ({0} differences).' -f $differences.Count)
    }

    Write-Log ("PASS: full VDD configuration restored after reconnect: position=({0},{1}); mode={2}x{3}@{4}; orientation={5}; rotation={6}; scaling={7}" -f $after.X,$after.Y,$after.Width,$after.Height,$after.RefreshRate,$after.Orientation,$after.Rotation,$after.Scaling) Green
}
