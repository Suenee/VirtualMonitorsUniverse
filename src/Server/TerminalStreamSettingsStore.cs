using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualMonitorsUniverse.Server;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TerminalAdaptationMode
{
    Automatic,
    PreferQuality,
    Fixed
}

internal sealed record TerminalStreamSettings(
    TerminalAdaptationMode Mode,
    int FixedMaximumWidth,
    int FixedJpegQuality)
{
    public static TerminalStreamSettings Default { get; } = new(TerminalAdaptationMode.Automatic, 1920, 68);

    public TerminalStreamSettings Normalize()
    {
        var width = FixedMaximumWidth switch
        {
            <= 1280 => 1280,
            <= 1600 => 1600,
            _ => 1920
        };
        return this with
        {
            FixedMaximumWidth = width,
            FixedJpegQuality = Math.Clamp(FixedJpegQuality, 45, 90)
        };
    }
}

/// <summary>
/// Persists Terminal transport preferences independently from monitor identity.
/// The file is intentionally small and human-readable; monitor identity remains
/// authoritative in SQLite while stream tuning can evolve without schema churn.
/// </summary>
internal sealed class TerminalStreamSettingsStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private Dictionary<string, TerminalStreamSettings> _settings;

    public TerminalStreamSettingsStore(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "terminal-stream-settings.json");
        _settings = Load();
    }

    public TerminalStreamSettings Get(string vmuId)
    {
        lock (_sync)
            return _settings.TryGetValue(vmuId, out var value) ? value.Normalize() : TerminalStreamSettings.Default;
    }

    public TerminalStreamSettings Set(string vmuId, TerminalStreamSettings value)
    {
        value = value.Normalize();
        lock (_sync)
        {
            _settings[vmuId] = value;
            Save();
            return value;
        }
    }

    public void Delete(string vmuId)
    {
        lock (_sync)
        {
            if (!_settings.Remove(vmuId)) return;
            Save();
        }
    }

    private Dictionary<string, TerminalStreamSettings> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<Dictionary<string, TerminalStreamSettings>>(File.ReadAllText(_path));
            return values is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, true);
    }
}
