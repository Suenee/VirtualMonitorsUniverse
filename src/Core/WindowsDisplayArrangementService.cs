using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Applies and restores active Windows display positions using the same documented
/// ChangeDisplaySettingsEx family that VMU already uses for display mode lifecycle.
/// Arrangement changes are position-only: the active display set must remain
/// identical before and after an apply operation.
/// </summary>
public sealed class WindowsDisplayArrangementService
{
    private const int EnumCurrentSettings = -1;
    private const uint DmPosition = 0x00000020;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;

    public IReadOnlyList<DisplayArrangementEntry> CaptureActive()
    {
        EnsureWindows();
        return GetActiveScreenNames()
            .Select(deviceName =>
            {
                var mode = ReadMode(deviceName);
                return new DisplayArrangementEntry(deviceName, mode.dmPositionX, mode.dmPositionY);
            })
            .ToArray();
    }

    public void Apply(IReadOnlyCollection<DisplayArrangementEntry> arrangement)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(arrangement);
        if (arrangement.Count == 0) throw new ArgumentException("At least one display position is required.", nameof(arrangement));

        var active = GetActiveScreenNames();
        var requested = arrangement
            .GroupBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

        if (requested.Count != arrangement.Count)
            throw new ArgumentException("Display arrangement contains duplicate device names.", nameof(arrangement));

        foreach (var deviceName in requested.Keys)
        {
            if (!active.Contains(deviceName))
                throw new InvalidOperationException($"Display '{deviceName}' is not currently active.");
        }

        if (requested.Count != active.Count || active.Any(x => !requested.ContainsKey(x)))
            throw new InvalidOperationException("Arrangement must contain exactly the currently active Windows displays.");

        ValidateConnectedDesktop(requested.Values);

        foreach (var entry in requested.Values)
        {
            var mode = ReadMode(entry.DeviceName);
            mode.dmPositionX = entry.X;
            mode.dmPositionY = entry.Y;
            mode.dmFields = DmPosition;
            var result = ChangeDisplaySettingsEx(entry.DeviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero);
            if (result != DispChangeSuccessful)
                throw new InvalidOperationException($"Windows rejected the staged position {entry.X},{entry.Y} for {entry.DeviceName} (result {result}).");
        }

        var applyResult = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (applyResult != DispChangeSuccessful)
            throw new InvalidOperationException($"Windows rejected the display arrangement commit (result {applyResult}).");

        RestoreUnexpectedlyActivatedDisplays(active);
        var finalActive = GetActiveScreenNames();
        if (finalActive.Count != active.Count || active.Any(x => !finalActive.Contains(x)))
            throw new InvalidOperationException("Windows changed the active display set while applying positions. The arrangement operation was rejected.");
    }

    private static void ValidateConnectedDesktop(IEnumerable<DisplayArrangementEntry> entries)
    {
        var nodes = entries.Select(entry =>
        {
            var mode = ReadMode(entry.DeviceName);
            return new DisplayRect(entry.DeviceName, entry.X, entry.Y, checked((int)mode.dmPelsWidth), checked((int)mode.dmPelsHeight));
        }).ToArray();
        if (nodes.Length <= 1) return;

        var visited = new HashSet<int> { 0 };
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            for (var i = 0; i < nodes.Length; i++)
            {
                if (visited.Contains(i) || !Touches(nodes[current], nodes[i])) continue;
                visited.Add(i);
                queue.Enqueue(i);
            }
        }

        if (visited.Count != nodes.Length)
            throw new InvalidOperationException("Display arrangement contains an isolated monitor. Every active monitor must touch the connected desktop area.");
    }

    private static bool Touches(DisplayRect a, DisplayRect b)
    {
        var horizontalGap = Math.Max(0, Math.Max(a.X - (b.X + b.Width), b.X - (a.X + a.Width)));
        var verticalGap = Math.Max(0, Math.Max(a.Y - (b.Y + b.Height), b.Y - (a.Y + a.Height)));
        return horizontalGap <= 1 && verticalGap <= 1;
    }

    private static void RestoreUnexpectedlyActivatedDisplays(HashSet<string> expectedActive)
    {
        var afterApply = GetActiveScreenNames();
        var unexpected = afterApply.Where(x => !expectedActive.Contains(x)).ToArray();
        if (unexpected.Length == 0) return;

        var modes = new WindowsDisplayModeService();
        foreach (var deviceName in unexpected) modes.Disconnect(deviceName);
    }

    private static DevMode ReadMode(string deviceName)
    {
        var mode = new DevMode { dmSize = checked((ushort)Marshal.SizeOf<DevMode>()) };
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
            throw new InvalidOperationException($"Cannot read the active display mode for {deviceName}.");
        return mode;
    }

    private static HashSet<string> GetActiveScreenNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var callback = new MonitorEnumProc((monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info) && !string.IsNullOrWhiteSpace(info.szDevice)) names.Add(info.szDevice);
            return true;
        });

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new InvalidOperationException("Windows active display enumeration failed.");

        GC.KeepAlive(callback);
        return names;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Display arrangement is available only on Windows.");
    }

    private sealed record DisplayRect(string DeviceName, int X, int Y, int Width, int Height);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public int rcMonitorLeft;
        public int rcMonitorTop;
        public int rcMonitorRight;
        public int rcMonitorBottom;
        public int rcWorkLeft;
        public int rcWorkTop;
        public int rcWorkRight;
        public int rcWorkBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);
}

public sealed record DisplayArrangementEntry(string DeviceName, int X, int Y);
