using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Windows implementation of the VMU virtual-monitor API for the upstream
/// VirtualDrivers Virtual Display Driver (MttVDD).
/// </summary>
/// <remarks>
/// Runtime display-count changes use the driver's local named pipe. Dependency
/// diagnostics are read-only: VMU observes the Windows display-adapter state but
/// does not enable, disable, reinstall, or otherwise repair the driver during a
/// Core self-test.
/// </remarks>
public sealed class WindowsVirtualMonitorService : IVirtualMonitorService
{
    private const string PipeName = "MTTVirtualDisplayPipe";
    private static readonly TimeSpan DefaultPipeTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<VirtualMonitorInfo> GetMonitors()
    {
        EnsureWindows();

        var vddAdapters = DisplayAdapterApi.GetVddAdapters(onlyActive: true);
        if (vddAdapters.Count == 0)
        {
            return Array.Empty<VirtualMonitorInfo>();
        }

        var adapterByGdiName = vddAdapters
            .Where(adapter => !string.IsNullOrWhiteSpace(adapter.GdiName))
            .ToDictionary(adapter => adapter.GdiName, StringComparer.OrdinalIgnoreCase);

        return DisplayConfigApi.GetActivePaths()
            .Where(path => path.GdiName is not null && adapterByGdiName.ContainsKey(path.GdiName))
            .Select(path =>
            {
                var adapter = adapterByGdiName[path.GdiName!];
                return new VirtualMonitorInfo(
                    path.SourceKey,
                    path.GdiName,
                    adapter.PnpInstanceId,
                    true,
                    path.Width,
                    path.Height,
                    path.X,
                    path.Y);
            })
            .ToArray();
    }

    public VddDriverDiagnostics GetDriverDiagnostics(TimeSpan? pipeTimeout = null)
    {
        EnsureWindows();

        var adapters = DisplayAdapterApi.GetVddAdapters(onlyActive: false);
        var adapter = adapters
            .OrderByDescending(item => item.IsActive)
            .FirstOrDefault();
        var pipeAvailable = IsDriverAvailable(pipeTimeout ?? TimeSpan.FromMilliseconds(750));

        return adapter is null
            ? new VddDriverDiagnostics(false, false, pipeAvailable, null, null, null, 0)
            : new VddDriverDiagnostics(
                true,
                adapter.IsActive,
                pipeAvailable,
                adapter.GdiName,
                adapter.PnpInstanceId,
                adapter.FriendlyName,
                adapter.StateFlags);
    }

    public bool IsDriverAvailable(TimeSpan? timeout = null)
    {
        EnsureWindows();
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
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

        var response = SendPipeCommand($"SETDISPLAYCOUNT {count}", timeout ?? DefaultPipeTimeout);
        if (response.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"VDD rejected SETDISPLAYCOUNT {count}: {response}");
        }
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
            if (GetMonitors().Count == expectedCount)
            {
                return true;
            }

            Thread.Sleep(poll);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static string SendPipeCommand(string command, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(ToTimeoutMilliseconds(timeout));
        if (!pipe.IsConnected)
        {
            throw new IOException("Could not connect to the Virtual Display Driver named pipe.");
        }

        var payload = Encoding.Unicode.GetBytes(command);
        pipe.Write(payload, 0, payload.Length);
        pipe.Flush();

        var buffer = new byte[1024];
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            var read = pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return read > 0
                ? Encoding.Unicode.GetString(buffer, 0, read).TrimEnd('\0', '\r', '\n', ' ')
                : string.Empty;
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"Timed out waiting for VDD acknowledgement to '{command}'.", ex);
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero
            ? 1
            : (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalMilliseconds));

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Virtual Display Driver integration is supported only on Windows.");
        }
    }

    private static class DisplayAdapterApi
    {
        private const int DisplayDeviceAttachedToDesktop = 0x00000001;
        private const int DisplayDeviceActive = 0x00000001;
        private const string VddPnpPrefix = "ROOT\\MTTVDD";
        private const string VddFriendlyName = "Virtual Display Driver";

        public static IReadOnlyList<VddAdapter> GetVddAdapters(bool onlyActive)
        {
            var result = new List<VddAdapter>();
            for (uint index = 0; ; index++)
            {
                var device = new DisplayDevice
                {
                    cb = Marshal.SizeOf<DisplayDevice>()
                };

                if (!EnumDisplayDevices(null, index, ref device, 0))
                {
                    break;
                }

                var isActive = (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0 ||
                    (device.StateFlags & DisplayDeviceActive) != 0;
                if (onlyActive && !isActive)
                {
                    continue;
                }

                var isVdd = (!string.IsNullOrWhiteSpace(device.DeviceID) &&
                             device.DeviceID.StartsWith(VddPnpPrefix, StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(device.DeviceString, VddFriendlyName, StringComparison.OrdinalIgnoreCase);
                if (!isVdd)
                {
                    continue;
                }

                result.Add(new VddAdapter(
                    device.DeviceName,
                    device.DeviceID,
                    device.DeviceString,
                    device.StateFlags,
                    isActive));
            }

            return result;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(
            string? lpDevice,
            uint iDevNum,
            ref DisplayDevice lpDisplayDevice,
            uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int cb;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;

            public int StateFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        public sealed record VddAdapter(
            string GdiName,
            string? PnpInstanceId,
            string? FriendlyName,
            int StateFlags,
            bool IsActive);
    }

    private static class DisplayConfigApi
    {
        private const uint QdcOnlyActivePaths = 0x00000002;
        private const uint GetSourceName = 1;
        private const uint ModeInfoTypeSource = 1;

        public static IReadOnlyList<DisplayPath> GetActivePaths()
        {
            var result = GetDisplayConfigBufferSizes(
                QdcOnlyActivePaths,
                out var pathCount,
                out var modeCount);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"GetDisplayConfigBufferSizes failed with Win32 error {result}.");
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            result = QueryDisplayConfig(
                QdcOnlyActivePaths,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"QueryDisplayConfig failed with Win32 error {result}.");
            }

            var snapshots = new List<DisplayPath>((int)pathCount);
            for (var index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var sourceName = ReadSourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
                var sourceMode = TryReadSourceMode(path, modes, modeCount);
                if (sourceMode is null)
                {
                    continue;
                }

                snapshots.Add(new DisplayPath(
                    $"{FormatLuid(path.sourceInfo.adapterId)}/{path.sourceInfo.id}",
                    sourceName,
                    sourceMode.Value.position.x,
                    sourceMode.Value.position.y,
                    checked((int)sourceMode.Value.width),
                    checked((int)sourceMode.Value.height)));
            }

            return snapshots;
        }

        private static DisplayConfigSourceMode? TryReadSourceMode(
            DisplayConfigPathInfo path,
            DisplayConfigModeInfo[] modes,
            uint modeCount)
        {
            var index = path.sourceInfo.modeInfoIdx;
            return index >= modeCount || modes[index].infoType != ModeInfoTypeSource
                ? null
                : modes[index].modeInfo.sourceMode;
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

            return DisplayConfigGetDeviceInfo(ref packet) == 0
                ? packet.viewGdiDeviceName
                : null;
        }

        private static string FormatLuid(Luid value) =>
            $"{value.HighPart:X8}:{value.LowPart:X8}";

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DisplayConfigPathInfo[] pathInfoArray,
            ref uint modeInfoArrayElements,
            [Out] DisplayConfigModeInfo[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DisplayConfigSourceDeviceName requestPacket);

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

        public sealed record DisplayPath(
            string SourceKey,
            string? GdiName,
            int X,
            int Y,
            int Width,
            int Height);
    }
}
