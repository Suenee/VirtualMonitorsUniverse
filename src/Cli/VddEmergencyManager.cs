using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Provides emergency Virtual Display Driver cleanup without PowerShell.
/// </summary>
/// <remarks>
/// The behavior intentionally follows the validated ALPHA cleanup path:
/// discover Display-class devices by the official friendly name, remove each
/// device node with pnputil /remove-device, then verify that both the device
/// nodes and active VDD display targets are gone.
/// </remarks>
internal static class VddEmergencyManager
{
    private const string VddFriendlyName = "Virtual Display Driver";
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private static readonly Guid DisplayClassGuid = new("4D36E968-E325-11CE-BFC1-08002BE10318");

    public static int Purge()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("VDD PURGE ............... FAIL - Windows is required");
            return 1;
        }

        IReadOnlyList<string> devices;
        try
        {
            devices = EnumerateVddInstanceIds();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VDD PURGE ............... FAIL - device enumeration failed: {ex.Message}");
            return 1;
        }

        var activeBefore = GetActiveVddMonitorCount();
        Console.WriteLine($"VDD PURGE ............... INFO - {devices.Count} VDD device node(s), {activeBefore} active VDD monitor(s)");

        if (devices.Count == 0)
        {
            if (activeBefore == 0)
            {
                Console.WriteLine("VDD PURGE ............... PASS - no VDD device nodes or active VDD monitors remain");
                return 0;
            }

            Console.WriteLine("VDD PURGE ............... FAIL - active VDD monitors remain although no removable VDD device node was found");
            return 1;
        }

        Console.WriteLine($"VDD PURGE ............... RUN - removing {devices.Count} Virtual Display Driver device node(s)");
        Console.WriteLine("                         Windows may show a UAC confirmation");

        foreach (var instanceId in devices)
        {
            var exitCode = RunElevatedPnPUtil("/remove-device", instanceId);
            if (exitCode != 0)
            {
                Console.WriteLine($"VDD PURGE ............... FAIL - pnputil exit code {exitCode} for {instanceId}");
                return 1;
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        do
        {
            var remainingDevices = EnumerateVddInstanceIds();
            var remainingMonitors = GetActiveVddMonitorCount();
            if (remainingDevices.Count == 0 && remainingMonitors == 0)
            {
                Console.WriteLine("VDD PURGE ............... PASS - VDD device nodes removed and no active virtual monitors remain");
                return 0;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        var finalDevices = EnumerateVddInstanceIds();
        var finalMonitors = GetActiveVddMonitorCount();
        Console.WriteLine(
            $"VDD PURGE ............... FAIL - cleanup incomplete: {finalDevices.Count} VDD device node(s), {finalMonitors} active VDD monitor(s) remain");
        return 1;
    }

    private static int GetActiveVddMonitorCount()
    {
        try
        {
            return new WindowsVirtualMonitorService().GetMonitors().Count;
        }
        catch
        {
            // Verification must be conservative. If topology inspection itself
            // fails, do not manufacture a false PASS by pretending the count is 0.
            return int.MaxValue;
        }
    }

    private static int RunElevatedPnPUtil(string operation, string instanceId)
    {
        var pnputil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "pnputil.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = pnputil,
            Arguments = $"{operation} \"{instanceId}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start pnputil.exe.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows UAC confirmation was cancelled.", ex);
        }
    }

    private static IReadOnlyList<string> EnumerateVddInstanceIds()
    {
        var classGuid = DisplayClassGuid;
        var deviceInfoSet = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DigcfPresent);
        if (deviceInfoSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new List<string>();
            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData
                {
                    cbSize = (uint)Marshal.SizeOf<SpDevInfoData>()
                };

                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) // ERROR_NO_MORE_ITEMS
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                var name = ReadRegistryProperty(deviceInfoSet, ref data, SpdrpFriendlyName)
                    ?? ReadRegistryProperty(deviceInfoSet, ref data, SpdrpDeviceDesc);
                if (!string.Equals(name, VddFriendlyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var instanceId = ReadInstanceId(deviceInfoSet, ref data);
                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    result.Add(instanceId);
                }
            }

            return result;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? ReadRegistryProperty(IntPtr set, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(
                set,
                ref data,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out _))
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string? ReadInstanceId(IntPtr set, ref SpDevInfoData data)
    {
        var builder = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(
                set,
                ref data,
                builder,
                builder.Capacity,
                out _)
            ? builder.ToString()
            : null;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        [Out] byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }
}
