using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Runs the hardware self-test through a persistent, narrowly scoped scheduled
/// task when Windows would otherwise require repeated elevation prompts.
/// </summary>
/// <remarks>
/// The normal CLI is launched through <c>dotnet vmu.dll</c>. The privileged task
/// therefore stages only the published VMU CLI payload in the user's TEMP folder
/// and executes the real installed dotnet host against that staged DLL. This
/// avoids copying an incomplete .NET installation and also avoids relying on a
/// mapped network drive in the elevated Task Scheduler context.
/// </remarks>
internal static class PrivilegedSelfTestLauncher
{
    private const int NoError = 0;
    private const string TaskPrefix = "VirtualMonitorsUniverse-SelfTest-v3-";
    private const string OldTaskPattern = "VirtualMonitorsUniverse-SelfTest-*";

    public static int Run()
    {
        try
        {
            return RunCore();
        }
        catch (Exception ex)
        {
            CliConsole.WriteStatusLine("SELFTEST ELEVATED ...... ", "FAIL", $" - {ex.Message}");
            CliConsole.WriteFinalStatus(false);
            return 1;
        }
    }

    private static int RunCore()
    {
        if (!OperatingSystem.IsWindows())
            return AlphaSelfTestRunner.Run();

        var dotnetExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the active dotnet host path.");
        var repoRoot = ResolveRepositoryRoot();
        var taskRepoRoot = ResolveMappedPath(repoRoot);
        var stagedDll = StageCliRuntimeToTemp(repoRoot);
        var taskName = BuildTaskName(taskRepoRoot);
        var stableId = BuildStableId(taskRepoRoot);
        var resultFile = Path.Combine(Path.GetTempPath(), $"VMU-selftest-{stableId}.result");
        var startedFile = Path.Combine(Path.GetTempPath(), $"VMU-selftest-{stableId}.started");

        if (!TaskExists(taskName))
        {
            CliConsole.WriteStatusLine("SELFTEST PRIVILEGES .... ", "SETUP", " - one-time Windows permission helper");
            Console.WriteLine("                         Windows should ask for approval once");
            RegisterTaskElevated(taskName, dotnetExecutable, stagedDll, taskRepoRoot, resultFile, startedFile);
            CliConsole.WriteStatusLine("SELFTEST PRIVILEGES .... ", "PASS", " - persistent helper registered");
        }
        else
        {
            CliConsole.WriteStatusLine("SELFTEST PRIVILEGES .... ", "PASS", " - persistent helper available");
        }

        TryDelete(resultFile);
        TryDelete(startedFile);
        CliConsole.WriteStatusLine("SELFTEST ELEVATED ...... ", "RUN");
        RunSchtasks($"/Run /TN \"{taskName}\"");

        if (!WaitForFile(startedFile, TimeSpan.FromSeconds(15)))
        {
            var diagnostic = ReadTaskDiagnostic(taskName);
            throw new InvalidOperationException(
                "Privileged self-test helper did not start within 15 seconds." +
                (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Task Scheduler: {diagnostic}"));
        }

        var exitCode = WaitForResult(resultFile, TimeSpan.FromMinutes(10));
        var selfTestLog = Path.Combine(repoRoot, "logs", "vmu-selftest.log");
        if (File.Exists(selfTestLog))
        {
            Console.WriteLine();
            Console.WriteLine(File.ReadAllText(selfTestLog));
        }

        CliConsole.WriteStatusLine("SELFTEST ELEVATED ...... ", exitCode == 0 ? "PASS" : "FAIL", $" - exit code {exitCode}");
        return exitCode;
    }

    public static int RunWorker(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsAdministrator())
            return 5;

        var repoRoot = ReadOption(args, "--repo-root")
            ?? throw new ArgumentException("Missing --repo-root for privileged self-test worker.");
        var resultFile = ReadOption(args, "--result-file")
            ?? throw new ArgumentException("Missing --result-file for privileged self-test worker.");
        var startedFile = ReadOption(args, "--started-file")
            ?? throw new ArgumentException("Missing --started-file for privileged self-test worker.");

        Directory.CreateDirectory(Path.GetDirectoryName(startedFile)!);
        File.WriteAllText(startedFile, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("VMU_REPO_ROOT", repoRoot);

        var exitCode = 1;
        try
        {
            exitCode = AlphaSelfTestRunner.Run();
            return exitCode;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
                File.WriteAllText(resultFile, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                // The caller has a timeout and reports an explicit helper failure.
            }
        }
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string ResolveRepositoryRoot()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new InvalidOperationException("VMU_REPO_ROOT is not set. Start the CLI through vmu.cmd.");
        return Path.GetFullPath(repoRoot);
    }

    private static string StageCliRuntimeToTemp(string repoRoot)
    {
        var sourceDirectory = Path.Combine(repoRoot, ".runtime", "cli");
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Published VMU CLI runtime is missing: {sourceDirectory}");

        var stageDirectory = Path.Combine(Path.GetTempPath(), "VirtualMonitorsUniverse", "privileged-selftest-v3");
        if (Directory.Exists(stageDirectory))
            Directory.Delete(stageDirectory, recursive: true);
        CopyDirectory(sourceDirectory, stageDirectory);

        var stagedDll = Path.Combine(stageDirectory, "vmu.dll");
        if (!File.Exists(stagedDll))
            throw new FileNotFoundException("The staged VMU CLI entry DLL is missing.", stagedDll);
        return stagedDll;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
    }

    private static string BuildTaskName(string repoRoot) => TaskPrefix + BuildStableId(repoRoot);

    private static string BuildStableId(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 6));
    }

    private static bool TaskExists(string taskName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = GetSchtasksPath(),
            Arguments = $"/Query /TN \"{taskName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static void RegisterTaskElevated(
        string taskName,
        string dotnetExecutable,
        string stagedDll,
        string repoRoot,
        string resultFile,
        string startedFile)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Privileged self-test helper registration is supported only on Windows.");

        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Could not resolve the current Windows user SID.");

        var workerArguments =
            $"{QuoteArgument(stagedDll)} selftest --privileged-worker --repo-root {QuoteArgument(repoRoot)} " +
            $"--result-file {QuoteArgument(resultFile)} --started-file {QuoteArgument(startedFile)}";
        var workingDirectory = Path.GetDirectoryName(stagedDll)!;
        var script = string.Join("; ",
            "$ErrorActionPreference='Stop'",
            $"Get-ScheduledTask -TaskName {PsQuote(OldTaskPattern)} -ErrorAction SilentlyContinue | Where-Object {{$_.TaskName -ne {PsQuote(taskName)}}} | Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue",
            $"$action=New-ScheduledTaskAction -Execute {PsQuote(dotnetExecutable)} -Argument {PsQuote(workerArguments)} -WorkingDirectory {PsQuote(workingDirectory)}",
            $"$principal=New-ScheduledTaskPrincipal -UserId {PsQuote(sid)} -LogonType Interactive -RunLevel Highest",
            "$trigger=New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(10)",
            $"Register-ScheduledTask -TaskName {PsQuote(taskName)} -Action $action -Principal $principal -Trigger $trigger -Force | Out-Null");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var info = new ProcessStartInfo
        {
            FileName = GetPowerShellPath(),
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Could not start elevated task registration.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Privileged self-test helper registration failed with exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows UAC confirmation was cancelled.", ex);
        }

        if (!TaskExists(taskName))
            throw new InvalidOperationException("Privileged self-test helper was not found after registration.");
    }

    private static void RunSchtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = GetSchtasksPath(),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            throw new InvalidOperationException($"Could not start privileged self-test helper: {details}");
        }
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
                return true;
            Thread.Sleep(200);
        }
        return File.Exists(path);
    }

    private static int WaitForResult(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (int.TryParse(text, out var result))
                {
                    TryDelete(path);
                    return result;
                }
            }
            Thread.Sleep(200);
        }

        throw new TimeoutException("Privileged self-test helper did not return a result within 10 minutes.");
    }

    private static string ReadTaskDiagnostic(string taskName)
    {
        var script =
            $"$t=Get-ScheduledTask -TaskName {PsQuote(taskName)} -ErrorAction SilentlyContinue; " +
            "if($null -eq $t){'task missing'; exit}; " +
            $"$i=Get-ScheduledTaskInfo -TaskName {PsQuote(taskName)}; " +
            "'state=' + $t.State + ', lastResult=0x' + ([uint32]$i.LastTaskResult).ToString('X8')";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = GetPowerShellPath(),
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null)
            return string.Empty;
        var stdout = process.StandardOutput.ReadToEnd().Trim();
        var stderr = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private static string ResolveMappedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            return fullPath;

        var drive = root[..2];
        var capacity = 1024;
        var remote = new StringBuilder(capacity);
        var result = WNetGetConnection(drive, remote, ref capacity);
        if (result != NoError || remote.Length == 0)
            return fullPath;

        var suffix = fullPath[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return suffix.Length == 0 ? remote.ToString() : Path.Combine(remote.ToString(), suffix);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string GetSchtasksPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "schtasks.exe");
    private static string GetPowerShellPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
