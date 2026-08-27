using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Installs the pinned Virtual Display Driver dependency using the validated ALPHA sequence.
/// </summary>
internal static class VddInstaller
{
    private const string DriverVersion = "25.7.23";
    private const string DriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip";
    private const string DriverSha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a";
    private const string NefConVersion = "1.14.0";
    private const string NefConUrl = "https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip";
    private const string NefConSha256 = "a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669";
    private const string PipeName = "MTTVirtualDisplayPipe";

    public static int Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("VDD INSTALL ............. FAIL - Windows is required");
            return 1;
        }

        var diagnostics = new WindowsVirtualMonitorService().GetDriverDiagnostics(TimeSpan.FromMilliseconds(500));
        if (diagnostics.DevicePresent)
        {
            Console.WriteLine($"VDD INSTALL: ALPHA device already present: {diagnostics.PnpInstanceId ?? diagnostics.GdiName ?? "unknown"}");
            if (TestPipe())
            {
                Console.WriteLine("VDD INSTALL: runtime pipe already available.");
                return 0;
            }

            Console.WriteLine("VDD INSTALL ............. FAIL - device exists but MTTVirtualDisplayPipe is unavailable; refusing to mutate an unhealthy state");
            return 1;
        }

        var workRoot = Path.Combine(Path.GetTempPath(), $"VMU-VDD-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workRoot);
            var driverZip = Path.Combine(workRoot, $"vdd-{DriverVersion}.zip");
            var nefconZip = Path.Combine(workRoot, $"nefcon-{NefConVersion}.zip");
            var driverExtract = Path.Combine(workRoot, "driver");
            var nefconExtract = Path.Combine(workRoot, "nefcon");

            Console.WriteLine($"  VDD INSTALL: downloading Virtual Display Driver {DriverVersion}...");
            Download(DriverUrl, driverZip);
            AssertHash(driverZip, DriverSha256);
            Console.WriteLine($"  VDD INSTALL: downloading NefCon {NefConVersion}...");
            Download(NefConUrl, nefconZip);
            AssertHash(nefconZip, NefConSha256);
            ZipFile.ExtractToDirectory(driverZip, driverExtract, true);
            ZipFile.ExtractToDirectory(nefconZip, nefconExtract, true);

            var driverSource = Path.Combine(driverExtract, "VirtualDisplayDriver");
            var infPath = Path.Combine(driverSource, "MttVDD.inf");
            var catPath = Path.Combine(driverSource, "mttvdd.cat");
            var nefconExe = Path.Combine(nefconExtract, "x64", "nefconw.exe");
            foreach (var required in new[] { infPath, catPath, nefconExe })
            {
                if (!File.Exists(required))
                {
                    throw new FileNotFoundException("Required VDD installation file not found.", required);
                }
            }

            ImportCatalogCertificates(catPath, workRoot);

            Console.WriteLine("  VDD INSTALL: creating exactly one root-enumerated Root\\MttVDD device...");
            RunElevated(nefconExe, $"install \"{infPath}\" Root\\MttVDD");

            if (!WaitUntil(() => new WindowsVirtualMonitorService().GetDriverDiagnostics(TimeSpan.FromMilliseconds(250)).DevicePresent, TimeSpan.FromSeconds(20)))
            {
                throw new InvalidOperationException("Expected one Virtual Display Driver device after NefCon exit code 0.");
            }

            if (!WaitUntil(TestPipe, TimeSpan.FromSeconds(20)))
            {
                throw new InvalidOperationException("Virtual Display Driver was detected, but MTTVirtualDisplayPipe did not become available.");
            }

            Console.WriteLine("VDD INSTALL ............. PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VDD INSTALL ............. FAIL - {ex.Message}");
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true); }
            catch { /* TEMP cleanup must not hide the installation result. */ }
        }
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
        {
            throw new InvalidDataException($"SHA-256 mismatch for {path}.");
        }
    }

    private static void ImportCatalogCertificates(string catalogPath, string workRoot)
    {
        var certificates = new X509Certificate2Collection();
        certificates.Import(File.ReadAllBytes(catalogPath));
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in certificates)
        {
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
            if (existing.Count == 0)
            {
                store.Add(certificate);
                Console.WriteLine($"  VDD INSTALL: trusted publisher certificate added: {certificate.Thumbprint}");
            }
        }
    }

    private static bool TestPipe()
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
        try { pipe.Connect(500); return pipe.IsConnected; }
        catch { return false; }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (condition()) return true;
            Thread.Sleep(500);
        } while (DateTime.UtcNow < deadline);
        return condition();
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
            Console.WriteLine($"  VDD INSTALL: EXIT CODE {process.ExitCode}");
            if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows UAC confirmation was cancelled.", ex);
        }
    }
}
