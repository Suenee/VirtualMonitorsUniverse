using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

internal static class MonitorCli
{
    public static int Run(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        return command switch
        {
            "list" => List(),
            "connect" => Connect(args.Skip(1).ToArray()),
            "disconnect" => Disconnect(args.Skip(1).ToArray()),
            "mode" => SetMode(args.Skip(1).ToArray()),
            _ => Fail($"Unknown monitor command: {command}")
        };
    }

    private static int List()
    {
        try
        {
            var displays = new WindowsDisplayModeService().GetDisplays();
            Console.WriteLine("WINDOWS MONITORS\n");
            foreach (var display in displays)
            {
                var id = GetDisplayNumber(display.DeviceName)?.ToString() ?? display.DeviceName;
                Console.Write($"{id,-3} ");
                CliConsole.WriteToken(display.IsVirtual ? "VIRTUAL" : "PHYSICAL");
                Console.Write(display.IsVirtual ? "  " : " ");
                CliConsole.WriteToken(display.IsAttached ? "ACTIVE" : "INACTIVE");
                Console.Write(display.IsAttached ? "   " : " ");
                var mode = display.Mode is null
                    ? "mode unavailable"
                    : $"{display.Mode.Width}x{display.Mode.Height}@{display.Mode.RefreshRate} ({display.Mode.X},{display.Mode.Y})";
                Console.WriteLine($"{display.DeviceName,-15} {mode}");
            }
            Console.WriteLine();
            CliConsole.WriteFinalStatus(true);
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static int Connect(string[] args)
    {
        if (args.Length != 1) return Fail("Usage: vmu monitor connect <id>");
        try
        {
            var service = new WindowsDisplayModeService();
            var display = ResolveDisplay(service, args[0]);
            EnsureVirtual(display);
            if (display.IsAttached) throw new InvalidOperationException($"{display.DeviceName} is already connected.");
            var topology = new WindowsDisplayConfigTopologyService();
            if (!topology.HasSavedTopology(display.DeviceName)) throw new InvalidOperationException($"No saved final-ALPHA CCD topology exists for {display.DeviceName}. Activate this virtual monitor once in Windows, then run disconnect before connect.");
            CliConsole.WriteStatusLine("MONITOR CONNECT ........ ", "RUN", $" - {display.DeviceName} [final ALPHA CCD]");
            topology.ReconnectSaved(display.DeviceName);
            var after = ResolveDisplay(service, args[0]);
            var mode = after.Mode is null ? string.Empty : $" {after.Mode.Width}x{after.Mode.Height}@{after.Mode.RefreshRate}";
            CliConsole.WriteStatusLine("MONITOR CONNECT ........ ", "PASS", $" - ACTIVE{mode}");
            CliConsole.WriteFinalStatus(true);
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static int Disconnect(string[] args)
    {
        if (args.Length != 1) return Fail("Usage: vmu monitor disconnect <id>");
        try
        {
            var service = new WindowsDisplayModeService();
            var display = ResolveDisplay(service, args[0]);
            EnsureVirtual(display);
            if (!display.IsAttached) throw new InvalidOperationException($"{display.DeviceName} is already disconnected.");
            CliConsole.WriteStatusLine("MONITOR DISCONNECT ..... ", "RUN", $" - {display.DeviceName} [final ALPHA CCD]");
            new WindowsDisplayConfigTopologyService().DisconnectExact(display.DeviceName);
            var after = ResolveDisplay(service, args[0]);
            CliConsole.WriteStatusLine("MONITOR DISCONNECT ..... ", "PASS", $" - {(after.IsAttached ? "ACTIVE" : "INACTIVE")}");
            CliConsole.WriteFinalStatus(true);
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static int SetMode(string[] args)
    {
        if (args.Length != 4 || !uint.TryParse(args[1], out var width) || !uint.TryParse(args[2], out var height) || !uint.TryParse(args[3], out var refreshRate))
            return Fail("Usage: vmu monitor mode <id> <width> <height> <refresh>");
        try
        {
            var service = new WindowsDisplayModeService();
            var display = ResolveDisplay(service, args[0]);
            EnsureVirtual(display);
            if (!display.IsAttached) throw new InvalidOperationException($"{display.DeviceName} is inactive. Connect it before changing its mode.");
            CliConsole.WriteStatusLine("MONITOR MODE ........... ", "RUN", $" - {display.DeviceName} [final ALPHA reflow-v10]");
            new WindowsAlphaReflowService().SetMode(display.DeviceName, width, height);
            var after = ResolveDisplay(service, args[0]);
            if (after.Mode is null || after.Mode.Width != width || after.Mode.Height != height)
                throw new InvalidOperationException($"Requested {width}x{height}, but Windows reports {after.Mode?.Width ?? 0}x{after.Mode?.Height ?? 0}.");
            if (refreshRate != 0 && Math.Abs((long)after.Mode.RefreshRate - refreshRate) > 1)
                throw new InvalidOperationException($"Requested {refreshRate} Hz, but Windows reports {after.Mode.RefreshRate} Hz.");
            CliConsole.WriteStatusLine("MONITOR MODE ........... ", "PASS", $" - ACTIVE {after.Mode.Width}x{after.Mode.Height}@{after.Mode.RefreshRate} ({after.Mode.X},{after.Mode.Y})");
            CliConsole.WriteFinalStatus(true);
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private static void EnsureVirtual(WindowsDisplayInfo display)
    {
        if (!display.IsVirtual) throw new InvalidOperationException($"Refusing to modify physical display {display.DeviceName}.");
    }

    private static WindowsDisplayInfo ResolveDisplay(WindowsDisplayModeService service, string id)
    {
        var displays = service.GetDisplays();
        WindowsDisplayInfo? display;
        if (int.TryParse(id, out var number)) display = displays.FirstOrDefault(x => string.Equals(x.DeviceName, $"\\\\.\\DISPLAY{number}", StringComparison.OrdinalIgnoreCase));
        else display = displays.FirstOrDefault(x => string.Equals(x.DeviceName, id, StringComparison.OrdinalIgnoreCase));
        return display ?? throw new InvalidOperationException($"Monitor '{id}' was not found.");
    }

    private static int? GetDisplayNumber(string name)
    {
        const string prefix = "\\\\.\\DISPLAY";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(name[prefix.Length..], out var number) ? number : null;
    }

    private static int Fail(string message)
    {
        CliConsole.WriteStatusLine("MONITOR .................. ", "FAIL", $" - {message}");
        CliConsole.WriteFinalStatus(false);
        return 1;
    }
}
