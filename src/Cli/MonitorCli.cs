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
            Console.WriteLine("WINDOWS MONITORS");
            Console.WriteLine();
            foreach (var display in displays)
            {
                var number = GetDisplayNumber(display.DeviceName);
                var id = number?.ToString() ?? display.DeviceName;
                var type = display.IsVirtual ? "VIRTUAL " : "PHYSICAL";
                var state = display.IsAttached ? "ACTIVE  " : "INACTIVE";
                var mode = display.Mode is null
                    ? "mode unavailable"
                    : $"{display.Mode.Width}x{display.Mode.Height}@{display.Mode.RefreshRate} ({display.Mode.X},{display.Mode.Y})";
                Console.WriteLine($"{id,-3} {type} {state} {display.DeviceName,-15} {mode}");
            }
            Console.WriteLine();
            Console.WriteLine("STATUS: OK");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Connect(string[] args)
    {
        if (args.Length != 1) return Fail("Usage: vmu monitor connect <id>");

        return RunOnVirtualDisplay(args[0], "CONNECT", service =>
        {
            var display = ResolveDisplay(service, args[0]);
            if (display.IsAttached)
                throw new InvalidOperationException($"{display.DeviceName} is already connected.");

            // Use the original ALPHA acceptance-test reconnect literally:
            // ENUM_REGISTRY_SETTINGS -> 1920x1080@60 fallback for 0x0 ->
            // DM_POSITION|WIDTH|HEIGHT|FREQUENCY -> CDS_UPDATEREGISTRY.
            new WindowsAlphaReconnectService().Reconnect(display.DeviceName);
        });
    }

    private static int Disconnect(string[] args)
    {
        if (args.Length != 1) return Fail("Usage: vmu monitor disconnect <id>");
        return RunOnVirtualDisplay(args[0], "DISCONNECT", service => service.Disconnect(ResolveDisplay(service, args[0]).DeviceName));
    }

    private static int SetMode(string[] args)
    {
        if (args.Length != 4 ||
            !uint.TryParse(args[1], out var width) ||
            !uint.TryParse(args[2], out var height) ||
            !uint.TryParse(args[3], out var refreshRate))
        {
            return Fail("Usage: vmu monitor mode <id> <width> <height> <refresh>");
        }

        return RunOnVirtualDisplay(args[0], "MODE", service =>
        {
            var display = ResolveDisplay(service, args[0]);
            if (!display.IsAttached)
                throw new InvalidOperationException($"{display.DeviceName} is inactive. Connect it before changing its mode.");
            service.SetMode(display.DeviceName, width, height, refreshRate);
        });
    }

    private static int RunOnVirtualDisplay(string id, string operation, Action<WindowsDisplayModeService> action)
    {
        try
        {
            var service = new WindowsDisplayModeService();
            var display = ResolveDisplay(service, id);
            if (!display.IsVirtual)
                throw new InvalidOperationException($"Refusing to modify physical display {display.DeviceName}.");

            Console.WriteLine($"MONITOR {operation} ........ RUN - {display.DeviceName}");
            action(service);
            var after = ResolveDisplay(service, id);
            var state = after.IsAttached ? "ACTIVE" : "INACTIVE";
            var mode = after.Mode is null ? string.Empty : $" {after.Mode.Width}x{after.Mode.Height}@{after.Mode.RefreshRate}";
            Console.WriteLine($"MONITOR {operation} ........ PASS - {state}{mode}");
            Console.WriteLine("STATUS: OK");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static WindowsDisplayInfo ResolveDisplay(WindowsDisplayModeService service, string id)
    {
        var displays = service.GetDisplays();
        WindowsDisplayInfo? display;
        if (int.TryParse(id, out var number))
        {
            var expected = $"\\\\.\\DISPLAY{number}";
            display = displays.FirstOrDefault(item => string.Equals(item.DeviceName, expected, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            display = displays.FirstOrDefault(item => string.Equals(item.DeviceName, id, StringComparison.OrdinalIgnoreCase));
        }

        return display ?? throw new InvalidOperationException($"Monitor '{id}' was not found.");
    }

    private static int? GetDisplayNumber(string deviceName)
    {
        const string prefix = "\\\\.\\DISPLAY";
        return deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(deviceName[prefix.Length..], out var number)
            ? number
            : null;
    }

    private static int Fail(string message)
    {
        Console.WriteLine($"MONITOR .................. FAIL - {message}");
        Console.WriteLine("STATUS: FAILED");
        return 1;
    }
}
