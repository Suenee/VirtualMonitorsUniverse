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
            "driver" => RunDriverCommand(args.Skip(1).ToArray()),
            _ => UnknownCommand(command)
        };
    }

    private static int ShowHelp()
    {
        Console.WriteLine("Virtual Monitors Universe CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  vmu help             Show this help");
        Console.WriteLine("  vmu version          Show CLI version");
        Console.WriteLine("  vmu selftest         Run automated VMU Core/VDD regression diagnostics");
        Console.WriteLine("  vmu driver status    Show read-only VDD dependency diagnostics");
        Console.WriteLine("  vmu driver install   Install the pinned ALPHA-validated VDD dependency");
        Console.WriteLine("  vmu driver purge     Emergency: remove VDD device nodes and all virtual monitors");
        return 0;
    }

    private static int ShowVersion()
    {
        Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown");
        return 0;
    }

    private static int RunDriverCommand(string[] args)
    {
        var subcommand = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        return subcommand switch
        {
            "status" => RunDriverStatus(),
            "install" => RunDriverInstall(),
            "purge" => RunDriverPurge(),
            _ => UnknownDriverCommand(subcommand)
        };
    }

    private static int RunDriverPurge()
    {
        try
        {
            var result = VddEmergencyManager.Purge();
            WriteFinalStatus(result == 0);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VDD PURGE ............... FAIL - {ex.Message}");
            WriteFinalStatus(false);
            return 1;
        }
    }

    private static int RunDriverStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("VDD DEVICE .............. FAIL - Windows is required");
            Console.WriteLine("VDD PIPE ................ FAIL - Windows is required");
            WriteFinalStatus(false);
            return 1;
        }

        var diagnostics = new WindowsVirtualMonitorService().GetDriverDiagnostics();
        WriteDriverDiagnosticsToConsole(diagnostics);
        var healthy = diagnostics.DevicePresent && diagnostics.DeviceActive && diagnostics.PipeAvailable;
        WriteFinalStatus(healthy);
        return healthy ? 0 : 1;
    }

    private static int RunDriverInstall()
    {
        Console.WriteLine("VDD INSTALL ............. RUN - native C# port of validated ALPHA setup");
        Console.WriteLine("                         Windows may show a UAC confirmation");
        var result = RunWithSpinner("VDD INSTALL ............. RUN", VddInstaller.Install);
        if (result == 0)
        {
            WriteDriverDiagnosticsToConsole(new WindowsVirtualMonitorService().GetDriverDiagnostics(TimeSpan.FromSeconds(2)));
        }
        WriteFinalStatus(result == 0);
        return result;
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
        var displayCountChanged = false;
        var cleanupPassed = false;
        Exception? failure = null;

        try
        {
            var diagnostics = service.GetDriverDiagnostics();
            WriteDriverDiagnostics(reporter, diagnostics);
            if (!diagnostics.PipeAvailable)
                throw new InvalidOperationException("VDD runtime pipe is unavailable. Run 'vmu driver install'.");

            reporter.Write("VDD DRIVER .............. PASS", ConsoleColor.Green);
            var baseline = service.GetMonitors();
            var baselineConnected = baseline.Where(m => m.IsConnected).ToArray();
            baselineCount = baselineConnected.Length;
            if (baselineCount == 0)
                throw new InvalidOperationException("VDD pipe is available but no active VDD display exists. The lifecycle test will not create a display because SETDISPLAYCOUNT 0 cannot safely restore this baseline.");

            var requestedCount = checked(baselineCount + 1);
            var baselineIds = baselineConnected.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            reporter.Write($"VDD BASELINE ............ PASS - {baselineCount} active VMU/VDD display(s)", ConsoleColor.Green);
            RunWithSpinner($"CREATE VIRTUAL DISPLAY . RUN - requesting display count {requestedCount}", () => service.SetDisplayCount(requestedCount));
            displayCountChanged = true;
            if (!RunWithSpinner("WAIT FOR DISPLAY ........ RUN", () => service.WaitForConnectedCount(requestedCount, TimeSpan.FromSeconds(12))))
                throw new TimeoutException($"Timed out waiting for VDD active display count to become {requestedCount}.");

            var created = service.GetMonitors().Where(m => m.IsConnected).FirstOrDefault(m => !baselineIds.Contains(m.Id));
            if (created is null) throw new InvalidOperationException("VDD display count increased, but VMU could not identify the newly created display path.");
            reporter.Write($"CREATE VIRTUAL DISPLAY . PASS - {created.GdiName ?? created.Id}", ConsoleColor.Green);
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
                    RunWithSpinner($"RESTORE DISPLAY COUNT .. RUN - restoring {baselineCount}", () => service.SetDisplayCount(baselineCount));
                    cleanupPassed = RunWithSpinner("VERIFY CLEANUP .......... RUN", () => service.WaitForConnectedCount(baselineCount, TimeSpan.FromSeconds(12)));
                    reporter.Write(cleanupPassed ? "CLEANUP VERIFIED ....... PASS" : $"CLEANUP VERIFIED ....... FAIL - active VDD display count did not return to {baselineCount}", cleanupPassed ? ConsoleColor.Green : ConsoleColor.Red);
                }
                catch (Exception ex)
                {
                    reporter.Write($"CLEANUP VERIFIED ....... FAIL - {ex.Message}", ConsoleColor.Red);
                    failure ??= ex;
                }
            }
            else cleanupPassed = failure is null;
        }

        reporter.Write(string.Empty);
        var passed = failure is null && cleanupPassed;
        reporter.Write(passed ? "RESULT: PASS" : "RESULT: FAIL", passed ? ConsoleColor.Green : ConsoleColor.Red);
        reporter.Write($"Log: {logPath}", ConsoleColor.DarkGray);
        reporter.Write(passed ? "STATUS: OK" : "STATUS: FAILED", passed ? ConsoleColor.Green : ConsoleColor.Red);
        return passed ? 0 : 1;
    }

    private static void WriteDriverDiagnostics(SelfTestReporter reporter, VddDriverDiagnostics diagnostics)
    {
        reporter.Write(diagnostics.DevicePresent
            ? $"VDD DEVICE .............. {(diagnostics.DeviceActive ? "PASS" : "WARN")} - {diagnostics.PnpInstanceId ?? diagnostics.GdiName ?? "unknown identity"}; flags=0x{diagnostics.StateFlags:X8}"
            : "VDD DEVICE .............. FAIL - ROOT\\MTTVDD adapter not found", diagnostics.DevicePresent && diagnostics.DeviceActive ? ConsoleColor.Green : diagnostics.DevicePresent ? ConsoleColor.Yellow : ConsoleColor.Red);
        reporter.Write(diagnostics.PipeAvailable ? "VDD PIPE ................ PASS - MTTVirtualDisplayPipe available" : "VDD PIPE ................ FAIL - MTTVirtualDisplayPipe unavailable", diagnostics.PipeAvailable ? ConsoleColor.Green : ConsoleColor.Red);
    }

    private static void WriteDriverDiagnosticsToConsole(VddDriverDiagnostics diagnostics)
    {
        WriteColored(diagnostics.DevicePresent
            ? $"VDD DEVICE .............. {(diagnostics.DeviceActive ? "PASS" : "WARN")} - {diagnostics.PnpInstanceId ?? diagnostics.GdiName ?? "unknown identity"}; flags=0x{diagnostics.StateFlags:X8}"
            : "VDD DEVICE .............. FAIL - ROOT\\MTTVDD adapter not found", diagnostics.DevicePresent && diagnostics.DeviceActive ? ConsoleColor.Green : diagnostics.DevicePresent ? ConsoleColor.Yellow : ConsoleColor.Red);
        WriteColored(diagnostics.PipeAvailable ? "VDD PIPE ................ PASS - MTTVirtualDisplayPipe available" : "VDD PIPE ................ FAIL - MTTVirtualDisplayPipe unavailable", diagnostics.PipeAvailable ? ConsoleColor.Green : ConsoleColor.Red);
    }

    private static T RunWithSpinner<T>(string text, Func<T> operation)
    {
        var task = Task.Run(operation);
        var frames = new[] { '|', '/', '-', '\\' };
        var index = 0;
        while (!task.IsCompleted)
        {
            Console.Write($"\r{text} {frames[index++ % frames.Length]}");
            Thread.Sleep(100);
        }
        ClearSpinnerLine(text.Length + 4);
        return task.GetAwaiter().GetResult();
    }

    private static void RunWithSpinner(string text, Action operation) => RunWithSpinner(text, () => { operation(); return true; });

    private static void ClearSpinnerLine(int requestedWidth)
    {
        try
        {
            var width = Math.Max(1, Math.Min(Console.BufferWidth - 1, requestedWidth));
            Console.Write($"\r{new string(' ', width)}\r");
        }
        catch { Console.WriteLine(); }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return 2;
    }

    private static int UnknownDriverCommand(string command)
    {
        Console.Error.WriteLine($"Unknown driver command: {command}");
        return 2;
    }

    private static void WriteFinalStatus(bool success) => WriteColored(success ? "STATUS: OK" : "STATUS: FAILED", success ? ConsoleColor.Green : ConsoleColor.Red);

    private static void WriteColored(string text, ConsoleColor color)
    {
        var original = Console.ForegroundColor;
        try { Console.ForegroundColor = color; Console.WriteLine(text); }
        finally { Console.ForegroundColor = original; }
    }

    private sealed class SelfTestReporter : IDisposable
    {
        private readonly StreamWriter writer;
        public SelfTestReporter(string path) => writer = new StreamWriter(path, false) { AutoFlush = true };
        public void Write(string text, ConsoleColor? color = null)
        {
            if (color is null) Console.WriteLine(text); else WriteColored(text, color.Value);
            writer.WriteLine(text);
        }
        public void Dispose() => writer.Dispose();
    }
}
