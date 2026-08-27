namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Centralized terminal presentation for the VMU CLI.
/// ANSI-free ConsoleColor output keeps redirected text and logs free of control codes.
/// </summary>
internal static class CliConsole
{
    public static void WriteLine(string text = "") => Console.WriteLine(text);

    public static void WriteLine(string text, ConsoleColor color)
    {
        var original = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }

    public static void WriteStatusLine(string prefix, string status, string? suffix = null)
    {
        Console.Write(prefix);
        WriteToken(status);
        if (!string.IsNullOrEmpty(suffix)) Console.Write(suffix);
        Console.WriteLine();
    }

    public static void WriteToken(string token)
    {
        var color = token.Trim().ToUpperInvariant() switch
        {
            "OK" or "PASS" or "ACTIVE" => ConsoleColor.Green,
            "WARNING" or "WARN" => ConsoleColor.Yellow,
            "FAILED" or "FAIL" or "ERROR" => ConsoleColor.Red,
            "RUN" => ConsoleColor.Cyan,
            "INACTIVE" => ConsoleColor.DarkGray,
            "VIRTUAL" => ConsoleColor.Magenta,
            "PHYSICAL" => ConsoleColor.Green,
            _ => Console.ForegroundColor
        };

        var original = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.Write(token);
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }

    public static void WriteFinalStatus(bool success)
    {
        Console.Write("STATUS: ");
        WriteToken(success ? "OK" : "FAILED");
        Console.WriteLine();
    }
}
