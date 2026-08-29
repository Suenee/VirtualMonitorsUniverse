using Microsoft.Data.Sqlite;

namespace VirtualMonitorsUniverse.Server;

internal sealed record LogEntry(long Id, DateTime Timestamp, string Level, string Service, string? MonitorId, string Event, string Message, string? DetailsJson);
internal sealed record LogCount(long Total, long Filtered);

/// <summary>Persists and queries operational VMU events in SQLite.</summary>
internal sealed class LogStore
{
    private readonly string _databasePath;
    public LogStore(string databasePath) { _databasePath = databasePath; Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? "."); Initialize(); }

    public void Write(string level, string service, string eventName, string message, string? monitorId = null, string? detailsJson = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO log_entries(timestamp_utc,level,service,monitor_id,event,message,details_json) VALUES($timestamp,$level,$service,$monitor,$event,$message,$details);";
        command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O")); command.Parameters.AddWithValue("$level", level); command.Parameters.AddWithValue("$service", service);
        command.Parameters.AddWithValue("$monitor", (object?)monitorId ?? DBNull.Value); command.Parameters.AddWithValue("$event", eventName); command.Parameters.AddWithValue("$message", message); command.Parameters.AddWithValue("$details", (object?)detailsJson ?? DBNull.Value); command.ExecuteNonQuery();
    }

    public IReadOnlyList<LogEntry> Read(string? search = null, IReadOnlyCollection<string>? services = null, long afterId = 0)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); var where = BuildWhere(command, search, services, afterId);
        command.CommandText = $"SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries WHERE {where} ORDER BY id"; return ReadEntries(command);
    }

    public LogCount Count(string? search = null, IReadOnlyCollection<string>? services = null)
    {
        using var connection = Open(); using var total = connection.CreateCommand(); total.CommandText = "SELECT COUNT(*) FROM log_entries"; var totalCount = Convert.ToInt64(total.ExecuteScalar());
        using var filtered = connection.CreateCommand(); var where = BuildWhere(filtered, search, services, 0); filtered.CommandText = $"SELECT COUNT(*) FROM log_entries WHERE {where}"; return new LogCount(totalCount, Convert.ToInt64(filtered.ExecuteScalar()));
    }

    public LogEntry? ReadLatestForMonitor(string monitorId)
    {
        using var connection=Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries WHERE monitor_id=$id ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$id",monitorId); return ReadEntries(command).FirstOrDefault();
    }

    private static string BuildWhere(SqliteCommand command, string? search, IReadOnlyCollection<string>? services, long afterId)
    {
        var where = new List<string> { "id > $after" }; command.Parameters.AddWithValue("$after", afterId);
        if (!string.IsNullOrWhiteSpace(search)) { where.Add("(level LIKE $q OR service LIKE $q OR COALESCE(monitor_id,'') LIKE $q OR event LIKE $q OR message LIKE $q OR COALESCE(details_json,'') LIKE $q)"); command.Parameters.AddWithValue("$q", $"%{search.Trim()}%"); }
        if (services is not null) { var names=services.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if(names.Length==0) where.Add("1=0"); else { var ps=new List<string>(); for(var i=0;i<names.Length;i++){var p=$"$service{i}";ps.Add(p);command.Parameters.AddWithValue(p,names[i]);} where.Add($"service IN ({string.Join(',',ps)})"); } }
        return string.Join(" AND ", where);
    }

    public IReadOnlyList<LogEntry> ReadAll(string? search = null) => Read(search);
    public LogEntry? ReadById(long id) { using var connection=Open(); using var command=connection.CreateCommand(); command.CommandText="SELECT id,timestamp_utc,level,service,monitor_id,event,message,details_json FROM log_entries WHERE id=$id"; command.Parameters.AddWithValue("$id",id); return ReadEntries(command).FirstOrDefault(); }
    public void Clear() { using var connection=Open(); using var command=connection.CreateCommand(); command.CommandText="DELETE FROM log_entries"; command.ExecuteNonQuery(); }
    public int DeleteOlderThan(int retentionMinutes) { using var connection=Open(); using var command=connection.CreateCommand(); command.CommandText="DELETE FROM log_entries WHERE timestamp_utc < $cutoff"; command.Parameters.AddWithValue("$cutoff",DateTime.UtcNow.AddMinutes(-Math.Max(1,retentionMinutes)).ToString("O")); return command.ExecuteNonQuery(); }
    private static IReadOnlyList<LogEntry> ReadEntries(SqliteCommand command) { var result=new List<LogEntry>(); using var reader=command.ExecuteReader(); while(reader.Read()){var utc=DateTime.Parse(reader.GetString(1),null,System.Globalization.DateTimeStyles.RoundtripKind);result.Add(new LogEntry(reader.GetInt64(0),utc.ToLocalTime(),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7)));}return result; }
    private SqliteConnection Open() { var connection=new SqliteConnection($"Data Source={_databasePath}");connection.Open();return connection; }
    private void Initialize() { using var connection=Open();using var command=connection.CreateCommand();command.CommandText="""
CREATE TABLE IF NOT EXISTS log_entries(id INTEGER PRIMARY KEY AUTOINCREMENT,timestamp_utc TEXT NOT NULL,level TEXT NOT NULL,service TEXT NOT NULL,monitor_id TEXT NULL,event TEXT NOT NULL,message TEXT NOT NULL,details_json TEXT NULL);
CREATE INDEX IF NOT EXISTS ix_log_entries_timestamp ON log_entries(timestamp_utc);
CREATE INDEX IF NOT EXISTS ix_log_entries_service ON log_entries(service);
CREATE INDEX IF NOT EXISTS ix_log_entries_monitor ON log_entries(monitor_id);
""";command.ExecuteNonQuery(); }
}
