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
        command.CommandText = "SELECT vmu_id,friendly_name,device_name,width,height,refresh_rate,portrait,remote_access,password_enabled,api_key_enabled,api_key,approval_enabled FROM monitors ORDER BY friendly_name COLLATE NOCASE, vmu_id";
        return Read(command);
    }

    public MonitorRecord? Get(string vmuId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT vmu_id,friendly_name,device_name,width,height,refresh_rate,portrait,remote_access,password_enabled,api_key_enabled,api_key,approval_enabled FROM monitors WHERE vmu_id=$id";
        command.Parameters.AddWithValue("$id", vmuId);
        return Read(command).FirstOrDefault();
    }

    public MonitorRecord EnsureForDevice(string deviceName, int width, int height, int refreshRate)
    {
        using var connection = Open();
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT vmu_id,friendly_name,device_name,width,height,refresh_rate,portrait,remote_access,password_enabled,api_key_enabled,api_key,approval_enabled FROM monitors WHERE device_name=$device";
            lookup.Parameters.AddWithValue("$device", deviceName);
            var existing = Read(lookup).FirstOrDefault();
            if (existing is not null)
            {
                UpdateObservedMode(connection, existing.VmuId, width, height, refreshRate);
                return existing with { Width = width, Height = height, RefreshRate = refreshRate };
            }
        }

        var number = NextFriendlyNumber(connection);
        var record = new MonitorRecord(Guid.NewGuid().ToString("N"), $"Monitor {number}", deviceName, width, height, refreshRate, height > width, RemoteAccessMode.Disabled, false, false, null, false);
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO monitors(vmu_id,friendly_name,device_name,width,height,refresh_rate,portrait,remote_access,password_enabled,password_hash,api_key_enabled,api_key,approval_enabled,created_utc)
            VALUES($id,$name,$device,$width,$height,$refresh,$portrait,$remote,0,NULL,0,NULL,0,$created)
            """;
        insert.Parameters.AddWithValue("$id", record.VmuId);
        insert.Parameters.AddWithValue("$name", record.FriendlyName);
        insert.Parameters.AddWithValue("$device", deviceName);
        insert.Parameters.AddWithValue("$width", width);
        insert.Parameters.AddWithValue("$height", height);
        insert.Parameters.AddWithValue("$refresh", refreshRate);
        insert.Parameters.AddWithValue("$portrait", record.Portrait ? 1 : 0);
        insert.Parameters.AddWithValue("$remote", record.RemoteAccess.ToString());
        insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return record;
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
                // A concurrent save may have claimed the same random key between the
                // pre-check and UPDATE. The database UNIQUE constraint is authoritative;
                // generate another key instead of ever accepting a duplicate.
            }
        }

        throw new InvalidOperationException("Could not generate a unique API key after repeated attempts.");
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

    private static void UpdateObservedMode(SqliteConnection connection, string id, int width, int height, int refreshRate)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE monitors SET width=$width,height=$height,refresh_rate=$refresh WHERE vmu_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$refresh", refreshRate);
        command.ExecuteNonQuery();
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
            _ = Enum.TryParse<RemoteAccessMode>(reader.GetString(7), true, out var remote);
            result.Add(new MonitorRecord(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6) != 0, remote,
                reader.GetInt32(8) != 0, reader.GetInt32(9) != 0, reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt32(11) != 0));
        }
        return result;
    }

    private void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? ".");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS monitors (
                vmu_id TEXT PRIMARY KEY,
                friendly_name TEXT NOT NULL,
                device_name TEXT NULL UNIQUE,
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
            CREATE UNIQUE INDEX IF NOT EXISTS ux_monitors_api_key ON monitors(api_key) WHERE api_key IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }
}
