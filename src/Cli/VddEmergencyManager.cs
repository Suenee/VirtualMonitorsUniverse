using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Provides emergency Virtual Display Driver cleanup without PowerShell.
/// </summary>
/// <remarks>
/// The normal path is a direct C# port of the validated ALPHA cleanup contract:
/// discover Display-class devices by FriendlyName, remember their driver INF,
/// remove device nodes, wait for their disappearance, then uninstall the driver
/// packages. The recovery path additionally handles an interrupted/older cleanup
/// where the device node is already gone but the MttVDD driver package remains.
/// </remarks>
internal static class VddEmergencyManager
{
    private const string VddFriendlyName = "Virtual Display Driver";
    private const string VddInfOriginalName = "MttVDD.inf";

    /// <summary>
    /// Returns the real PnP InstanceId values of ALPHA VDD display-class devices.
    /// These identities correspond to Get-PnpDevice.InstanceId in the final ALPHA
    /// acceptance test (for example ROOT\DISPLAY\0000), not the generic hardware ID
    /// Root\MttVDD exposed by EnumDisplayDevices.
    /// </summary>
    public static string[] GetVddInstanceIds() => QueryDisplayDevices()
        .Where(device => string.Equals(device.FriendlyName, VddFriendlyName, StringComparison.OrdinalIgnoreCase))
        .Select(device => device.InstanceId)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static int Purge()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("VDD PURGE ............... FAIL - Windows is required");
            return 1;
        }

        try
        {
            Console.WriteLine("VDD PURGE ............... RUN - ALPHA-equivalent cleanup");
            Console.WriteLine("                         Windows may show a UAC confirmation");

            var snapshot = QueryDisplayDevices();
            var devices = snapshot
                .Where(device => string.Equals(device.FriendlyName, VddFriendlyName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var infNames = devices
                .Select(device => device.DriverInf)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"VDD PURGE ............... INFO - {devices.Length} ALPHA VDD device node(s)");

            foreach (var device in devices)
            {
                Console.WriteLine($"  Removing VDD device node: {device.InstanceId} [{device.Status ?? "unknown"}]");
                RunElevatedPnPUtil($"/remove-device \"{device.InstanceId}\"");
            }

            if (!WaitUntil(
                    () => QueryDisplayDevices().All(device =>
                        !string.Equals(device.FriendlyName, VddFriendlyName, StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine("VDD PURGE ............... FAIL - ALPHA VDD device node(s) did not disappear");
                return 1;
            }

            // Crisis recovery: an earlier incomplete purge may already have removed
            // the device node and therefore lost DEVPKEY_Device_DriverInfPath. In
            // that state recover only packages whose Original Name is exactly the
            // pinned ALPHA VDD INF. This deliberately does not broaden matching to
            // unrelated display drivers.
            foreach (var package in QueryDriverPackages()
                         .Where(package => string.Equals(package.OriginalName, VddInfOriginalName, StringComparison.OrdinalIgnoreCase)))
            {
                if (!infNames.Contains(package.PublishedName, StringComparer.OrdinalIgnoreCase))
                {
                    infNames.Add(package.PublishedName);
                    Console.WriteLine($"  Recovery: orphaned ALPHA VDD package found: {package.PublishedName} ({package.OriginalName})");
                }
            }

            foreach (var inf in infNames)
            {
                Console.WriteLine($"  Removing VDD driver package: {inf}");
                RunElevatedPnPUtil($"/delete-driver \"{inf}\" /uninstall /force");
            }

            var remainingDevices = QueryDisplayDevices()
                .Count(device => string.Equals(device.FriendlyName, VddFriendlyName, StringComparison.OrdinalIgnoreCase));
            var remainingPackages = QueryDriverPackages()
                .Count(package => string.Equals(package.OriginalName, VddInfOriginalName, StringComparison.OrdinalIgnoreCase));

            // Keep topology inspection as an additional diagnostic only. ALPHA's
            // authoritative cleanup identity is the PnP device + its INF package.
            var activeMonitors = TryGetActiveVddMonitorCount();
            Console.WriteLine(
                $"VDD PURGE ............... INFO - remaining: {remainingDevices} device node(s), {remainingPackages} MttVDD package(s), " +
                (activeMonitors is null ? "topology unavailable" : $"{activeMonitors} active VDD monitor(s)"));

            if (remainingDevices != 0 || remainingPackages != 0 || activeMonitors is > 0)
            {
                Console.WriteLine("VDD PURGE ............... FAIL - cleanup verification did not reach a clean baseline");
                return 1;
            }

            Console.WriteLine("VDD PURGE ............... PASS - ALPHA VDD device and driver package are absent");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VDD PURGE ............... FAIL - {ex.Message}");
            return 1;
        }
    }

    private static IReadOnlyList<PnpDisplayDevice> QueryDisplayDevices()
    {
        // /properties is available only on newer PnPUtil implementations. VMU only
        // needs Instance ID + description for normal discovery, so gracefully fall
        // back to the older command form. DriverInf becomes optional in that case;
        // purge can still recover the exact MttVDD package through /enum-drivers.
        string output;
        if (!TryRunPnPUtilCapture("/enum-devices /class Display /properties", out output, out var propertiesError))
        {
            if (!TryRunPnPUtilCapture("/enum-devices /class Display", out output, out var basicError))
            {
                throw new InvalidOperationException(
                    "Could not enumerate Display-class devices with pnputil.exe. " +
                    $"With /properties: {propertiesError} Fallback: {basicError}");
            }
        }

        var blocks = Regex.Split(output, @"(?:\r?\n){2,}");
        var result = new List<PnpDisplayDevice>();

        foreach (var block in blocks)
        {
            var instanceId = ReadPnPField(block, "Instance ID");
            var description = ReadPnPField(block, "Device Description");
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            result.Add(new PnpDisplayDevice(
                instanceId,
                description,
                ReadPnPField(block, "Status"),
                ReadPropertyValue(block, "DEVPKEY_Device_DriverInfPath")));
        }

        return result;
    }

    private static IReadOnlyList<DriverPackage> QueryDriverPackages()
    {
        var output = RunPnPUtilCapture("/enum-drivers");
        var blocks = Regex.Split(output, @"(?:\r?\n){2,}");
        var result = new List<DriverPackage>();

        foreach (var block in blocks)
        {
            var published = ReadPnPField(block, "Published Name");
            var original = ReadPnPField(block, "Original Name");
            if (!string.IsNullOrWhiteSpace(published) && !string.IsNullOrWhiteSpace(original))
            {
                result.Add(new DriverPackage(published, original));
            }
        }

        return result;
    }

    private static string? ReadPnPField(string block, string field)
    {
        var match = Regex.Match(
            block,
            $@"(?im)^\s*{Regex.Escape(field)}\s*:\s*(.+?)\s*$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ReadPropertyValue(string block, string propertyName)
    {
        var match = Regex.Match(
            block,
            $@"(?ims)^\s*{Regex.Escape(propertyName)}[^\r\n]*\r?\n\s*Value\s*:\s*(.+?)\s*$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"') : null;
    }

    private static string RunPnPUtilCapture(string arguments)
    {
        if (TryRunPnPUtilCapture(arguments, out var stdout, out var error))
        {
            return stdout;
        }

        throw new InvalidOperationException(error);
    }

    private static bool TryRunPnPUtilCapture(string arguments, out string stdout, out string error)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetPnPUtilPath(),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start pnputil.exe.");
        stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            error = string.Empty;
            return true;
        }

        var diagnostic = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
        error = $"pnputil.exe {arguments} failed with exit code {process.ExitCode}" +
                (string.IsNullOrWhiteSpace(diagnostic) ? "." : $": {diagnostic}");
        return false;
    }

    private static void RunElevatedPnPUtil(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetPnPUtilPath(),
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start elevated pnputil.exe.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"pnputil.exe {arguments} failed with exit code {process.ExitCode}.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows UAC confirmation was cancelled.", ex);
        }
    }

    private static string GetPnPUtilPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32",
        "pnputil.exe");

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);
        return condition();
    }

    private static int? TryGetActiveVddMonitorCount()
    {
        try
        {
            return new WindowsVirtualMonitorService().GetMonitors().Count;
        }
        catch
        {
            return null;
        }
    }

    private sealed record PnpDisplayDevice(string InstanceId, string FriendlyName, string? Status, string? DriverInf);
    private sealed record DriverPackage(string PublishedName, string OriginalName);
}
