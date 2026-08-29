using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Native C# port of the display-mode lifecycle proven by the ALPHA acceptance test.
/// </summary>
/// <remarks>
/// The implementation deliberately follows the validated ALPHA behavior: top-level
/// EnumDisplayDevices enumeration, ALPHA virtual-display identity based on the device
/// string, EnumDisplaySettings for mode capture, and ChangeDisplaySettingsEx with
/// CDS_UPDATEREGISTRY for mode/disconnect/reconnect operations.
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
    private const uint CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;
    private const string VddPnpPrefix = "ROOT\\MTTVDD";

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
            try { mode = GetMode(device.DeviceName, registry: !attached); }
            catch { }

            var isVirtual = IsAlphaVirtualIdentity(device.DeviceString) ||
                            (!string.IsNullOrWhiteSpace(device.DeviceID) && device.DeviceID.StartsWith(VddPnpPrefix, StringComparison.OrdinalIgnoreCase));
            result.Add(new WindowsDisplayInfo(device.DeviceName, device.DeviceString, device.DeviceID, attached, isVirtual, mode));
        }
        return result;
    }

    public DisplayModeInfo GetMode(string deviceName, bool registry = false)
    {
        EnsureWindows();
        return ToInfo(ReadMode(deviceName, registry ? EnumRegistrySettings : EnumCurrentSettings));
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
        if (!IsAttached(deviceName))
            throw new InvalidOperationException($"{deviceName} is already disconnected; refusing to overwrite the saved ALPHA mode snapshot.");

        var current = ReadMode(deviceName, EnumCurrentSettings);
        if (current.dmPelsWidth == 0 || current.dmPelsHeight == 0)
            throw new InvalidOperationException($"{deviceName} returned an invalid active mode; refusing to disconnect.");

        SaveModeSnapshot(deviceName, current);
        var mode = current;
        mode.dmPelsWidth = 0;
        mode.dmPelsHeight = 0;
        mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;
        Apply(deviceName, ref mode, "disconnect");
        if (!WaitUntil(() => !IsAttached(deviceName), TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Timed out waiting for {deviceName} to disconnect.");
    }

    public void Reconnect(string deviceName)
    {
        EnsureWindows();
        if (IsAttached(deviceName)) throw new InvalidOperationException($"{deviceName} is already connected.");

        var hasSnapshot = TryLoadModeSnapshot(deviceName, out var saved);
        DevMode mode;
        if (hasSnapshot)
        {
            if (saved.dmPelsWidth == 0 || saved.dmPelsHeight == 0)
                throw new InvalidDataException($"Saved ALPHA mode for {deviceName} is 0x0 and cannot be used for reconnect.");
            mode = ReadMode(deviceName, EnumRegistrySettings);
            mode.dmPositionX = saved.dmPositionX;
            mode.dmPositionY = saved.dmPositionY;
            mode.dmPelsWidth = saved.dmPelsWidth;
            mode.dmPelsHeight = saved.dmPelsHeight;
            mode.dmDisplayFrequency = saved.dmDisplayFrequency;
        }
        else
        {
            mode = ReadMode(deviceName, EnumRegistrySettings);
            if (mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0)
            {
                mode.dmPelsWidth = 1920;
                mode.dmPelsHeight = 1080;
                mode.dmDisplayFrequency = 60;
            }
        }

        mode.dmSize = checked((ushort)Marshal.SizeOf<DevMode>());
        mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        ApplyReconnect(deviceName, ref mode, hasSnapshot ? "saved ALPHA snapshot" : "registry fallback");
        if (!WaitUntil(() => IsAttached(deviceName), TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Timed out waiting for {deviceName} to reconnect.");
        DeleteModeSnapshot(deviceName);
    }

    public bool IsAttached(string deviceName)
    {
        EnsureWindows();
        return GetActiveScreenNames().Contains(deviceName);
    }

    private static bool IsAlphaVirtualIdentity(string? deviceString)
    {
        if (string.IsNullOrWhiteSpace(deviceString)) return false;
        return deviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
               deviceString.Contains("MTT", StringComparison.OrdinalIgnoreCase) ||
               deviceString.Contains("VDD", StringComparison.OrdinalIgnoreCase);
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

    private static void ApplyReconnect(string deviceName, ref DevMode mode, string source)
    {
        var directResult = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (directResult == DispChangeSuccessful) return;

        // Some Windows display stacks reject the immediate reconnect after a saved CCD
        // topology has become stale (for example after the VMU process was restarted).
        // Preserve the validated DEVMODE first attempt, then use the documented staged
        // multi-display pattern: write this device with CDS_NORESET and globally apply.
        var stageResult = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero);
        if (stageResult == DispChangeSuccessful)
        {
            var applyResult = ChangeDisplaySettingsExGlobal(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (applyResult == DispChangeSuccessful) return;
            throw new InvalidOperationException(
                $"Windows rejected reconnect on {deviceName}: direct result {directResult}; staged write succeeded but global apply returned {applyResult}; " +
                $"source={source}; position={mode.dmPositionX},{mode.dmPositionY}; mode={mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency}; dmFields=0x{mode.dmFields:X8}.");
        }

        throw new InvalidOperationException(
            $"Windows rejected reconnect on {deviceName}: direct result {directResult}; staged write result {stageResult}; " +
            $"source={source}; position={mode.dmPositionX},{mode.dmPositionY}; mode={mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency}; dmFields=0x{mode.dmFields:X8}.");
    }

    private static void SaveModeSnapshot(string deviceName, DevMode mode)
    {
        var path = GetModeSnapshotPath(deviceName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, StructureToBytes(mode));
    }

    private static bool TryLoadModeSnapshot(string deviceName, out DevMode mode)
    {
        var path = GetModeSnapshotPath(deviceName);
        if (!File.Exists(path)) { mode = default; return false; }
        var bytes = File.ReadAllBytes(path);
        var expectedSize = Marshal.SizeOf<DevMode>();
        if (bytes.Length != expectedSize) throw new InvalidDataException($"Saved display mode for {deviceName} has an invalid size.");
        mode = BytesToStructure<DevMode>(bytes);
        return true;
    }

    private static void DeleteModeSnapshot(string deviceName)
    {
        var path = GetModeSnapshotPath(deviceName);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetModeSnapshotPath(string deviceName)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualMonitorsUniverse", "state");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceName)));
        return Path.Combine(root, $"display-mode-{hash}.bin");
    }

    private static byte[] StructureToBytes<T>(T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static T BytesToStructure<T>(byte[] bytes) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return Marshal.PtrToStructure<T>(pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static DisplayModeInfo ToInfo(DevMode mode) => new(mode.dmPositionX, mode.dmPositionY, mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do { if (condition()) return true; Thread.Sleep(100); } while (DateTime.UtcNow < deadline);
        return condition();
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Display mode management is supported only on Windows.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsExGlobal(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);

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
