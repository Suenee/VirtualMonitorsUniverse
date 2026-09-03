using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualMonitorsUniverse.Server;

internal sealed class ServerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly int[] AllowedPreviewRefreshSeconds = [0, 15, 30, 60, 120, 300, 600];

    public ServiceEndpointSettings Vmu { get; set; } = new() { Port = 8180 };
    public ServiceEndpointSettings Web { get; set; } = new() { Port = 8181 };
    public ServiceEndpointSettings Socket { get; set; } = new() { Port = 8182 };
    public LoggingSettings Logging { get; set; } = new();
    public WebUiSettings WebUi { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public TerminalInputSettings TerminalInput { get; set; } = new();
    public ExitSettings Exit { get; set; } = new();
    public ServiceStateSettings ServiceState { get; set; } = new();

    public static ServerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null) { loaded.Normalize(); return loaded; }
            }
        }
        catch { }
        return new ServerSettings();
    }

    public void Save(string path)
    {
        Normalize();
        WindowsStartupService.Apply(Startup.Enabled);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private void Normalize()
    {
        Vmu ??= new ServiceEndpointSettings { Port = 8180 };
        Web ??= new ServiceEndpointSettings { Port = 8181 };
        Socket ??= new ServiceEndpointSettings { Port = 8182 };
        Logging ??= new LoggingSettings();
        WebUi ??= new WebUiSettings();
        Startup ??= new StartupSettings();
        Hotkeys ??= new HotkeySettings();
        TerminalInput ??= new TerminalInputSettings();
        Exit ??= new ExitSettings();
        ServiceState ??= new ServiceStateSettings();
        Vmu.Normalize(8180); Web.Normalize(8181); Socket.Normalize(8182);
        if (Vmu.Interface.Equals("any", StringComparison.OrdinalIgnoreCase)) Web.Interface = "any";
        Logging.RetentionMinutes = Math.Max(1, Logging.RetentionMinutes);
        if (!AllowedPreviewRefreshSeconds.Contains(WebUi.MonitorPreviewRefreshSeconds)) WebUi.MonitorPreviewRefreshSeconds = 60;
        WebUi.ArrangementSnapTolerancePx = Math.Clamp(WebUi.ArrangementSnapTolerancePx, 5, 50);
        Hotkeys.Normalize();
        if (!Enum.IsDefined(Exit.MonitorAction) || Exit.MonitorAction == MonitorExitAction.Uninstall) Exit.MonitorAction = MonitorExitAction.Disconnect;
    }
}

internal sealed class ServiceEndpointSettings
{
    public string Interface { get; set; } = "localhost";
    public int Port { get; set; }
    public void Normalize(int defaultPort)
    {
        if (!Interface.Equals("any", StringComparison.OrdinalIgnoreCase) && !Interface.Equals("localhost", StringComparison.OrdinalIgnoreCase)) Interface = "localhost";
        if (Port is < 1 or > 65535) Port = defaultPort;
    }
}

internal sealed class LoggingSettings { public int RetentionMinutes { get; set; } = 10080; }
internal sealed class WebUiSettings
{
    public int MonitorPreviewRefreshSeconds { get; set; } = 60;
    public int ArrangementSnapTolerancePx { get; set; } = 15;
}
internal sealed class StartupSettings { public bool Enabled { get; set; } }

internal sealed class HotkeySettings
{
    public string TerminalF11Forward { get; set; } = "Win+Alt+F11";

    // Migration only: VMU 0.54 used this property for Exit Fullscreen.
    // Keep reading it so existing settings files retain a customized shortcut.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullscreenExit { get; set; }

    public void Normalize()
    {
        if (!string.IsNullOrWhiteSpace(FullscreenExit) &&
            (string.IsNullOrWhiteSpace(TerminalF11Forward) || TerminalF11Forward.Equals("Win+Alt+F11", StringComparison.OrdinalIgnoreCase)))
            TerminalF11Forward = FullscreenExit.Trim();
        else if (string.IsNullOrWhiteSpace(TerminalF11Forward))
            TerminalF11Forward = "Win+Alt+F11";
        else
            TerminalF11Forward = TerminalF11Forward.Trim();
        FullscreenExit = null;
    }
}

internal sealed class TerminalInputSettings
{
    public bool MousePassthroughImmediately { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum MonitorExitAction { Disconnect, Keep, Uninstall }
internal sealed class ExitSettings { public MonitorExitAction MonitorAction { get; set; } = MonitorExitAction.Disconnect; public bool RestoreServices { get; set; } }
internal sealed class ServiceStateSettings { public bool VmuServerRunning { get; set; } public bool WebRunning { get; set; } public bool SocketRunning { get; set; } }
