using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using VirtualMonitorsUniverse.Core;

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

        // Importing the catalog certificate and creating the root device both require
        // administrative rights. Do not elevate the original executable in place:
        // an elevated Windows token may not see mapped network drives. Instead copy
        // the published CLI runtime to a local TEMP directory and elevate that copy.
        if (!IsAdministrator())
        {
            return RunElevatedFromLocalStage();
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

            ImportCatalogCertificates(catPath);

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
            TryDeleteDirectory(workRoot);
        }
    }

    private static int RunElevatedFromLocalStage()
    {
        var stageRoot = Path.Combine(Path.GetTempPath(), $"VMU-Elevated-{Guid.NewGuid():N}");
        try
        {
            Console.WriteLine("  VDD INSTALL: administrator rights required; preparing local elevation staging...");
            CopyDirectory(AppContext.BaseDirectory, stageRoot);

            var currentProcess = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the current process executable for UAC elevation.");
            var currentProcessName = Path.GetFileName(currentProcess);
            var assemblyName = Path.GetFileName(typeof(VddInstaller).Assembly.Location);
            var stagedAssembly = Path.Combine(stageRoot, assemblyName);

            var info = new ProcessStartInfo
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = stageRoot,
                WindowStyle = ProcessWindowStyle.Normal
            };

            if (string.Equals(currentProcessName, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = currentProcess;
                info.ArgumentList.Add(stagedAssembly);
            }
            else
            {
                var stagedExecutable = Path.Combine(stageRoot, currentProcessName);
                if (!File.Exists(stagedExecutable))
                {
                    throw new FileNotFoundException("The staged VMU CLI executable was not found.", stagedExecutable);
                }

                info.FileName = stagedExecutable;
            }

            info.ArgumentList.Add("driver");
            info.ArgumentList.Add("install");

            Console.WriteLine("  VDD INSTALL: requesting Windows UAC confirmation...");
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Could not start the elevated VMU driver installer.");
            process.WaitForExit();
            Console.WriteLine($"  VDD INSTALL: elevated installer exit code {process.ExitCode}");
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.WriteLine("VDD INSTALL ............. FAIL - Windows UAC confirmation was cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VDD INSTALL ............. FAIL - elevation bootstrap failed: {ex.Message}");
            return 1;
        }
        finally
        {
            TryDeleteDirectory(stageRoot);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // TEMP cleanup must never hide the installation result.
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

    private static void ImportCatalogCertificates(string catalogPath)
    {
        // Windows catalog files are PKCS#7 SignedData. This replaces the obsolete
        // X509Certificate2Collection.Import(byte[]) while preserving the ALPHA
        // behavior of importing every catalog certificate into TrustedPublisher.
        var signedCms = new SignedCms();
        signedCms.Decode(File.ReadAllBytes(catalogPath));

        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in signedCms.Certificates)
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
        // This method is retained as a defense-in-depth fallback. In the normal
        // path the entire installer is already running elevated from local TEMP.
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
