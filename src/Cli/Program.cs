using System.Reflection;
using System.Security.Principal;
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
            "selftest" => RunSelfTest(args.Skip(1).ToArray()),
            "driver" => RunDriverCommand(args.Skip(1).ToArray()),
            "monitor" => MonitorCli.Run(args.Skip(1).ToArray()),
            _ => UnknownCommand(command)
        };
    }

    private static int ShowHelp()
    {
        Console.WriteLine("Virtual Monitors Universe CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  vmu help                         Show this help");
        Console.WriteLine("  vmu version                      Show CLI version");
        Console.WriteLine("  vmu selftest                     Run final ALPHA multi-VDD acceptance diagnostics");
        Console.WriteLine("  vmu driver status                Show read-only VDD dependency diagnostics");
        Console.WriteLine("  vmu driver install               Install the pinned ALPHA-validated VDD dependency");
        Console.WriteLine("  vmu driver purge                 Emergency: remove VDD device nodes and all virtual monitors");
        Console.WriteLine("  vmu monitor list                 List physical and virtual Windows displays");
        Console.WriteLine("  vmu monitor connect <id>         Connect a virtual display to the desktop");
        Console.WriteLine("  vmu monitor disconnect <id>      Disconnect a virtual display from the desktop");
        Console.WriteLine("  vmu monitor mode <id> W H Hz     Change virtual display mode");
        return 0;
    }

    private static int ShowVersion()
    {
        Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown");
        return 0;
    }

    private static int RunSelfTest(string[] args)
    {
        if (args.Any(value => string.Equals(value, "--privileged-worker", StringComparison.OrdinalIgnoreCase)))
            return PrivilegedSelfTestLauncher.RunWorker(args);

        if (!OperatingSystem.IsWindows())
            return AlphaSelfTestRunner.Run();

        if (IsAdministrator())
        {
            CliConsole.WriteStatusLine("SELFTEST PRIVILEGES .... ", "PASS", " - direct Windows path");
            return AlphaSelfTestRunner.Run();
        }

        return PrivilegedSelfTestLauncher.Run();
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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
            CliConsole.WriteFinalStatus(result == 0);
            return result;
        }
        catch (Exception ex)
        {
            CliConsole.WriteStatusLine("VDD PURGE ............... ", "FAIL", $" - {ex.Message}");
            CliConsole.WriteFinalStatus(false);
            return 1;
        }
    }

    private static int RunDriverStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            CliConsole.WriteStatusLine("VDD DEVICE .............. ", "FAIL", " - Windows is required");
            CliConsole.WriteStatusLine("VDD PIPE ................ ", "FAIL", " - Windows is required");
            CliConsole.WriteFinalStatus(false);
            return 1;
        }

        var diagnostics = new WindowsVirtualMonitorService().GetDriverDiagnostics();
        WriteDriverDiagnostics(diagnostics);
        var healthy = diagnostics.DevicePresent && diagnostics.DeviceActive && diagnostics.PipeAvailable;
        CliConsole.WriteFinalStatus(healthy);
        return healthy ? 0 : 1;
    }

    private static int RunDriverInstall()
    {
        CliConsole.WriteStatusLine("VDD INSTALL ............. ", "RUN", " - native C# port of validated ALPHA setup");
        Console.WriteLine("                         Windows may show a UAC confirmation");
        var result = RunWithSpinner("VDD INSTALL ............. RUN", VddInstaller.Install);
        if (result == 0) WriteDriverDiagnostics(new WindowsVirtualMonitorService().GetDriverDiagnostics(TimeSpan.FromSeconds(2)));
        CliConsole.WriteFinalStatus(result == 0);
        return result;
    }

    private static void WriteDriverDiagnostics(VddDriverDiagnostics diagnostics)
    {
        if (diagnostics.DevicePresent)
        {
            CliConsole.WriteStatusLine(
                "VDD DEVICE .............. ",
                diagnostics.DeviceActive ? "PASS" : "WARN",
                $" - {diagnostics.PnpInstanceId ?? diagnostics.GdiName ?? "unknown identity"}; flags=0x{diagnostics.StateFlags:X8}");
        }
        else
        {
            CliConsole.WriteStatusLine("VDD DEVICE .............. ", "FAIL", " - ROOT\\MTTVDD adapter not found");
        }

        CliConsole.WriteStatusLine(
            "VDD PIPE ................ ",
            diagnostics.PipeAvailable ? "PASS" : "FAIL",
            diagnostics.PipeAvailable ? " - MTTVirtualDisplayPipe available" : " - MTTVirtualDisplayPipe unavailable");
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

    private static void ClearSpinnerLine(int requestedWidth)
    {
        try
        {
            var width = Math.Max(1, Math.Min(Console.BufferWidth - 1, requestedWidth));
            Console.Write($"\r{new string(' ', width)}\r");
        }
        catch
        {
            Console.WriteLine();
        }
    }

    private static int UnknownCommand(string command)
    {
        CliConsole.WriteStatusLine("CLI ...................... ", "ERROR", $" - unknown command: {command}");
        return 2;
    }

    private static int UnknownDriverCommand(string command)
    {
        CliConsole.WriteStatusLine("VDD ...................... ", "ERROR", $" - unknown driver command: {command}");
        return 2;
    }
}
