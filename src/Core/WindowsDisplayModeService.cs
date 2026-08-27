using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Native C# port of the display-mode lifecycle proven by the ALPHA acceptance test.
/// </summary>
/// <remarks>
/// This class intentionally preserves the ALPHA Win32 contract: EnumDisplaySettings
/// reads current/registry DEVMODE values and ChangeDisplaySettingsEx performs mode,
/// disconnect, and reconnect operations with CDS_UPDATEREGISTRY.
/// </remarks>
public sealed class WindowsDisplayModeService
{
    private const int EnumCurrentSettings = -1;
    private const int EnumRegistrySettings = -2;
    private const uint DmPosition = 0x00000020;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const int DispChangeSuccessful = 0;
    private const int DisplayDeviceAttachedToDesktop = 0x00000001;
    private const string VddFriendlyName = "Virtual Display Driver";
    private const string VddPnpPrefix = "ROOT\\MTTVDD";

    private readonly Dictionary<string, DevMode> savedModes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WindowsDisplayInfo> GetDisplays()
    {
        EnsureWindows();
        var activeScreenNames = GetActiveScreenNames();
        var result = new List<WindowsDisplayInfo>();
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;

            var attached = activeScreenNames.Contains(device.DeviceName);
            DisplayModeInfo? mode = null;
            try
            {
                mode = GetMode(device.DeviceName, registry: !attached);
            }
            catch
            {
                // ALPHA treated display enumeration and mode inspection separately.
            }

            var isVirtual = string.Equals(device.DeviceString, VddFriendlyName, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(device.DeviceID) &&
                             device.DeviceID.StartsWith(VddPnpPrefix, StringComparison.OrdinalIgnoreCase));

            result.Add(new WindowsDisplayInfo(
                device.DeviceName,
                device.DeviceString,
                device.DeviceID,
                attached,
                isVirtual,
                mode));
        }

        return result;
    }

    public DisplayModeInfo GetMode(string deviceName, bool registry = false)
    {
        EnsureWindows();
        var mode = ReadMode(deviceName, registry ? EnumRegistrySettings : EnumCurrentSettings);
        return ToInfo(mode);
    }

    public void SetMode(string deviceName, uint width, uint height, uint refreshRate)
    {
        EnsureWindows();
        var mode = ReadMode(deviceName, EnumCurrentSettings);
        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmDisplayFrequency = refreshRate;
        mode.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        Apply(deviceName, ref mode, $"{width}x{height}@{refreshRate}");
        if (!WaitUntil(() => TestMode(deviceName, width, height), TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Timed out waiting for {width}x{height} on {deviceName}.");
    }

    public void Disconnect(string deviceName)
    {
        EnsureWindows();
        var current = ReadMode(deviceName, EnumCurrentSettings);
        savedModes[deviceName] = current;
        var mode = current;
        mode.dmPelsWidth = 0;
        mode.dmPelsHeight = 0;

        // Preserve the ALPHA disconnect that was actually validated before the
        // later Screen-enumeration refactor: DM_POSITION is part of dmFields when
        // submitting the 0x0 mode. Removing it changed Windows behavior on the
        // development machine even though ChangeDisplaySettingsEx returned success.
        mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;

        Apply(deviceName, ref mode, "disconnect");
        if (!WaitUntil(() => !IsAttached(deviceName), TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Timed out waiting for {deviceName} to disconnect.");
    }

    public void Reconnect(string deviceName)
    {
        EnsureWindows();
        DevMode mode;
        if (!savedModes.TryGetValue(deviceName, out mode))
        {
            mode = ReadMode(deviceName, EnumRegistrySettings);
            if (mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0)
            {
                mode.dmPelsWidth = 1920;
                mode.dmPelsHeight = 1080;
                mode.dmDisplayFrequency = 60;
            }
        }

        mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        Apply(deviceName, ref mode, "reconnect");
        if (!WaitUntil(() => IsAttached(deviceName), TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Timed out waiting for {deviceName} to reconnect.");
    }

    public bool IsAttached(string deviceName)
    {
        EnsureWindows();
        return GetActiveScreenNames().Contains(deviceName);
    }

    private static HashSet<string> GetActiveScreenNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var callback = new MonitorEnumProc((monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info) && !string.IsNullOrWhiteSpace(info.szDevice))
                names.Add(info.szDevice);
            return true;
        });

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new InvalidOperationException("Windows active display enumeration failed.");
        GC.KeepAlive(callback);
        return names;
    }

    private bool TestMode(string deviceName, uint width, uint height)
    {
        try
        {
            var mode = ReadMode(deviceName, EnumCurrentSettings);
            return mode.dmPelsWidth == width && mode.dmPelsHeight == height;
        }
        catch { return false; }
    }

    private static DevMode ReadMode(string deviceName, int modeIndex)
    {
        var mode = new DevMode { dmSize = checked((ushort)Marshal.SizeOf<DevMode>()) };
        if (!EnumDisplaySettings(deviceName, modeIndex, ref mode))
            throw new InvalidOperationException($"Cannot read display mode for {deviceName}.");
        return mode;
    }

    private static void Apply(string deviceName, ref DevMode mode, string description)
    {
        var result = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (result != DispChangeSuccessful)
            throw new InvalidOperationException($"Windows rejected {description} on {deviceName} (result {result}).");
    }

    private static DisplayModeInfo ToInfo(DevMode mode) => new(
        mode.dmPositionX, mode.dmPositionY, mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (condition()) return true;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return condition();
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Display mode management is supported only on Windows.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

public sealed record DisplayModeInfo(int X, int Y, uint Width, uint Height, uint RefreshRate);
public sealed record WindowsDisplayInfo(
    string DeviceName,
    string FriendlyName,
    string DeviceId,
    bool IsAttached,
    bool IsVirtual,
    DisplayModeInfo? Mode);
