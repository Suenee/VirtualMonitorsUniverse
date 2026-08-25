using System.Reflection;

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
            "selftest" => RunBootstrapSelfTest(),
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
        Console.WriteLine("  vmu selftest   Run automated VMU diagnostics");
        return 0;
    }

    private static int ShowVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Console.WriteLine(version);
        return 0;
    }

    private static int RunBootstrapSelfTest()
    {
        Console.WriteLine("VMU SELFTEST - C#/.NET bootstrap");
        Console.WriteLine();
        Console.WriteLine("RUNTIME ................ PASS");
        Console.WriteLine("CORE LOAD .............. PASS");
        Console.WriteLine("WINDOWS PLATFORM ....... {0}", OperatingSystem.IsWindows() ? "PASS" : "FAIL");
        Console.WriteLine("VDD INTEGRATION ........ NOT IMPLEMENTED");
        Console.WriteLine();
        Console.WriteLine("RESULT: BOOTSTRAP ONLY");
        return OperatingSystem.IsWindows() ? 0 : 1;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run 'vmu help' for available commands.");
        return 2;
    }
}
