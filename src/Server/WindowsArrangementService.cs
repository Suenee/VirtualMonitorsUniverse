using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Server;

internal sealed record WindowsArrangementDisplay(int WindowsNumber, string DeviceName, int X, int Y, int Width, int Height, bool Primary);

/// <summary>
/// Reads the active Windows desktop arrangement directly from User32 so browser
/// polling sees native Settings changes immediately rather than depending on any
/// process-local Screen cache. Windows does not expose the Settings app Identify
/// label as a documented stable identifier; enumeration order remains best effort.
/// </summary>
internal static class WindowsArrangementService
{
    private const uint MonitorInfoPrimary = 0x00000001;

    public static IReadOnlyList<WindowsArrangementDisplay> GetActive()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var displays = new List<WindowsArrangementDisplay>();
        var callback = new MonitorEnumProc((monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.szDevice)) return true;
            displays.Add(new WindowsArrangementDisplay(
                displays.Count + 1,
                info.szDevice,
                info.rcMonitorLeft,
                info.rcMonitorTop,
                info.rcMonitorRight - info.rcMonitorLeft,
                info.rcMonitorBottom - info.rcMonitorTop,
                (info.dwFlags & MonitorInfoPrimary) != 0));
            return true;
        });

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new InvalidOperationException("Windows active display enumeration failed.");

        GC.KeepAlive(callback);
        return displays.ToArray();
    }

    public static int? GetWindowsNumber(string? deviceName)
        => string.IsNullOrWhiteSpace(deviceName) ? null : GetActive().FirstOrDefault(x => x.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))?.WindowsNumber;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

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

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
