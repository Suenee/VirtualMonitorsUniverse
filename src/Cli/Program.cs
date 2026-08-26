using System.Diagnostics;
using System.Reflection;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";

        return command switch
        {
            "help" or "--help" or "-h" => ShowHelp(),
            "version" or "--version" => ShowVersion(),
            "selftest" => RunCoreSelfTest(),
            _ => UnknownCommand(command)
        };
    }

    private static int ShowHelp()
    {
        Console.WriteLine("Virtual Monitors Universe CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  vmu help       Show this help");
        Console.WriteLine("  vmu version    Show CLI version");
        Console.WriteLine("  vmu selftest   Run automated VMU Core/VDD regression diagnostics");
        return 0;
    }

    private static int ShowVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Console.WriteLine(version);
        return 0;
    }

    private static int RunCoreSelfTest()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        var logsDir = Path.Combine(repoRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var logPath = Path.Combine(logsDir, "vmu-selftest.log");

        using var reporter = new SelfTestReporter(logPath);
        reporter.Write("VMU SELFTEST - C#/.NET Core + VDD lifecycle", ConsoleColor.Cyan);
        reporter.Write(string.Empty);

        if (!OperatingSystem.IsWindows())
        {
            reporter.Write("RUNTIME ................ PASS", ConsoleColor.Green);
            reporter.Write("CORE LOAD .............. PASS", ConsoleColor.Green);
            reporter.Write("WINDOWS PLATFORM ....... FAIL", ConsoleColor.Red);
            reporter.Write(string.Empty);
            reporter.Write($"Log: {logPath}", ConsoleColor.DarkGray);
            reporter.Write("STATUS: FAILED", ConsoleColor.Red);
            return 1;
        }

        reporter.Write("RUNTIME ................ PASS", ConsoleColor.Green);
        reporter.Write("CORE LOAD .............. PASS", ConsoleColor.Green);
        reporter.Write("WINDOWS PLATFORM ....... PASS", ConsoleColor.Green);

        var service = new WindowsVirtualMonitorService();
        var baselineCount = 0;
        var requestedCount = 0;
        var displayCountChanged = false;
        var cleanupPassed = false;
        Exception? failure = null;

        try
        {
            if (!service.IsDriverAvailable())
            {
                reporter.Write("VDD DRIVER .............. RUN - dependency is unavailable; starting deterministic setup", ConsoleColor.Cyan);
                EnsureVddDependency(repoRoot, reporter);
            }

            if (!service.IsDriverAvailable(TimeSpan.FromSeconds(2)))
            {
                throw new InvalidOperationException(
                    "The MttVDD named pipe is still unavailable after dependency setup.");
            }

            reporter.Write("VDD DRIVER .............. PASS", ConsoleColor.Green);

            var baseline = service.GetMonitors();
            var baselineConnected = baseline.Where(monitor => monitor.IsConnected).ToArray();
            baselineCount = baselineConnected.Length;
            requestedCount = checked(baselineCount + 1);
            var baselineIds = baselineConnected.Select(monitor => monitor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            reporter.Write($"VDD BASELINE ............ PASS - {baselineCount} active VMU/VDD display(s)", ConsoleColor.Green);
            foreach (var monitor in baselineConnected)
            {
                reporter.Write(
                    $"VDD FOUND ................ {monitor.GdiName ?? monitor.Id} {monitor.Width}x{monitor.Height} at ({monitor.X},{monitor.Y})",
                    ConsoleColor.DarkGray);
            }

            reporter.Write($"CREATE VIRTUAL DISPLAY . RUN - requesting display count {requestedCount}", ConsoleColor.Cyan);

            service.SetDisplayCount(requestedCount);
            displayCountChanged = true;

            if (!service.WaitForConnectedCount(requestedCount, TimeSpan.FromSeconds(12)))
            {
                throw new TimeoutException(
                    $"Timed out waiting for VDD active display count to become {requestedCount}.");
            }

            var afterCreate = service.GetMonitors().Where(monitor => monitor.IsConnected).ToArray();
            var created = afterCreate.FirstOrDefault(monitor => !baselineIds.Contains(monitor.Id));
            if (created is null)
            {
                throw new InvalidOperationException(
                    "VDD display count increased, but VMU could not deterministically identify the newly created CCD display path.");
            }

            var windowsNumber = GetWindowsDisplayNumber(created.GdiName);
            var monitorLabel = windowsNumber is not null
                ? $"Windows monitor {windowsNumber} ({created.GdiName})"
                : created.GdiName ?? created.Id;

            reporter.Write($"CREATE VIRTUAL DISPLAY . PASS - {monitorLabel}", ConsoleColor.Green);
            reporter.Write($"DISPLAY DETECTED ....... PASS - {created.Width}x{created.Height} at ({created.X},{created.Y})", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            failure = ex;
            reporter.Write($"VDD LIFECYCLE ........... FAIL - {ex.Message}", ConsoleColor.Red);
        }
        finally
        {
            if (displayCountChanged)
            {
                try
                {
                    reporter.Write($"RESTORE DISPLAY COUNT .. RUN - restoring {baselineCount}", ConsoleColor.Cyan);
                    service.SetDisplayCount(baselineCount);
                    cleanupPassed = service.WaitForConnectedCount(baselineCount, TimeSpan.FromSeconds(12));
                    reporter.Write(
                        cleanupPassed
                            ? "CLEANUP VERIFIED ....... PASS"
                            : $"CLEANUP VERIFIED ....... FAIL - active VDD display count did not return to {baselineCount}",
                        cleanupPassed ? ConsoleColor.Green : ConsoleColor.Red);
                }
                catch (Exception cleanupException)
                {
                    reporter.Write($"CLEANUP VERIFIED ....... FAIL - {cleanupException.Message}", ConsoleColor.Red);
                    failure ??= cleanupException;
                }
            }
            else
            {
                cleanupPassed = failure is null;
            }
        }

        reporter.Write(string.Empty);
        var passed = failure is null && cleanupPassed;
        reporter.Write(passed ? "RESULT: PASS" : "RESULT: FAIL", passed ? ConsoleColor.Green : ConsoleColor.Red);
        reporter.Write($"Log: {logPath}", ConsoleColor.DarkGray);
        reporter.Write(passed ? "STATUS: OK" : "STATUS: FAILED", passed ? ConsoleColor.Green : ConsoleColor.Red);
        return passed ? 0 : 1;
    }

    private static void EnsureVddDependency(string repoRoot, SelfTestReporter reporter)
    {
        var scriptPath = Path.Combine(repoRoot, "scripts", "Ensure-Vdd.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("VDD dependency setup script is missing.", scriptPath);
        }

        reporter.Write("VDD SETUP ............... RUN - Windows may show a UAC confirmation", ConsoleColor.Yellow);

        // Administrative work must run in an elevated process because Windows
        // cannot elevate the already-running console process in place. Keep the
        // helper window hidden so selftest remains a single visible console flow;
        // only the standard Windows UAC prompt may become visible.
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = repoRoot,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start elevated VDD dependency setup.");
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"VDD dependency setup failed with exit code {process.ExitCode}.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("VDD dependency setup was cancelled at the Windows UAC prompt.", ex);
        }

        reporter.Write("VDD SETUP ............... PASS", ConsoleColor.Green);
    }

    private static int? GetWindowsDisplayNumber(string? gdiName)
    {
        if (string.IsNullOrWhiteSpace(gdiName))
        {
            return null;
        }

        const string marker = "DISPLAY";
        var markerIndex = gdiName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var numberText = gdiName[(markerIndex + marker.Length)..];
        return int.TryParse(numberText, out var number) ? number : null;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run 'vmu help' for available commands.");
        return 2;
    }

    private sealed class SelfTestReporter : IDisposable
    {
        private readonly StreamWriter writer;

        public SelfTestReporter(string logPath)
        {
            writer = new StreamWriter(logPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }

        public void Write(string message, ConsoleColor? color = null)
        {
            writer.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss.fff}] {message}");

            var originalColor = Console.ForegroundColor;
            try
            {
                if (color.HasValue)
                {
                    Console.ForegroundColor = color.Value;
                }

                Console.WriteLine(message);
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }

        public void Dispose() => writer.Dispose();
    }
}
