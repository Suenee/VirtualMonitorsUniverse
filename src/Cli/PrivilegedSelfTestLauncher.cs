using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Runs the hardware self-test through a persistent, narrowly scoped scheduled
/// task when the current process does not already have an elevated Windows token.
/// </summary>
/// <remarks>
/// The task is intentionally limited to the VMU self-test worker command. It is
/// not a generic elevated command runner. The task is registered once with the
/// highest run level and can then be started without additional UAC prompts.
/// </remarks>
internal static class PrivilegedSelfTestLauncher
{
    private const int NoError = 0;
    private const string TaskPrefix = "VirtualMonitorsUniverse-SelfTest-";

    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
            return AlphaSelfTestRunner.Run();

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve the VMU executable path.");
        var repoRoot = ResolveRepositoryRoot(executable);
        var taskExecutable = ResolveMappedPath(executable);
        var taskRepoRoot = ResolveMappedPath(repoRoot);
        var taskName = BuildTaskName(taskExecutable, taskRepoRoot);
        var resultFile = Path.Combine(Path.GetTempPath(), $"VMU-selftest-{Environment.UserName}.result");

        if (!TaskExists(taskName))
        {
            CliConsole.WriteStatusLine("SELFTEST PRIVILEGES .... ", "SETUP", " - one-time Windows permission helper");
            Console.WriteLine("                         Windows should ask for approval once");
            RegisterTaskElevated(taskName, taskExecutable, taskRepoRoot, resultFile);
            CliConsole.WriteStatusLine("SELFTEST PRIVILEGES .... ", "PASS", " - persistent helper registered");
        }
        else
        {
            CliConsole.WriteStatusLine("SELFTEST PRIVILEGES .... ", "PASS", " - persistent helper available");
        }

        TryDelete(resultFile);
        CliConsole.WriteStatusLine("SELFTEST ELEVATED ...... ", "RUN");
        RunSchtasks($"/Run /TN \"{taskName}\"");

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
                // The caller has a timeout and will report an explicit helper failure.
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

    private static string ResolveRepositoryRoot(string executable)
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
            return Path.GetFullPath(repoRoot);

        var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VirtualMonitorsUniverse.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not resolve the VMU repository root.");
    }

    private static string BuildTaskName(string executable, string repoRoot)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(executable + "|" + repoRoot));
        return TaskPrefix + Convert.ToHexString(bytes.AsSpan(0, 6));
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

    private static void RegisterTaskElevated(string taskName, string executable, string repoRoot, string resultFile)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Could not resolve the current Windows user SID.");

        var workerArguments = $"selftest --privileged-worker --repo-root {QuoteArgument(repoRoot)} --result-file {QuoteArgument(resultFile)}";
        var script = string.Join("; ",
            "$ErrorActionPreference='Stop'",
            $"$action=New-ScheduledTaskAction -Execute {PsQuote(executable)} -Argument {PsQuote(workerArguments)} -WorkingDirectory {PsQuote(repoRoot)}",
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
