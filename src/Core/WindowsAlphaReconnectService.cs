using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Implements the reconnect sequence from the original validated ALPHA acceptance test.
/// </summary>
/// <remarks>
/// Keep this implementation intentionally literal until the C# port passes the same
/// acceptance scenario. The ALPHA sequence reads ENUM_REGISTRY_SETTINGS after the
/// display has been disconnected, substitutes 1920x1080 @ 60 Hz only when the stored
/// size is 0x0, sets DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT |
/// DM_DISPLAYFREQUENCY, and submits the mode with CDS_UPDATEREGISTRY.
/// </remarks>
public sealed class WindowsAlphaReconnectService
{
    private const int EnumRegistrySettings = -2;
    private const uint DmPosition = 0x00000020;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const int DispChangeSuccessful = 0;

    public void Reconnect(string deviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Display reconnect is supported only on Windows.");
        }

        var mode = new DevMode
        {
            dmSize = checked((ushort)Marshal.SizeOf<DevMode>())
        };

        if (!EnumDisplaySettings(deviceName, EnumRegistrySettings, ref mode))
        {
            throw new InvalidOperationException($"Cannot read registry display mode for {deviceName}.");
        }

        if (mode.dmPelsWidth == 0 || mode.dmPelsHeight == 0)
        {
            mode.dmPelsWidth = 1920;
            mode.dmPelsHeight = 1080;
            mode.dmDisplayFrequency = 60;
        }

        mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

        var result = ChangeDisplaySettingsEx(
            deviceName,
            ref mode,
            IntPtr.Zero,
            CdsUpdateRegistry,
            IntPtr.Zero);

        if (result != DispChangeSuccessful)
        {
            throw new InvalidOperationException(
                $"Windows rejected ALPHA reconnect on {deviceName} " +
                $"(result {result}; source=ENUM_REGISTRY_SETTINGS; " +
                $"position={mode.dmPositionX},{mode.dmPositionY}; " +
                $"mode={mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency}; " +
                $"dmFields=0x{mode.dmFields:X8}).");
        }

        var displayService = new WindowsDisplayModeService();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            if (displayService.IsAttached(deviceName))
            {
                return;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        if (!displayService.IsAttached(deviceName))
        {
            throw new TimeoutException($"Timed out waiting for {deviceName} to reconnect.");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string deviceName,
        ref DevMode devMode,
        IntPtr hwnd,
        uint flags,
        IntPtr lParam);

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
}
