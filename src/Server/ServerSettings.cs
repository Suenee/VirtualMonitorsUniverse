using System.Text.Json;

namespace VirtualMonitorsUniverse.Server;

internal sealed class ServerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ServiceEndpointSettings Vmu { get; set; } = new() { Port = 8180 };
    public ServiceEndpointSettings Web { get; set; } = new() { Port = 8181 };
    public ServiceEndpointSettings Socket { get; set; } = new() { Port = 8182 };
    public LoggingSettings Logging { get; set; } = new();
    public ExitSettings Exit { get; set; } = new();
    public ServiceStateSettings ServiceState { get; set; } = new();

    public static ServerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // Invalid settings must not prevent the tray application from starting.
        }

        return new ServerSettings();
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private void Normalize()
    {
        Vmu ??= new ServiceEndpointSettings { Port = 8180 };
        Web ??= new ServiceEndpointSettings { Port = 8181 };
        Socket ??= new ServiceEndpointSettings { Port = 8182 };
        Logging ??= new LoggingSettings();
        Exit ??= new ExitSettings();
        ServiceState ??= new ServiceStateSettings();
        Vmu.Normalize(8180);
        Web.Normalize(8181);
        Socket.Normalize(8182);
        Logging.RetentionMinutes = Math.Max(1, Logging.RetentionMinutes);
        if (!Enum.IsDefined(Exit.MonitorAction)) Exit.MonitorAction = MonitorExitAction.Disconnect;
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

internal sealed class LoggingSettings
{
    public int RetentionMinutes { get; set; } = 10080;
}

internal enum MonitorExitAction
{
    Disconnect,
    Keep,
    Uninstall,
}

internal sealed class ExitSettings
{
    public MonitorExitAction MonitorAction { get; set; } = MonitorExitAction.Disconnect;
    public bool RestoreServices { get; set; }
}

internal sealed class ServiceStateSettings
{
    public bool VmuServerRunning { get; set; }
    public bool WebRunning { get; set; }
    public bool SocketRunning { get; set; }
}
