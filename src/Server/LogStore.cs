using Microsoft.Data.Sqlite;

namespace VirtualMonitorsUniverse.Server;

internal sealed record LogEntry(long Id, DateTime Timestamp, string Level, string Service, string? MonitorId, string Event, string Message, string? DetailsJson);

/// <summary>
/// Persists operational VMU events in SQLite and applies the configured retention policy.
/// </summary>
internal sealed class LogStore
{
    private readonly string _databasePath;

    public LogStore(string databasePath)
    {
        _databasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        Initialize();
    }

    public void Write(string level, string service, string eventName, string message, string? monitorId = null, string? detailsJson = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO log_entries(timestamp_utc, level, service, monitor_id, event, message, details_json)
            VALUES ($timestamp, $level, $service, $monitor, $event, $message, $details);
            """;
        command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$service", service);
        command.Parameters.AddWithValue("$monitor", (object?)monitorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$event", eventName);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$details", (object?)detailsJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LogEntry> ReadAll(string? search = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(search)
            ? "SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries ORDER BY id"
            : "SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries WHERE level LIKE $q OR service LIKE $q OR COALESCE(monitor_id,'') LIKE $q OR event LIKE $q OR message LIKE $q OR COALESCE(details_json,'') LIKE $q ORDER BY id";
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("$q", $"%{search.Trim()}%");

        var result = new List<LogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var utc = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
            result.Add(new LogEntry(reader.GetInt64(0), utc.ToLocalTime(), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    public void Clear()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM log_entries";
        command.ExecuteNonQuery();
    }

    public int DeleteOlderThan(int retentionMinutes)
    {
        var safeMinutes = Math.Max(1, retentionMinutes);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM log_entries WHERE timestamp_utc < $cutoff";
        command.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddMinutes(-safeMinutes).ToString("O"));
        return command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS log_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                level TEXT NOT NULL,
                service TEXT NOT NULL,
                monitor_id TEXT NULL,
                event TEXT NOT NULL,
                message TEXT NOT NULL,
                details_json TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_log_entries_timestamp ON log_entries(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_log_entries_monitor ON log_entries(monitor_id);
            """;
        command.ExecuteNonQuery();
    }
}
