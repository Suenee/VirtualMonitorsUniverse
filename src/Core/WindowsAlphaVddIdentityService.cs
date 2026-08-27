using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Resolves a VDD PnP instance to its live CCD/GDI identity using the exact
/// mapping rule from the final ALPHA displayconfig-topology helper.
/// </summary>
public sealed class WindowsAlphaVddIdentityService
{
    private const uint QdcAllPaths = 1;
    private const uint DisplayConfigPathActive = 1;
    private const uint GetSourceName = 1;
    private const uint GetAdapterName = 4;

    public WindowsVddIdentity ResolveActive(string instanceId)
    {
        EnsureWindows();
        var matches = GetMatchingPaths(instanceId)
            .Where(item => item.IsActive)
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one active CCD path for {instanceId}, found {matches.Length}.");
        }

        return matches[0];
    }

    public bool IsActive(string instanceId)
    {
        EnsureWindows();
        return GetMatchingPaths(instanceId).Count(item => item.IsActive) == 1;
    }

    private static IReadOnlyList<WindowsVddIdentity> GetMatchingPaths(string instanceId)
    {
        var token = instanceId.Replace('\\', '#');
        Query(out var paths, out _);
        var result = new List<WindowsVddIdentity>();

        foreach (var path in paths)
        {
            var adapterPath = GetAdapterPath(path);
            if (string.IsNullOrWhiteSpace(adapterPath) ||
                adapterPath.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            result.Add(new WindowsVddIdentity(
                instanceId,
                GetSourceNameForPath(path),
                LuidText(path.sourceInfo.adapterId),
                path.sourceInfo.id,
                LuidText(path.targetInfo.adapterId),
                path.targetInfo.id,
                adapterPath,
                (path.flags & DisplayConfigPathActive) != 0));
        }

        return result;
    }

    private static string GetSourceNameForPath(DisplayConfigPathInfo path)
    {
        var packet = new DisplayConfigSourceDeviceName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = GetSourceName,
                size = checked((uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>()),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id
            }
        };

        return DisplayConfigGetDeviceInfo(ref packet) == 0
            ? packet.viewGdiDeviceName ?? string.Empty
            : string.Empty;
    }

    private static string GetAdapterPath(DisplayConfigPathInfo path)
    {
        var packet = new DisplayConfigAdapterName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = GetAdapterName,
                size = checked((uint)Marshal.SizeOf<DisplayConfigAdapterName>()),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id
            }
        };

        return DisplayConfigGetDeviceInfo(ref packet) == 0
            ? packet.adapterDevicePath ?? string.Empty
            : string.Empty;
    }

    private static void Query(out DisplayConfigPathInfo[] paths, out DisplayConfigModeInfo[] modes)
    {
        var result = GetDisplayConfigBufferSizes(QdcAllPaths, out var pathCount, out var modeCount);
        if (result != 0)
            throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {result}");

        var pathBuffer = new DisplayConfigPathInfo[pathCount];
        var modeBuffer = new DisplayConfigModeInfo[modeCount];
        result = QueryDisplayConfig(QdcAllPaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
        if (result != 0)
            throw new InvalidOperationException($"QueryDisplayConfig failed: {result}");

        paths = pathBuffer.Take(checked((int)pathCount)).ToArray();
        modes = modeBuffer.Take(checked((int)modeCount)).ToArray();
    }

    private static string LuidText(Luid value) => $"{value.HighPart:X8}:{value.LowPart:X8}";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VDD identity resolution is supported only on Windows.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public Rational refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigAdapterName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName packet);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdapterName packet);
}

public sealed record WindowsVddIdentity(
    string InstanceId,
    string GdiName,
    string SourceLuid,
    uint SourceId,
    string TargetLuid,
    uint TargetId,
    string AdapterPath,
    bool IsActive);
