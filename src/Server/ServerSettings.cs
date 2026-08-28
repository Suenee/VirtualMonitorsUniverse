using System.Text.Json;

namespace VirtualMonitorsUniverse.Server;

internal sealed class ServerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LoggingSettings Logging { get; set; } = new();

    public static ServerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), JsonOptions) ?? new ServerSettings();
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
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

internal sealed class LoggingSettings
{
    public int RetentionMinutes { get; set; } = 10080;
}
