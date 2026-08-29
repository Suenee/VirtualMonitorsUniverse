using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Production wrapper around the VDD node lifecycle validated by the final ALPHA self-test.
/// </summary>
public sealed class WindowsVddNodeService
{
    private const string DriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip";
    private const string DriverSha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a";
    private const string NefConUrl = "https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip";
    private const string NefConSha256 = "a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669";
    private const string VddFriendlyName = "Virtual Display Driver";

    public PreparedPayload PreparePayload()
    {
        EnsureWindows();
        var root = Path.Combine(Path.GetTempPath(), $"VMU-VDD-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var driverZip = Path.Combine(root, "vdd.zip");
            var nefconZip = Path.Combine(root, "nefcon.zip");
            Download(DriverUrl, driverZip);
            AssertHash(driverZip, DriverSha256);
            Download(NefConUrl, nefconZip);
            AssertHash(nefconZip, NefConSha256);

            var driverRoot = Path.Combine(root, "driver");
            var nefconRoot = Path.Combine(root, "nefcon");
            ZipFile.ExtractToDirectory(driverZip, driverRoot, true);
            ZipFile.ExtractToDirectory(nefconZip, nefconRoot, true);

            var inf = Path.Combine(driverRoot, "VirtualDisplayDriver", "MttVDD.inf");
            var cat = Path.Combine(driverRoot, "VirtualDisplayDriver", "mttvdd.cat");
            var nefcon = Path.Combine(nefconRoot, "x64", "nefconw.exe");
            foreach (var file in new[] { inf, cat, nefcon })
                if (!File.Exists(file)) throw new FileNotFoundException("Required validated VDD payload is missing.", file);

            ImportCatalogCertificates(cat);
            return new PreparedPayload(root, inf, nefcon);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public string[] GetInstanceIds()
    {
        EnsureWindows();
        return QueryDisplayDevices()
            .Where(device => string.Equals(device.FriendlyName, VddFriendlyName, StringComparison.OrdinalIgnoreCase))
            .Select(device => device.InstanceId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void InstallOne(PreparedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RunElevated(payload.NefConPath, $"install \"{payload.InfPath}\" Root\\MttVDD", "NefCon");
    }

    public void RemoveOne(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("PnP instance ID is required.", nameof(instanceId));
        RunElevated(GetPnPUtilPath(), $"/remove-device \"{instanceId}\"", "pnputil");
        if (!WaitUntil(() => !GetInstanceIds().Contains(instanceId, StringComparer.OrdinalIgnoreCase), TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException($"VDD node '{instanceId}' did not disappear after uninstall.");
    }

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var delay = interval ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try { if (condition()) return true; } catch (InvalidOperationException) { }
            Thread.Sleep(delay);
        }
        while (DateTime.UtcNow < deadline);

        try { return condition(); } catch { return false; }
    }

    private static IReadOnlyList<PnpDisplayDevice> QueryDisplayDevices()
    {
        var output = RunPnPUtilCapture("/enum-devices /class Display /properties");
        var blocks = Regex.Split(output, @"(?:\r?\n){2,}");
        var result = new List<PnpDisplayDevice>();
        foreach (var block in blocks)
        {
            var instanceId = ReadPnPField(block, "Instance ID");
            var description = ReadPnPField(block, "Device Description");
            if (!string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(description))
                result.Add(new PnpDisplayDevice(instanceId, description));
        }
        return result;
    }

    private static string RunPnPUtilCapture(string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = GetPnPUtilPath(),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start pnputil.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pnputil.exe {arguments} failed with exit code {process.ExitCode}: {stderr.Trim()}");
        return stdout;
    }

    private static string? ReadPnPField(string block, string field)
    {
        var match = Regex.Match(block, $@"(?im)^\s*{Regex.Escape(field)}\s*:\s*(.+?)\s*$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static void Download(string url, string destination)
    {
        using var client = new HttpClient();
        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var input = response.Content.ReadAsStream();
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    private static void AssertHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch for {Path.GetFileName(path)}.");
    }

    private static void ImportCatalogCertificates(string catalogPath)
    {
        var signedCms = new SignedCms();
        signedCms.Decode(File.ReadAllBytes(catalogPath));
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in signedCms.Certificates)
            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
                store.Add(certificate);
    }

    private static void RunElevated(string fileName, string arguments, string label)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"{label} failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows UAC confirmation was cancelled.", ex);
        }
    }

    private static string GetPnPUtilPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "pnputil.exe");
    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VDD node management is supported only on Windows.");
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private sealed record PnpDisplayDevice(string InstanceId, string FriendlyName);

    public sealed class PreparedPayload : IDisposable
    {
        internal PreparedPayload(string root, string infPath, string nefConPath)
        {
            Root = root;
            InfPath = infPath;
            NefConPath = nefConPath;
        }

        internal string Root { get; }
        internal string InfPath { get; }
        internal string NefConPath { get; }
        public void Dispose() => TryDelete(Root);
    }
}
