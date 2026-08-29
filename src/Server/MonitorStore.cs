using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace VirtualMonitorsUniverse.Server;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RemoteAccessMode
{
    Disabled,
    Presentation,
    Collaboration,
}

internal sealed record MonitorRecord(
    string VmuId,
    string FriendlyName,
    string? DeviceName,
    string? InstanceId,
    int Width,
    int Height,
    int RefreshRate,
    bool Portrait,
    RemoteAccessMode RemoteAccess,
    bool PasswordEnabled,
    bool ApiKeyEnabled,
    string? ApiKey,
    bool ApprovalEnabled);

internal sealed class MonitorStore
{
    private readonly string _databasePath;

    public MonitorStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    public IReadOnlyList<MonitorRecord> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " FROM monitors ORDER BY friendly_name COLLATE NOCASE, vmu_id";
        return Read(command);
    }

    public MonitorRecord? Get(string vmuId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " FROM monitors WHERE vmu_id=$id";
        command.Parameters.AddWithValue("$id", vmuId);
        return Read(command).FirstOrDefault();
    }

    public MonitorRecord EnsureForDevice(string deviceName, string? instanceId, int width, int height, int refreshRate)
    {
        using var connection = Open();
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = SelectColumns + " FROM monitors WHERE device_name=$device OR ($instance IS NOT NULL AND instance_id=$instance) LIMIT 1";
            lookup.Parameters.AddWithValue("$device", deviceName);
            lookup.Parameters.AddWithValue("$instance", (object?)instanceId ?? DBNull.Value);
            var existing = Read(lookup).FirstOrDefault();
            if (existing is not null)
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE monitors SET device_name=$device,instance_id=COALESCE($instance,instance_id),width=$width,height=$height,refresh_rate=$refresh WHERE vmu_id=$id";
                update.Parameters.AddWithValue("$device", deviceName);
                update.Parameters.AddWithValue("$instance", (object?)instanceId ?? DBNull.Value);
                update.Parameters.AddWithValue("$width", width);
                update.Parameters.AddWithValue("$height", height);
                update.Parameters.AddWithValue("$refresh", refreshRate);
                update.Parameters.AddWithValue("$id", existing.VmuId);
                update.ExecuteNonQuery();
                return Get(existing.VmuId)!;
            }
        }

        return Insert(connection, $"Monitor {NextFriendlyNumber(connection)}", deviceName, instanceId, width, height, refreshRate, height > width);
    }

    public MonitorRecord CreateBound(string friendlyName, string deviceName, string instanceId, int width, int height, int refreshRate, bool portrait)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("Windows display identity is required.", nameof(deviceName));
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("PnP instance identity is required.", nameof(instanceId));
        using var connection = Open();
        var name = string.IsNullOrWhiteSpace(friendlyName) ? $"Monitor {NextFriendlyNumber(connection)}" : friendlyName.Trim();
        return Insert(connection, name, deviceName, instanceId, width, height, refreshRate, portrait);
    }

    public void Delete(string vmuId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM monitors WHERE vmu_id=$id";
        command.Parameters.AddWithValue("$id", vmuId);
        command.ExecuteNonQuery();
    }

    public MonitorRecord Update(
        string vmuId,
        string friendlyName,
        int width,
        int height,
        int refreshRate,
        bool portrait,
        RemoteAccessMode remoteAccess,
        bool passwordEnabled,
        string? password,
        bool apiKeyEnabled,
        bool regenerateApiKey,
        bool approvalEnabled)
    {
        var existing = Get(vmuId) ?? throw new KeyNotFoundException($"Monitor '{vmuId}' was not found.");
        var requestedName = string.IsNullOrWhiteSpace(friendlyName) ? existing.FriendlyName : friendlyName.Trim();
        var passwordHash = string.IsNullOrEmpty(password) ? null : HashPassword(password);

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var apiKey = existing.ApiKey;
            if (apiKeyEnabled && (string.IsNullOrWhiteSpace(apiKey) || regenerateApiKey || attempt > 0)) apiKey = GenerateUniqueApiKey();
            if (!apiKeyEnabled) apiKey = null;

            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE monitors SET friendly_name=$name,width=$width,height=$height,refresh_rate=$refresh,portrait=$portrait,
                        remote_access=$remote,password_enabled=$password_enabled,
                        password_hash=CASE WHEN $password_hash IS NULL THEN password_hash ELSE $password_hash END,
                        api_key_enabled=$api_enabled,api_key=$api_key,approval_enabled=$approval
                    WHERE vmu_id=$id
                    """;
                command.Parameters.AddWithValue("$id", vmuId);
                command.Parameters.AddWithValue("$name", requestedName);
                command.Parameters.AddWithValue("$width", width);
                command.Parameters.AddWithValue("$height", height);
                command.Parameters.AddWithValue("$refresh", refreshRate);
                command.Parameters.AddWithValue("$portrait", portrait ? 1 : 0);
                command.Parameters.AddWithValue("$remote", remoteAccess.ToString());
                command.Parameters.AddWithValue("$password_enabled", passwordEnabled ? 1 : 0);
                command.Parameters.AddWithValue("$password_hash", (object?)passwordHash ?? DBNull.Value);
                command.Parameters.AddWithValue("$api_enabled", apiKeyEnabled ? 1 : 0);
                command.Parameters.AddWithValue("$api_key", (object?)apiKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$approval", approvalEnabled ? 1 : 0);
                command.ExecuteNonQuery();
                return Get(vmuId)!;
            }
            catch (SqliteException ex) when (apiKeyEnabled && ex.SqliteErrorCode == 19)
            {
                // The database UNIQUE constraint is authoritative. Generate another
                // key if another concurrent save claimed the same random value.
            }
        }

        throw new InvalidOperationException("Could not generate a unique API key after repeated attempts.");
    }

    private MonitorRecord Insert(SqliteConnection connection, string friendlyName, string deviceName, string? instanceId, int width, int height, int refreshRate, bool portrait)
    {
        var record = new MonitorRecord(Guid.NewGuid().ToString("N"), friendlyName, deviceName, instanceId, width, height, refreshRate, portrait, RemoteAccessMode.Disabled, false, false, null, false);
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO monitors(vmu_id,friendly_name,device_name,instance_id,width,height,refresh_rate,portrait,remote_access,password_enabled,password_hash,api_key_enabled,api_key,approval_enabled,created_utc)
            VALUES($id,$name,$device,$instance,$width,$height,$refresh,$portrait,$remote,0,NULL,0,NULL,0,$created)
            """;
        insert.Parameters.AddWithValue("$id", record.VmuId);
        insert.Parameters.AddWithValue("$name", record.FriendlyName);
        insert.Parameters.AddWithValue("$device", deviceName);
        insert.Parameters.AddWithValue("$instance", (object?)instanceId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$width", width);
        insert.Parameters.AddWithValue("$height", height);
        insert.Parameters.AddWithValue("$refresh", refreshRate);
        insert.Parameters.AddWithValue("$portrait", portrait ? 1 : 0);
        insert.Parameters.AddWithValue("$remote", record.RemoteAccess.ToString());
        insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return record;
    }

    private string GenerateUniqueApiKey()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM monitors WHERE api_key=$key LIMIT 1";
            command.Parameters.AddWithValue("$key", candidate);
            if (command.ExecuteScalar() is null) return candidate;
        }
        throw new InvalidOperationException("Could not generate a unique API key.");
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2-SHA256$120000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static int NextFriendlyNumber(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitors";
        return Convert.ToInt32(command.ExecuteScalar()) + 1;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<MonitorRecord> Read(SqliteCommand command)
    {
        var result = new List<MonitorRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _ = Enum.TryParse<RemoteAccessMode>(reader.GetString(8), true, out var remote);
            result.Add(new MonitorRecord(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7) != 0, remote,
                reader.GetInt32(9) != 0, reader.GetInt32(10) != 0, reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetInt32(12) != 0));
        }
        return result;
    }

    private void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? ".");
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS monitors (
                    vmu_id TEXT PRIMARY KEY,
                    friendly_name TEXT NOT NULL,
                    device_name TEXT NULL UNIQUE,
                    instance_id TEXT NULL UNIQUE,
                    width INTEGER NOT NULL,
                    height INTEGER NOT NULL,
                    refresh_rate INTEGER NOT NULL,
                    portrait INTEGER NOT NULL DEFAULT 0,
                    remote_access TEXT NOT NULL DEFAULT 'Disabled',
                    password_enabled INTEGER NOT NULL DEFAULT 0,
                    password_hash TEXT NULL,
                    api_key_enabled INTEGER NOT NULL DEFAULT 0,
                    api_key TEXT NULL UNIQUE,
                    approval_enabled INTEGER NOT NULL DEFAULT 0,
                    created_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        if (!HasColumn(connection, "monitors", "instance_id"))
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE monitors ADD COLUMN instance_id TEXT NULL";
            migration.ExecuteNonQuery();
        }

        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_monitors_api_key ON monitors(api_key) WHERE api_key IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_monitors_instance_id ON monitors(instance_id) WHERE instance_id IS NOT NULL;
            """;
        indexes.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private const string SelectColumns = "SELECT vmu_id,friendly_name,device_name,instance_id,width,height,refresh_rate,portrait,remote_access,password_enabled,api_key_enabled,api_key,approval_enabled";
}
