using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Creates the two clean-baseline VDD device nodes required by the final ALPHA
/// multi-VDD acceptance scenario. This fixture is used only by <c>vmu selftest</c>.
/// </summary>
internal static class AlphaSelfTestVdd
{
    private const string DriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip";
    private const string DriverSha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a";
    private const string NefConUrl = "https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip";
    private const string NefConSha256 = "a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669";

    public static PreparedPayload Prepare()
    {
        var root = Path.Combine(Path.GetTempPath(), $"VMU-SELFTEST-{Guid.NewGuid():N}");
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
                if (!File.Exists(file)) throw new FileNotFoundException("Required final-ALPHA VDD payload is missing.", file);
            ImportCatalogCertificates(cat);
            return new PreparedPayload(root, inf, nefcon);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public static void InstallOne(PreparedPayload payload)
    {
        RunElevated(payload.NefConPath, $"install \"{payload.InfPath}\" Root\\MttVDD");
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
        {
            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
                store.Add(certificate);
        }
    }

    private static void RunElevated(string fileName, string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"NefCon failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows UAC confirmation was cancelled.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    internal sealed class PreparedPayload : IDisposable
    {
        public PreparedPayload(string root, string infPath, string nefConPath)
        {
            Root = root;
            InfPath = infPath;
            NefConPath = nefConPath;
        }
        public string Root { get; }
        public string InfPath { get; }
        public string NefConPath { get; }
        public void Dispose() => TryDelete(Root);
    }
}
