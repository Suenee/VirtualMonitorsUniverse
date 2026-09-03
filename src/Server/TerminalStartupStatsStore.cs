using Microsoft.Data.Sqlite;

namespace VirtualMonitorsUniverse.Server;

internal sealed record TerminalStartupEstimate(int ExpectedMilliseconds, int SuccessfulSamples, int FailedSamples);

/// <summary>
/// Stores short-lived Terminal startup timing samples in the VMU SQLite database.
/// History is retained for the latest seven active days per monitor and is capped
/// at 100 rows so development diagnostics cannot grow without bound.
/// </summary>
internal sealed class TerminalStartupStatsStore
{
    private const int ActiveDayLimit = 7;
    private const int RowLimitPerMonitor = 100;
    private readonly string _databasePath;

    public TerminalStartupStatsStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        Initialize();
    }

    public TerminalStartupEstimate ReadEstimate(string monitorName)
    {
        Cleanup(monitorName);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT duration_ms, success FROM terminal_start_history WHERE monitor_name=$monitor ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$monitor", monitorName);
        command.Parameters.AddWithValue("$limit", RowLimitPerMonitor);

        var successes = new List<int>();
        var failures = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(1) != 0) successes.Add(reader.GetInt32(0));
            else failures++;
        }

        if (successes.Count == 0)
            return new TerminalStartupEstimate(0, 0, failures);

        successes.Sort();
        var percentileIndex = (int)Math.Ceiling(successes.Count * 0.90d) - 1;
        percentileIndex = Math.Clamp(percentileIndex, 0, successes.Count - 1);
        var p90 = successes[percentileIndex];
        var expected = Math.Clamp((int)Math.Ceiling(p90 * 1.25d), 1000, 30000);
        return new TerminalStartupEstimate(expected, successes.Count, failures);
    }

    public void Record(string monitorName, int durationMilliseconds, bool success)
    {
        durationMilliseconds = Math.Clamp(durationMilliseconds, 0, 300000);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO terminal_start_history(monitor_name,started_utc,duration_ms,success) VALUES($monitor,$timestamp,$duration,$success)";
        command.Parameters.AddWithValue("$monitor", monitorName);
        command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$duration", durationMilliseconds);
        command.Parameters.AddWithValue("$success", success ? 1 : 0);
        command.ExecuteNonQuery();
        Cleanup(monitorName);
    }

    public void Delete(string monitorName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM terminal_start_history WHERE monitor_name=$monitor";
        command.Parameters.AddWithValue("$monitor", monitorName);
        command.ExecuteNonQuery();
    }

    private void Cleanup(string monitorName)
    {
        using var connection = Open();

        string? oldestKeptDay;
        using (var days = connection.CreateCommand())
        {
            days.CommandText = "SELECT substr(started_utc,1,10) FROM terminal_start_history WHERE monitor_name=$monitor GROUP BY substr(started_utc,1,10) ORDER BY substr(started_utc,1,10) DESC LIMIT 1 OFFSET $offset";
            days.Parameters.AddWithValue("$monitor", monitorName);
            days.Parameters.AddWithValue("$offset", ActiveDayLimit - 1);
            oldestKeptDay = days.ExecuteScalar() as string;
        }

        if (!string.IsNullOrWhiteSpace(oldestKeptDay))
        {
            using var old = connection.CreateCommand();
            old.CommandText = "DELETE FROM terminal_start_history WHERE monitor_name=$monitor AND substr(started_utc,1,10) < $day";
            old.Parameters.AddWithValue("$monitor", monitorName);
            old.Parameters.AddWithValue("$day", oldestKeptDay);
            old.ExecuteNonQuery();
        }

        using var overflow = connection.CreateCommand();
        overflow.CommandText = "DELETE FROM terminal_start_history WHERE monitor_name=$monitor AND id NOT IN (SELECT id FROM terminal_start_history WHERE monitor_name=$monitor ORDER BY id DESC LIMIT $limit)";
        overflow.Parameters.AddWithValue("$monitor", monitorName);
        overflow.Parameters.AddWithValue("$limit", RowLimitPerMonitor);
        overflow.ExecuteNonQuery();
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
CREATE TABLE IF NOT EXISTS terminal_start_history(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    monitor_name TEXT NOT NULL,
    started_utc TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    success INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_terminal_start_history_monitor ON terminal_start_history(monitor_name,id DESC);
CREATE INDEX IF NOT EXISTS ix_terminal_start_history_timestamp ON terminal_start_history(started_utc);
""";
        command.ExecuteNonQuery();
    }
}
