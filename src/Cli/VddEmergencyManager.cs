using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Provides emergency Virtual Display Driver device control without PowerShell.
/// </summary>
internal static class VddEmergencyManager
{
    private const string VddFriendlyName = "Virtual Display Driver";
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpDeviceDesc = 0x00000000;

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

        if (devices.Count == 0)
        {
            Console.WriteLine("VDD PURGE ............... PASS - no Virtual Display Driver device is present");
            return 0;
        }

        Console.WriteLine($"VDD PURGE ............... RUN - disabling {devices.Count} Virtual Display Driver device(s)");
        Console.WriteLine("                         Windows may show a UAC confirmation");

        foreach (var instanceId in devices)
        {
            var exitCode = RunElevatedPnPUtil("/disable-device", instanceId);
            if (exitCode != 0)
            {
                Console.WriteLine($"VDD PURGE ............... FAIL - pnputil exit code {exitCode} for {instanceId}");
                return 1;
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = EnumerateVddInstanceIds(onlyEnabled: true);
            if (remaining.Count == 0)
            {
                Console.WriteLine("VDD PURGE ............... PASS - all VDD devices are disabled; virtual monitors removed");
                return 0;
            }

            Thread.Sleep(200);
        }

        Console.WriteLine("VDD PURGE ............... FAIL - one or more VDD devices remain enabled after timeout");
        return 1;
    }

    private static int RunElevatedPnPUtil(string operation, string instanceId)
    {
        var pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe");
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

    private static IReadOnlyList<string> EnumerateVddInstanceIds(bool onlyEnabled = false)
    {
        var deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent);
        if (deviceInfoSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new List<string>();
            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
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
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                if (onlyEnabled && !IsDeviceEnabled(data.DevInst))
                {
                    continue;
                }

                result.Add(instanceId);
            }

            return result;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static bool IsDeviceEnabled(uint devInst)
    {
        var result = CM_Get_DevNode_Status(out var status, out var problem, devInst, 0);
        if (result != 0)
        {
            return true;
        }

        const uint DnStarted = 0x00000008;
        const uint CmProbDisabled = 22;
        return (status & DnStarted) != 0 && problem != CmProbDisabled;
    }

    private static string? ReadRegistryProperty(IntPtr set, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out _))
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string? ReadInstanceId(IntPtr set, ref SpDevInfoData data)
    {
        var builder = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref data, builder, builder.Capacity, out _)
            ? builder.ToString()
            : null;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

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

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }
}
