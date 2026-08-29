using Microsoft.Data.Sqlite;

namespace VirtualMonitorsUniverse.Server;

internal sealed record LogEntry(long Id, DateTime Timestamp, string Level, string Service, string? MonitorId, string Event, string Message, string? DetailsJson);
internal sealed record LogCount(long Total, long Filtered);
internal sealed record LogRetentionCleanupResult(
    string DatabasePath,
    DateTime NowUtc,
    DateTime CutoffUtc,
    long TotalBefore,
    long Candidates,
    int Deleted,
    long TotalAfter,
    string? OldestTimestampUtc,
    string? NewestTimestampUtc,
    bool SafetyBlocked);

/// <summary>Persists and queries operational VMU events in SQLite.</summary>
internal sealed class LogStore
{
    private readonly string _databasePath;

    public LogStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? ".");
        Initialize();
    }

    public string DatabasePath => _databasePath;

    public void Write(string level, string service, string eventName, string message, string? monitorId = null, string? detailsJson = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO log_entries(timestamp_utc,level,service,monitor_id,event,message,details_json) VALUES($timestamp,$level,$service,$monitor,$event,$message,$details);";
        command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$service", service);
        command.Parameters.AddWithValue("$monitor", (object?)monitorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$event", eventName);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$details", (object?)detailsJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<LogEntry> Read(string? search = null, IReadOnlyCollection<string>? services = null, long afterId = 0)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = BuildWhere(command, search, services, afterId);
        command.CommandText = $"SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries WHERE {where} ORDER BY id";
        return ReadEntries(command);
    }

    public LogCount Count(string? search = null, IReadOnlyCollection<string>? services = null)
    {
        using var connection = Open();
        using var total = connection.CreateCommand();
        total.CommandText = "SELECT COUNT(*) FROM log_entries";
        var totalCount = Convert.ToInt64(total.ExecuteScalar());

        using var filtered = connection.CreateCommand();
        var where = BuildWhere(filtered, search, services, 0);
        filtered.CommandText = $"SELECT COUNT(*) FROM log_entries WHERE {where}";
        return new LogCount(totalCount, Convert.ToInt64(filtered.ExecuteScalar()));
    }

    public LogEntry? ReadLatestForMonitor(string monitorId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries WHERE monitor_id=$id ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", monitorId);
        return ReadEntries(command).FirstOrDefault();
    }

    private static string BuildWhere(SqliteCommand command, string? search, IReadOnlyCollection<string>? services, long afterId)
    {
        var where = new List<string> { "id > $after" };
        command.Parameters.AddWithValue("$after", afterId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(level LIKE $q OR service LIKE $q OR COALESCE(monitor_id,'') LIKE $q OR event LIKE $q OR message LIKE $q OR COALESCE(details_json,'') LIKE $q)");
            command.Parameters.AddWithValue("$q", $"%{search.Trim()}%");
        }

        if (services is not null)
        {
            var names = services.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 0)
            {
                where.Add("1=0");
            }
            else
            {
                var parameters = new List<string>();
                for (var i = 0; i < names.Length; i++)
                {
                    var parameter = $"$service{i}";
                    parameters.Add(parameter);
                    command.Parameters.AddWithValue(parameter, names[i]);
                }
                where.Add($"service IN ({string.Join(',', parameters)})");
            }
        }

        return string.Join(" AND ", where);
    }

    public IReadOnlyList<LogEntry> ReadAll(string? search = null) => Read(search);

    public LogEntry? ReadById(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return ReadEntries(command).FirstOrDefault();
    }

    public void Clear()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM log_entries";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes rows older than the configured retention period. SQLite date conversion is used
    /// deliberately instead of lexical TEXT comparison so a malformed/legacy timestamp format
    /// cannot move the retention boundary unexpectedly. A large-delete guard prevents one bad
    /// cutoff from destroying most of the operational history in a single pass.
    /// </summary>
    public LogRetentionCleanupResult CleanupOlderThan(int retentionMinutes)
    {
        var nowUtc = DateTime.UtcNow;
        var cutoffUtc = nowUtc.AddMinutes(-Math.Max(1, retentionMinutes));
        var cutoff = cutoffUtc.ToString("O");

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        long ScalarLong(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        string? ScalarString(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value is null || value is DBNull ? null : Convert.ToString(value);
        }

        var totalBefore = ScalarLong("SELECT COUNT(*) FROM log_entries");
        var oldest = ScalarString("SELECT MIN(timestamp_utc) FROM log_entries");
        var newest = ScalarString("SELECT MAX(timestamp_utc) FROM log_entries");

        using var candidateCommand = connection.CreateCommand();
        candidateCommand.Transaction = transaction;
        candidateCommand.CommandText = "SELECT COUNT(*) FROM log_entries WHERE julianday(timestamp_utc) IS NOT NULL AND julianday(timestamp_utc) < julianday($cutoff)";
        candidateCommand.Parameters.AddWithValue("$cutoff", cutoff);
        var candidates = Convert.ToInt64(candidateCommand.ExecuteScalar());

        var safetyBlocked = totalBefore >= 10 && candidates > 0 && candidates >= Math.Ceiling(totalBefore * 0.80d);
        var deleted = 0;
        if (!safetyBlocked && candidates > 0)
        {
            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM log_entries WHERE julianday(timestamp_utc) IS NOT NULL AND julianday(timestamp_utc) < julianday($cutoff)";
            deleteCommand.Parameters.AddWithValue("$cutoff", cutoff);
            deleted = deleteCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        var totalAfter = totalBefore - deleted;
        return new LogRetentionCleanupResult(_databasePath, nowUtc, cutoffUtc, totalBefore, candidates, deleted, totalAfter, oldest, newest, safetyBlocked);
    }

    private static IReadOnlyList<LogEntry> ReadEntries(SqliteCommand command)
    {
        var result = new List<LogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var utc = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
            result.Add(new LogEntry(
                reader.GetInt64(0),
                utc.ToLocalTime(),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
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
CREATE TABLE IF NOT EXISTS log_entries(id INTEGER PRIMARY KEY AUTOINCREMENT,timestamp_utc TEXT NOT NULL,level TEXT NOT NULL,service TEXT NOT NULL,monitor_id TEXT NULL,event TEXT NOT NULL,message TEXT NOT NULL,details_json TEXT NULL);
CREATE INDEX IF NOT EXISTS ix_log_entries_timestamp ON log_entries(timestamp_utc);
CREATE INDEX IF NOT EXISTS ix_log_entries_service ON log_entries(service);
CREATE INDEX IF NOT EXISTS ix_log_entries_monitor ON log_entries(monitor_id);
""";
        command.ExecuteNonQuery();
    }
}
