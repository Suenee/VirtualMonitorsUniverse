using System.Diagnostics;
using System.Text.Json;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Whitelists and executes the VMU operations that are intentionally exposed to
/// remote VPP clients. The Socket Server never calls internal VMU functions by
/// arbitrary name; every remotely callable method must be registered here.
/// </summary>
internal sealed class RemoteActionRegistry
{
    private readonly MonitorApplicationService _monitors;
    private readonly Func<int> _webPortProvider;
    private readonly WindowsDisplayArrangementService _arrangement = new();

    public RemoteActionRegistry(MonitorApplicationService monitors, Func<int> webPortProvider)
    {
        _monitors = monitors;
        _webPortProvider = webPortProvider;
    }

    public static IReadOnlyList<string> Actions { get; } =
    [
        "monitor_connect",
        "monitor_disconnect",
        "monitor_toggle",
        "monitor_set_resolution",
        "monitor_set_refresh_rate",
        "monitor_set_orientation",
        "monitor_set_position",
        "monitor_mouse_enable",
        "monitor_clipboard_enable",
        "monitor_keyboard_enable",
        "monitor_open_terminal",
        "monitor_open_properties",
        "monitor_open_remote_access",
        "monitor_set_title",
        "all_monitors_connect",
        "all_monitors_disconnect",
        "get_state"
    ];

    public object Execute(string action, JsonElement args)
    {
        if (!Actions.Contains(action, StringComparer.Ordinal))
            throw new RemoteActionException("UNKNOWN_METHOD", $"Remote action '{action}' is not published by VMU.");

        return action switch
        {
            "monitor_connect" => StateFor(_monitors.Connect(RequireMonitor(args))),
            "monitor_disconnect" => StateFor(_monitors.Disconnect(RequireMonitor(args))),
            "monitor_toggle" => Toggle(RequireMonitor(args)),
            "monitor_set_resolution" => SetResolution(args),
            "monitor_set_refresh_rate" => SetRefreshRate(args),
            "monitor_set_orientation" => SetOrientation(args),
            "monitor_set_position" => SetPosition(args),
            "monitor_mouse_enable" => SetCapability(args, Capability.Mouse),
            "monitor_clipboard_enable" => SetCapability(args, Capability.Clipboard),
            "monitor_keyboard_enable" => SetCapability(args, Capability.Keyboard),
            "monitor_open_terminal" => OpenMonitorPage(args, terminal: true, remoteAccess: false),
            "monitor_open_properties" => OpenMonitorPage(args, terminal: false, remoteAccess: false),
            "monitor_open_remote_access" => OpenMonitorPage(args, terminal: false, remoteAccess: true),
            "monitor_set_title" => SetTitle(args),
            "all_monitors_connect" => SetAllMonitors(true),
            "all_monitors_disconnect" => SetAllMonitors(false),
            "get_state" => StateSnapshot(),
            _ => throw new RemoteActionException("UNKNOWN_METHOD", $"Remote action '{action}' is not published by VMU.")
        };
    }

    public object StateSnapshot()
    {
        var monitors = _monitors.List();
        return new Dictionary<string, object?>
        {
            ["vmu_version"] = ProjectInfo.Version,
            ["installed_monitor_count"] = monitors.Count(x => x.Installed),
            ["connected_monitor_count"] = monitors.Count(x => x.Connected),
            ["monitors"] = monitors.Select(StateFor).ToArray()
        };
    }

    private object Toggle(string id)
    {
        var current = RequireSnapshot(id);
        return StateFor(current.Connected ? _monitors.Disconnect(id) : _monitors.Connect(id));
    }

    private object SetResolution(JsonElement args)
    {
        var current = RequireSnapshot(RequireMonitor(args));
        var width = RequireInt(args, "width", 320, 16384);
        var height = RequireInt(args, "height", 200, 16384);
        return StateFor(Update(current, width: width, height: height));
    }

    private object SetRefreshRate(JsonElement args)
    {
        var current = RequireSnapshot(RequireMonitor(args));
        var hz = RequireInt(args, "hz", 1, 1000);
        if (!MonitorApplicationService.SupportedRefreshRates.Contains(hz))
            throw new RemoteActionException("INVALID_ARGUMENT", $"Unsupported refresh rate {hz} Hz.");
        return StateFor(Update(current, refreshRate: hz));
    }

    private object SetOrientation(JsonElement args)
    {
        var current = RequireSnapshot(RequireMonitor(args));
        if (!args.TryGetProperty("orientation", out var value) || value.ValueKind != JsonValueKind.String)
            throw new RemoteActionException("INVALID_ARGUMENT", "orientation must be 'landscape' or 'portrait'.");
        var orientation = value.GetString();
        var portrait = orientation?.Equals("portrait", StringComparison.OrdinalIgnoreCase) == true;
        if (!portrait && orientation?.Equals("landscape", StringComparison.OrdinalIgnoreCase) != true)
            throw new RemoteActionException("INVALID_ARGUMENT", "orientation must be 'landscape' or 'portrait'.");
        return StateFor(Update(current, portrait: portrait));
    }

    private object SetPosition(JsonElement args)
    {
        var current = RequireSnapshot(RequireMonitor(args));
        if (!current.Connected || string.IsNullOrWhiteSpace(current.DeviceName))
            throw new RemoteActionException("INVALID_ARGUMENT", "The monitor must be connected before its Windows position can be changed.");
        var x = RequireInt(args, "x", -65535, 65535);
        var y = RequireInt(args, "y", -65535, 65535);

        var active = _arrangement.CaptureActive().ToArray();
        if (!active.Any(entry => entry.DeviceName.Equals(current.DeviceName, StringComparison.OrdinalIgnoreCase)))
            throw new RemoteActionException("COMMAND_FAILED", "The monitor is not part of the active Windows desktop.");
        var replacement = active.Select(entry => entry.DeviceName.Equals(current.DeviceName, StringComparison.OrdinalIgnoreCase)
            ? new DisplayArrangementEntry(entry.DeviceName, x, y)
            : entry).ToArray();
        try { _arrangement.Apply(replacement); }
        catch (Exception ex) { throw new RemoteActionException("COMMAND_FAILED", ex.Message, ex); }
        return StateFor(RequireSnapshot(current.Configuration.VmuId));
    }

    private object SetCapability(JsonElement args, Capability capability)
    {
        var current = RequireSnapshot(RequireMonitor(args));
        var enabled = RequireBool(args, "enabled");
        var c = current.Configuration;
        return StateFor(_monitors.UpdateProperties(
            c.VmuId, c.Name, c.Title, c.Width, c.Height, c.RefreshRate, c.Portrait,
            c.RemoteAccess, c.SecurityMode, null, false,
            capability == Capability.Clipboard ? enabled : c.CollaborationClipboard,
            capability == Capability.Mouse ? enabled : c.CollaborationMouse,
            capability == Capability.Keyboard ? enabled : c.CollaborationKeyboard));
    }

    private object SetTitle(JsonElement args)
    {
        var current = RequireSnapshot(RequireMonitor(args));
        if (!args.TryGetProperty("title", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new RemoteActionException("INVALID_ARGUMENT", "title must be a non-empty string.");
        return StateFor(Update(current, title: value.GetString()!.Trim()));
    }

    private object SetAllMonitors(bool connected)
    {
        var changed = new List<object>();
        foreach (var monitor in _monitors.List())
        {
            try
            {
                if (connected && !monitor.Connected && monitor.Installed) changed.Add(StateFor(_monitors.Connect(monitor.Configuration.VmuId)));
                else if (!connected && monitor.Connected) changed.Add(StateFor(_monitors.Disconnect(monitor.Configuration.VmuId)));
            }
            catch (Exception ex)
            {
                throw new RemoteActionException("COMMAND_FAILED", $"Could not {(connected ? "connect" : "disconnect")} '{monitor.Configuration.Title}': {ex.Message}", ex);
            }
        }
        return new Dictionary<string, object?> { ["success"] = true, ["changed"] = changed, ["state"] = StateSnapshot() };
    }

    private object OpenMonitorPage(JsonElement args, bool terminal, bool remoteAccess)
    {
        var monitor = RequireSnapshot(RequireMonitor(args));
        var path = terminal
            ? $"/monitor/{Uri.EscapeDataString(monitor.Configuration.Name)}"
            : $"/monitors/{Uri.EscapeDataString(monitor.Configuration.Name)}{(remoteAccess ? "#remote-access" : string.Empty)}";
        var url = $"http://127.0.0.1:{_webPortProvider()}{path}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { throw new RemoteActionException("COMMAND_FAILED", ex.Message, ex); }
        return new Dictionary<string, object?> { ["success"] = true, ["url"] = url };
    }

    private MonitorSnapshot Update(MonitorSnapshot current, int? width = null, int? height = null, int? refreshRate = null, bool? portrait = null, string? title = null)
    {
        var c = current.Configuration;
        return _monitors.UpdateProperties(
            c.VmuId, c.Name, title ?? c.Title, width ?? c.Width, height ?? c.Height, refreshRate ?? c.RefreshRate, portrait ?? c.Portrait,
            c.RemoteAccess, c.SecurityMode, null, false,
            c.CollaborationClipboard, c.CollaborationMouse, c.CollaborationKeyboard);
    }

    private MonitorSnapshot RequireSnapshot(string id)
        => _monitors.Get(id) ?? throw new RemoteActionException("INVALID_ARGUMENT", $"Monitor '{id}' was not found.");

    private static string RequireMonitor(JsonElement args)
    {
        if (!args.TryGetProperty("monitor", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new RemoteActionException("INVALID_ARGUMENT", "monitor must be a non-empty VMU name or ID.");
        return value.GetString()!.Trim();
    }

    private static int RequireInt(JsonElement args, string name, int min, int max)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < min || result > max)
            throw new RemoteActionException("INVALID_ARGUMENT", $"{name} must be an integer from {min} through {max}.");
        return result;
    }

    private static bool RequireBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RemoteActionException("INVALID_ARGUMENT", $"{name} must be a boolean.");
        return value.GetBoolean();
    }

    private static object StateFor(MonitorSnapshot monitor) => new Dictionary<string, object?>
    {
        ["monitor_exists"] = true,
        ["monitor_installed"] = monitor.Installed,
        ["monitor_connected"] = monitor.Connected,
        ["monitor_healthy"] = !monitor.Health.IsError,
        ["monitor_health_state"] = monitor.Health.State,
        ["monitor_health_message"] = monitor.Health.Message,
        ["monitor_windows_display"] = monitor.WindowsDisplay,
        ["monitor_width"] = monitor.Width,
        ["monitor_height"] = monitor.Height,
        ["monitor_resolution"] = $"{monitor.Width}x{monitor.Height}",
        ["monitor_refresh_rate"] = monitor.RefreshRate,
        ["monitor_orientation"] = monitor.Configuration.Portrait ? "portrait" : "landscape",
        ["monitor_x"] = monitor.PositionX,
        ["monitor_y"] = monitor.PositionY,
        ["monitor_mouse_enabled"] = monitor.Configuration.CollaborationMouse,
        ["monitor_clipboard_enabled"] = monitor.Configuration.CollaborationClipboard,
        ["monitor_keyboard_enabled"] = monitor.Configuration.CollaborationKeyboard,
        ["monitor_audio_supported"] = false,
        ["monitor_audio_enabled"] = false,
        ["monitor_remote_access"] = monitor.Configuration.RemoteAccess.ToString(),
        ["monitor_title"] = monitor.Configuration.Title,
        ["monitor_name"] = monitor.Configuration.Name,
        ["vmu_id"] = monitor.Configuration.VmuId
    };

    private enum Capability { Clipboard, Mouse, Keyboard }
}

internal sealed class RemoteActionException : Exception
{
    public RemoteActionException(string code, string message, Exception? innerException = null) : base(message, innerException) => Code = code;
    public string Code { get; }
}
