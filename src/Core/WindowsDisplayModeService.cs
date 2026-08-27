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

    private readonly Dictionary<string, DevMode> savedModes = new(StringComparer.OrdinalIgnoreCase);

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
        mode.dmFields = DmPelsWidth | DmPelsHeight;
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
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevices(null, index, ref device, 0); index++)
        {
            if (string.Equals(device.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return (device.StateFlags & 0x00000001) != 0;
            device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        }
        return false;
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
}

public sealed record DisplayModeInfo(int X, int Y, uint Width, uint Height, uint RefreshRate);
