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
        Console.WriteLine("  vmu driver purge     Emergency: disable all VDD devices and remove virtual monitors");
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
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("VDD INSTALL ............. FAIL - Windows is required");
            WriteFinalStatus(false);
            return 1;
        }

        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Install-Vdd.ps1");
        if (!File.Exists(scriptPath))
        {
            Console.WriteLine($"VDD INSTALL ............. FAIL - installer script not found: {scriptPath}");
            WriteFinalStatus(false);
            return 1;
        }

        Console.WriteLine("VDD INSTALL ............. RUN - deterministic ALPHA-equivalent dependency setup");
        Console.WriteLine("                         Windows may show a UAC confirmation");

        var setupLogPath = Path.Combine(
            Path.GetTempPath(),
            $"VMU-VDD-install-{Environment.ProcessId}-{Guid.NewGuid():N}.log");

        var escapedScriptPath = EscapePowerShellSingleQuotedString(scriptPath);
        var escapedSetupLogPath = EscapePowerShellSingleQuotedString(setupLogPath);
        var command =
            $"& {{ & '{escapedScriptPath}' *>&1 | Out-File -LiteralPath '{escapedSetupLogPath}' -Encoding utf8; exit $LASTEXITCODE }}";

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = repoRoot
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start elevated VDD installer.");

            RunWithSpinner("VDD INSTALL ............. RUN", () => process.WaitForExit());
            var lines = ReadExternalDiagnostics(setupLogPath);
            foreach (var line in lines)
            {
                Console.WriteLine($"  {line}");
            }

            if (process.ExitCode != 0)
            {
                var detail = GetLastMeaningfulLine(lines);
                Console.WriteLine(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"VDD INSTALL ............. FAIL - exit code {process.ExitCode}"
                        : $"VDD INSTALL ............. FAIL - {detail}");
                WriteFinalStatus(false);
                return 1;
            }

            var diagnostics = new WindowsVirtualMonitorService().GetDriverDiagnostics(TimeSpan.FromSeconds(2));
            if (!diagnostics.DevicePresent || !diagnostics.PipeAvailable)
            {
                Console.WriteLine("VDD INSTALL ............. FAIL - installer completed but ALPHA VDD identity/runtime is not healthy");
                WriteDriverDiagnosticsToConsole(diagnostics);
                WriteFinalStatus(false);
                return 1;
            }

            Console.WriteLine("VDD INSTALL ............. PASS");
            WriteDriverDiagnosticsToConsole(diagnostics);
            WriteFinalStatus(true);
            return 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.WriteLine("VDD INSTALL ............. FAIL - Windows UAC confirmation was cancelled");
            WriteFinalStatus(false);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VDD INSTALL ............. FAIL - {ex.Message}");
            WriteFinalStatus(false);
            return 1;
        }
        finally
        {
            try
            {
                if (File.Exists(setupLogPath))
                {
                    File.Delete(setupLogPath);
                }
            }
            catch (IOException)
            {
                // TEMP cleanup failure must not hide the actual driver result.
            }
            catch (UnauthorizedAccessException)
            {
                // TEMP cleanup failure must not hide the actual driver result.
            }
        }
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
            {
                throw new InvalidOperationException(
                    "VDD runtime pipe is unavailable. Run 'vmu driver status' and, if ROOT\\MTTVDD is missing, 'vmu driver install'.");
            }

            reporter.Write("VDD DRIVER .............. PASS", ConsoleColor.Green);

            var baseline = service.GetMonitors();
            var baselineConnected = baseline.Where(monitor => monitor.IsConnected).ToArray();
            baselineCount = baselineConnected.Length;

            if (baselineCount == 0)
            {
                throw new InvalidOperationException(
                    "VDD pipe is available but no active VDD display exists. The lifecycle test will not create a display because SETDISPLAYCOUNT 0 cannot safely restore this baseline.");
            }

            var requestedCount = checked(baselineCount + 1);
            var baselineIds = baselineConnected
                .Select(monitor => monitor.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            reporter.Write($"VDD BASELINE ............ PASS - {baselineCount} active VMU/VDD display(s)", ConsoleColor.Green);
            reporter.Log($"CREATE VIRTUAL DISPLAY . RUN - requesting display count {requestedCount}");
            RunWithSpinner(
                $"CREATE VIRTUAL DISPLAY . RUN - requesting display count {requestedCount}",
                () => service.SetDisplayCount(requestedCount));
            displayCountChanged = true;

            var detected = RunWithSpinner(
                "WAIT FOR DISPLAY ........ RUN",
                () => service.WaitForConnectedCount(requestedCount, TimeSpan.FromSeconds(12)));
            if (!detected)
            {
                throw new TimeoutException(
                    $"Timed out waiting for VDD active display count to become {requestedCount}.");
            }

            var afterCreate = service.GetMonitors().Where(monitor => monitor.IsConnected).ToArray();
            var created = afterCreate.FirstOrDefault(monitor => !baselineIds.Contains(monitor.Id));
            if (created is null)
            {
                throw new InvalidOperationException(
                    "VDD display count increased, but VMU could not deterministically identify the newly created display path.");
            }

            var windowsNumber = GetWindowsDisplayNumber(created.GdiName);
            var label = windowsNumber is not null
                ? $"Windows monitor {windowsNumber} ({created.GdiName})"
                : created.GdiName ?? created.Id;

            reporter.Write($"CREATE VIRTUAL DISPLAY . PASS - {label}", ConsoleColor.Green);
            reporter.Write(
                $"DISPLAY DETECTED ....... PASS - {created.Width}x{created.Height} at ({created.X},{created.Y})",
                ConsoleColor.Green);
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
                    reporter.Log($"RESTORE DISPLAY COUNT .. RUN - restoring {baselineCount}");
                    RunWithSpinner(
                        $"RESTORE DISPLAY COUNT .. RUN - restoring {baselineCount}",
                        () => service.SetDisplayCount(baselineCount));
                    cleanupPassed = RunWithSpinner(
                        "VERIFY CLEANUP .......... RUN",
                        () => service.WaitForConnectedCount(baselineCount, TimeSpan.FromSeconds(12)));
                    reporter.Write(
                        cleanupPassed
                            ? "CLEANUP VERIFIED ....... PASS"
                            : $"CLEANUP VERIFIED ....... FAIL - active VDD display count did not return to {baselineCount}",
                        cleanupPassed ? ConsoleColor.Green : ConsoleColor.Red);
                }
                catch (Exception ex)
                {
                    reporter.Write($"CLEANUP VERIFIED ....... FAIL - {ex.Message}", ConsoleColor.Red);
                    failure ??= ex;
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

    private static void WriteDriverDiagnostics(SelfTestReporter reporter, VddDriverDiagnostics diagnostics)
    {
        if (!diagnostics.DevicePresent)
        {
            reporter.Write("VDD DEVICE .............. FAIL - ROOT\\MTTVDD adapter not found", ConsoleColor.Red);
        }
        else
        {
            var identity = diagnostics.PnpInstanceId ?? diagnostics.GdiName ?? "unknown identity";
            reporter.Write(
                $"VDD DEVICE .............. {(diagnostics.DeviceActive ? "PASS" : "WARN")} - {identity}; flags=0x{diagnostics.StateFlags:X8}",
                diagnostics.DeviceActive ? ConsoleColor.Green : ConsoleColor.Yellow);
        }

        reporter.Write(
            diagnostics.PipeAvailable
                ? "VDD PIPE ................ PASS - MTTVirtualDisplayPipe available"
                : "VDD PIPE ................ FAIL - MTTVirtualDisplayPipe unavailable",
            diagnostics.PipeAvailable ? ConsoleColor.Green : ConsoleColor.Red);
    }

    private static void WriteDriverDiagnosticsToConsole(VddDriverDiagnostics diagnostics)
    {
        if (!diagnostics.DevicePresent)
        {
            WriteColored("VDD DEVICE .............. FAIL - ROOT\\MTTVDD adapter not found", ConsoleColor.Red);
        }
        else
        {
            var identity = diagnostics.PnpInstanceId ?? diagnostics.GdiName ?? "unknown identity";
            WriteColored(
                $"VDD DEVICE .............. {(diagnostics.DeviceActive ? "PASS" : "WARN")} - {identity}; flags=0x{diagnostics.StateFlags:X8}",
                diagnostics.DeviceActive ? ConsoleColor.Green : ConsoleColor.Yellow);
        }

        WriteColored(
            diagnostics.PipeAvailable
                ? "VDD PIPE ................ PASS - MTTVirtualDisplayPipe available"
                : "VDD PIPE ................ FAIL - MTTVirtualDisplayPipe unavailable",
            diagnostics.PipeAvailable ? ConsoleColor.Green : ConsoleColor.Red);
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

    private static void RunWithSpinner(string text, Action operation) =>
        RunWithSpinner(text, () =>
        {
            operation();
            return true;
        });

    private static void ClearSpinnerLine(int requestedWidth)
    {
        try
        {
            var width = Math.Max(1, Math.Min(Console.BufferWidth - 1, requestedWidth));
            Console.Write($"\r{new string(' ', width)}\r");
        }
        catch (IOException)
        {
            Console.WriteLine();
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine();
        }
    }

    private static string EscapePowerShellSingleQuotedString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string[] ReadExternalDiagnostics(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        try
        {
            return File.ReadAllLines(path)
                .Select(line => line.TrimEnd())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? GetLastMeaningfulLine(IEnumerable<string> lines) =>
        lines.Reverse().Select(line => line.Trim()).FirstOrDefault(line =>
            !string.IsNullOrWhiteSpace(line) &&
            !line.StartsWith("At ", StringComparison.OrdinalIgnoreCase) &&
            !line.StartsWith("+ ", StringComparison.Ordinal) &&
            !line.StartsWith("CategoryInfo", StringComparison.OrdinalIgnoreCase) &&
            !line.StartsWith("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase));

    private static int? GetWindowsDisplayNumber(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        const string marker = "DISPLAY";
        var index = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        return int.TryParse(name[(index + marker.Length)..], out var number) ? number : null;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run 'vmu help' for available commands.");
        return 2;
    }

    private static int UnknownDriverCommand(string command)
    {
        Console.Error.WriteLine($"Unknown driver command: {command}");
        Console.Error.WriteLine("Use 'vmu driver status', 'vmu driver install', or 'vmu driver purge'.");
        return 2;
    }

    private static void WriteFinalStatus(bool ok) =>
        WriteColored(ok ? "STATUS: OK" : "STATUS: FAILED", ok ? ConsoleColor.Green : ConsoleColor.Red);

    private static void WriteColored(string message, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = old;
        }
    }

    private sealed class SelfTestReporter : IDisposable
    {
        private readonly StreamWriter writer;

        public SelfTestReporter(string logPath)
        {
            writer = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        }

        public void Log(string message) =>
            writer.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss.fff}] {message}");

        public void Write(string message, ConsoleColor? color = null)
        {
            Log(message);
            var old = Console.ForegroundColor;
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
                Console.ForegroundColor = old;
            }
        }

        public void Dispose() => writer.Dispose();
    }
}
