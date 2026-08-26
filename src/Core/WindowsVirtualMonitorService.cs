using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Windows implementation of the VMU virtual-monitor API for the upstream
/// VirtualDrivers Virtual Display Driver (MttVDD).
/// </summary>
/// <remarks>
/// The implementation intentionally uses the driver's documented local named
/// pipe for display-count changes and Windows CCD (DisplayConfig) for identity
/// and verification. It does not move windows or reconfigure physical displays.
/// </remarks>
public sealed class WindowsVirtualMonitorService : IVirtualMonitorService
{
    private const string PipeName = "MTTVirtualDisplayPipe";
    private const string VddAdapterToken = "ROOT#MTTVDD#";
    private static readonly TimeSpan DefaultPipeTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<VirtualMonitorInfo> GetMonitors()
    {
        EnsureWindows();

        return DisplayConfigApi.GetPaths()
            .Where(path => path.IsVdd)
            .Select(path => new VirtualMonitorInfo(
                Id: path.SourceKey,
                GdiName: path.GdiName,
                PnpInstanceId: path.PnpInstanceId,
                IsConnected: path.IsActive,
                Width: path.Width,
                Height: path.Height,
                X: path.X,
                Y: path.Y))
            .ToArray();
    }

    public bool IsDriverAvailable(TimeSpan? timeout = null)
    {
        EnsureWindows();

        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.None);

        try
        {
            pipe.Connect(ToTimeoutMilliseconds(timeout ?? TimeSpan.FromMilliseconds(750)));
            return pipe.IsConnected;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void SetDisplayCount(int count, TimeSpan? timeout = null)
    {
        EnsureWindows();

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Display count cannot be negative.");
        }

        SendPipeCommand($"SETDISPLAYCOUNT {count}", timeout ?? DefaultPipeTimeout);
    }

    public bool WaitForConnectedCount(int expectedCount, TimeSpan timeout, TimeSpan? pollingInterval = null)
    {
        EnsureWindows();

        if (expectedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedCount));
        }

        var poll = pollingInterval ?? TimeSpan.FromMilliseconds(150);
        var deadline = DateTime.UtcNow + timeout;

        do
        {
            var count = GetMonitors().Count(monitor => monitor.IsConnected);
            if (count == expectedCount)
            {
                return true;
            }

            Thread.Sleep(poll);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static void SendPipeCommand(string command, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.None);

        pipe.Connect(ToTimeoutMilliseconds(timeout));
        if (!pipe.IsConnected)
        {
            throw new IOException("Could not connect to the Virtual Display Driver named pipe.");
        }

        // The upstream driver consumes wchar_t commands, therefore UTF-16 LE
        // must be used. This is the same protocol verified in the VMU ALPHA POC.
        var payload = Encoding.Unicode.GetBytes(command);
        pipe.Write(payload, 0, payload.Length);
        pipe.Flush();
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 1;
        }

        return (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalMilliseconds));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Virtual Display Driver integration is supported only on Windows.");
        }
    }

    private static class DisplayConfigApi
    {
        private const uint QdcAllPaths = 0x00000001;
        private const uint DisplayConfigPathActive = 0x00000001;
        private const uint GetSourceName = 1;
        private const uint GetTargetName = 2;
        private const uint GetAdapterName = 4;
        private const uint ModeInfoTypeSource = 1;

        public static IReadOnlyList<DisplayPath> GetPaths()
        {
            var result = GetDisplayConfigBufferSizes(QdcAllPaths, out var pathCount, out var modeCount);
            if (result != 0)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with Win32 error {result}.");
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            result = QueryDisplayConfig(QdcAllPaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result != 0)
            {
                throw new InvalidOperationException($"QueryDisplayConfig failed with Win32 error {result}.");
            }

            var snapshots = new List<DisplayPath>((int)pathCount);
            for (var index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var sourceName = ReadSourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
                var targetName = ReadTargetName(path.targetInfo.adapterId, path.targetInfo.id);
                var adapterPath = ReadAdapterName(path.targetInfo.adapterId, path.targetInfo.id);

                var sourceMode = TryReadSourceMode(path, modes, modeCount);
                var isVdd = !string.IsNullOrWhiteSpace(adapterPath) &&
                    adapterPath.IndexOf(VddAdapterToken, StringComparison.OrdinalIgnoreCase) >= 0;

                snapshots.Add(new DisplayPath(
                    SourceKey: $"{FormatLuid(path.sourceInfo.adapterId)}/{path.sourceInfo.id}",
                    GdiName: sourceName,
                    PnpInstanceId: TryExtractPnpInstanceId(adapterPath),
                    FriendlyName: targetName,
                    AdapterPath: adapterPath,
                    IsActive: (path.flags & DisplayConfigPathActive) != 0,
                    IsVdd: isVdd,
                    X: sourceMode?.position.x ?? 0,
                    Y: sourceMode?.position.y ?? 0,
                    Width: checked((int)(sourceMode?.width ?? 0)),
                    Height: checked((int)(sourceMode?.height ?? 0))));
            }

            return snapshots;
        }

        private static DisplayConfigSourceMode? TryReadSourceMode(
            DisplayConfigPathInfo path,
            DisplayConfigModeInfo[] modes,
            uint modeCount)
        {
            var modeIndex = path.sourceInfo.modeInfoIdx;
            if (modeIndex >= modeCount || modes[modeIndex].infoType != ModeInfoTypeSource)
            {
                return null;
            }

            return modes[modeIndex].modeInfo.sourceMode;
        }

        private static string? ReadSourceName(Luid adapterId, uint id)
        {
            var packet = new DisplayConfigSourceDeviceName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = GetSourceName,
                    size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                    adapterId = adapterId,
                    id = id
                }
            };

            return DisplayConfigGetDeviceInfo(ref packet) == 0 ? packet.viewGdiDeviceName : null;
        }

        private static string? ReadTargetName(Luid adapterId, uint id)
        {
            var packet = new DisplayConfigTargetDeviceName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = GetTargetName,
                    size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                    adapterId = adapterId,
                    id = id
                }
            };

            return DisplayConfigGetDeviceInfo(ref packet) == 0 ? packet.monitorFriendlyDeviceName : null;
        }

        private static string? ReadAdapterName(Luid adapterId, uint id)
        {
            var packet = new DisplayConfigAdapterName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = GetAdapterName,
                    size = (uint)Marshal.SizeOf<DisplayConfigAdapterName>(),
                    adapterId = adapterId,
                    id = id
                }
            };

            return DisplayConfigGetDeviceInfo(ref packet) == 0 ? packet.adapterDevicePath : null;
        }

        private static string? TryExtractPnpInstanceId(string? adapterPath)
        {
            if (string.IsNullOrWhiteSpace(adapterPath))
            {
                return null;
            }

            var tokenIndex = adapterPath.IndexOf(VddAdapterToken, StringComparison.OrdinalIgnoreCase);
            if (tokenIndex < 0)
            {
                return null;
            }

            var start = tokenIndex;
            var end = adapterPath.IndexOf("#{", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                end = adapterPath.Length;
            }

            var token = adapterPath[start..end];
            return token.Replace('#', '\\');
        }

        private static string FormatLuid(Luid value) => $"{value.HighPart:X8}:{value.LowPart:X8}";

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DisplayConfigPathInfo[] pathInfoArray,
            ref uint modeInfoArrayElements,
            [Out] DisplayConfigModeInfo[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdapterName requestPacket);

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
        private struct PointL
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigSourceMode
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public PointL position;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct DisplayConfigModeInfoUnion
        {
            [FieldOffset(0)]
            public DisplayConfigSourceMode sourceMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DisplayConfigModeInfo
        {
            public uint infoType;
            public uint id;
            public Luid adapterId;
            public DisplayConfigModeInfoUnion modeInfo;
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
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
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
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayConfigTargetDeviceName
        {
            public DisplayConfigDeviceInfoHeader header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayConfigAdapterName
        {
            public DisplayConfigDeviceInfoHeader header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string adapterDevicePath;
        }

        private sealed record DisplayPath(
            string SourceKey,
            string? GdiName,
            string? PnpInstanceId,
            string? FriendlyName,
            string? AdapterPath,
            bool IsActive,
            bool IsVdd,
            int X,
            int Y,
            int Width,
            int Height);
    }
}
