using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace VirtualMonitorsUniverse.Server;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RemoteAccessMode { Disabled, Presentation, Collaboration }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RemoteSecurityMode { Public, Password, ApiKey, Approval }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum AccessPermission { Deny, Deferred, Allow }

internal sealed record MonitorAccessRule(long Id, string VmuId, string ClientId, string? IpAddress, string? MacAddress, string? ComputerName, string? UserName, AccessPermission Permission, DateTime? LastSeenUtc);

internal sealed record MonitorRecord(
    string VmuId,
    string Name,
    string Title,
    string? DeviceName,
    string? InstanceId,
    int Width,
    int Height,
    int RefreshRate,
    bool Portrait,
    string AvatarKind,
    string AvatarValue,
    RemoteAccessMode RemoteAccess,
    RemoteSecurityMode SecurityMode,
    string? ApiKey,
    bool CollaborationClipboard,
    bool CollaborationMouse,
    bool CollaborationKeyboard);

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
        command.CommandText = SelectColumns + " FROM monitors ORDER BY title COLLATE NOCASE, canonical_name";
        return Read(command);
    }

    public MonitorRecord? Get(string idOrName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " FROM monitors WHERE vmu_id=$id OR canonical_name=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", idOrName.Trim());
        return Read(command).FirstOrDefault();
    }

    public bool NameExists(string name, string? exceptVmuId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM monitors WHERE canonical_name=$name AND ($except IS NULL OR vmu_id<>$except) LIMIT 1";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$except", (object?)exceptVmuId ?? DBNull.Value);
        return command.ExecuteScalar() is not null;
    }

    public (string Name, string Title) NormalizeIdentity(string? name, string? title, string? exceptVmuId = null)
    {
        name = name?.Trim();
        title = title?.Trim();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(title))
        {
            var number = 1;
            do { name = $"virtual-monitor-{number++}"; } while (NameExists(name, exceptVmuId));
            title = ToTitle(name);
        }
        else if (string.IsNullOrWhiteSpace(name)) name = Slugify(title!);
        else name = NormalizeCanonical(name);

        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Name must contain at least one letter or number.");
        if (string.IsNullOrWhiteSpace(title)) title = ToTitle(name);
        if (NameExists(name, exceptVmuId)) throw new InvalidOperationException($"Monitor Name '{name}' already exists.");
        return (name, title.Trim());
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

        var identity = NormalizeIdentity(null, null);
        return Insert(connection, identity.Name, identity.Title, deviceName, instanceId, width, height, refreshRate, height > width, "animal", MonitorAvatarService.RandomAnimal());
    }

    public MonitorRecord ApplyCreationIdentity(string vmuId, string? name, string? title, string? avatarAnimal)
    {
        var identity = NormalizeIdentity(name, title, vmuId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE monitors SET canonical_name=$name,title=$title,avatar_kind='animal',avatar_value=$avatar WHERE vmu_id=$id";
        command.Parameters.AddWithValue("$id", vmuId);
        command.Parameters.AddWithValue("$name", identity.Name);
        command.Parameters.AddWithValue("$title", identity.Title);
        command.Parameters.AddWithValue("$avatar", ValidAnimal(avatarAnimal) ? avatarAnimal! : MonitorAvatarService.RandomAnimal());
        command.ExecuteNonQuery();
        return Get(vmuId)!;
    }

    public MonitorRecord Update(
        string idOrName, string? name, string? title, int width, int height, int refreshRate, bool portrait,
        RemoteAccessMode remoteAccess, RemoteSecurityMode securityMode, string? password, bool regenerateApiKey,
        bool collaborationClipboard, bool collaborationMouse, bool collaborationKeyboard)
    {
        var existing = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        var identity = NormalizeIdentity(name, title, existing.VmuId);
        if (remoteAccess == RemoteAccessMode.Collaboration && !collaborationClipboard && !collaborationMouse && !collaborationKeyboard)
            remoteAccess = RemoteAccessMode.Presentation;
        var passwordHash = string.IsNullOrEmpty(password) ? null : HashPassword(password);

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var apiKey = existing.ApiKey;
            if (securityMode == RemoteSecurityMode.ApiKey && (string.IsNullOrWhiteSpace(apiKey) || regenerateApiKey || attempt > 0)) apiKey = GenerateUniqueApiKey();
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE monitors SET canonical_name=$name,title=$title,width=$width,height=$height,refresh_rate=$refresh,portrait=$portrait,
                        remote_access=$remote,security_mode=$security,
                        password_hash=CASE WHEN $password_hash IS NULL THEN password_hash ELSE $password_hash END,
                        api_key=$api_key,collaboration_clipboard=$clipboard,collaboration_mouse=$mouse,collaboration_keyboard=$keyboard
                    WHERE vmu_id=$id
                    """;
                command.Parameters.AddWithValue("$id", existing.VmuId);
                command.Parameters.AddWithValue("$name", identity.Name);
                command.Parameters.AddWithValue("$title", identity.Title);
                command.Parameters.AddWithValue("$width", width);
                command.Parameters.AddWithValue("$height", height);
                command.Parameters.AddWithValue("$refresh", refreshRate);
                command.Parameters.AddWithValue("$portrait", portrait ? 1 : 0);
                command.Parameters.AddWithValue("$remote", remoteAccess.ToString());
                command.Parameters.AddWithValue("$security", securityMode.ToString());
                command.Parameters.AddWithValue("$password_hash", (object?)passwordHash ?? DBNull.Value);
                command.Parameters.AddWithValue("$api_key", (object?)apiKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$clipboard", collaborationClipboard ? 1 : 0);
                command.Parameters.AddWithValue("$mouse", collaborationMouse ? 1 : 0);
                command.Parameters.AddWithValue("$keyboard", collaborationKeyboard ? 1 : 0);
                command.ExecuteNonQuery();
                return Get(existing.VmuId)!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && securityMode == RemoteSecurityMode.ApiKey) { }
        }
        throw new InvalidOperationException("Could not generate a unique API key after repeated attempts.");
    }

    public MonitorRecord SetAnimalAvatar(string idOrName, string animal)
    {
        if (!ValidAnimal(animal)) throw new InvalidOperationException("Unknown built-in avatar.");
        return SetAvatar(idOrName, "animal", animal);
    }

    public MonitorRecord SetCustomAvatar(string idOrName, string storedName) => SetAvatar(idOrName, "custom", storedName);

    private MonitorRecord SetAvatar(string idOrName, string kind, string value)
    {
        var monitor = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE monitors SET avatar_kind=$kind,avatar_value=$value WHERE vmu_id=$id";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$id", monitor.VmuId);
        command.ExecuteNonQuery();
        return Get(monitor.VmuId)!;
    }

    public IReadOnlyList<MonitorAccessRule> ListAccessRules(string idOrName)
    {
        var monitor = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,vmu_id,client_id,ip_address,mac_address,computer_name,user_name,permission,last_seen_utc FROM monitor_access_rules WHERE vmu_id=$id ORDER BY COALESCE(computer_name,client_id) COLLATE NOCASE";
        command.Parameters.AddWithValue("$id", monitor.VmuId);
        var result = new List<MonitorAccessRule>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _ = Enum.TryParse<AccessPermission>(reader.GetString(7), true, out var permission);
            result.Add(new MonitorAccessRule(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), permission,
                reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    public MonitorAccessRule UpsertAccessRule(string idOrName, string clientId, string? ipAddress, string? macAddress, string? computerName, string? userName, AccessPermission permission)
    {
        var monitor = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        clientId = clientId.Trim();
        if (clientId.Length == 0) throw new InvalidOperationException("Client/User ID is required.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO monitor_access_rules(vmu_id,client_id,ip_address,mac_address,computer_name,user_name,permission,created_utc,updated_utc)
            VALUES($vmu,$client,$ip,$mac,$computer,$user,$permission,$now,$now)
            ON CONFLICT(vmu_id,client_id) DO UPDATE SET ip_address=$ip,mac_address=$mac,computer_name=$computer,user_name=$user,permission=$permission,updated_utc=$now
            """;
        command.Parameters.AddWithValue("$vmu", monitor.VmuId);
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$ip", Db(ipAddress));
        command.Parameters.AddWithValue("$mac", Db(macAddress));
        command.Parameters.AddWithValue("$computer", Db(computerName));
        command.Parameters.AddWithValue("$user", Db(userName));
        command.Parameters.AddWithValue("$permission", permission.ToString());
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return ListAccessRules(monitor.VmuId).Single(x => x.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteAccessRule(string idOrName, long ruleId)
    {
        var monitor = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM monitor_access_rules WHERE id=$rule AND vmu_id=$vmu";
        command.Parameters.AddWithValue("$rule", ruleId);
        command.Parameters.AddWithValue("$vmu", monitor.VmuId);
        command.ExecuteNonQuery();
    }

    public void Delete(string idOrName)
    {
        var monitor = Get(idOrName);
        if (monitor is null) return;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM monitors WHERE vmu_id=$id";
        command.Parameters.AddWithValue("$id", monitor.VmuId);
        command.ExecuteNonQuery();
    }

    private MonitorRecord Insert(SqliteConnection connection, string name, string title, string deviceName, string? instanceId, int width, int height, int refreshRate, bool portrait, string avatarKind, string avatarValue)
    {
        var record = new MonitorRecord(Guid.NewGuid().ToString("N"), name, title, deviceName, instanceId, width, height, refreshRate, portrait, avatarKind, avatarValue, RemoteAccessMode.Disabled, RemoteSecurityMode.Public, null, true, true, true);
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO monitors(vmu_id,friendly_name,canonical_name,title,device_name,instance_id,width,height,refresh_rate,portrait,avatar_kind,avatar_value,remote_access,security_mode,password_enabled,password_hash,api_key_enabled,api_key,approval_enabled,collaboration_clipboard,collaboration_mouse,collaboration_keyboard,created_utc)
            VALUES($id,$title,$name,$title,$device,$instance,$width,$height,$refresh,$portrait,$avatar_kind,$avatar_value,'Disabled','Public',0,NULL,0,NULL,0,1,1,1,$created)
            """;
        insert.Parameters.AddWithValue("$id", record.VmuId);
        insert.Parameters.AddWithValue("$name", record.Name);
        insert.Parameters.AddWithValue("$title", record.Title);
        insert.Parameters.AddWithValue("$device", deviceName);
        insert.Parameters.AddWithValue("$instance", (object?)instanceId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$width", width);
        insert.Parameters.AddWithValue("$height", height);
        insert.Parameters.AddWithValue("$refresh", refreshRate);
        insert.Parameters.AddWithValue("$portrait", portrait ? 1 : 0);
        insert.Parameters.AddWithValue("$avatar_kind", avatarKind);
        insert.Parameters.AddWithValue("$avatar_value", avatarValue);
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

    public static string NormalizeCanonical(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.Length == 0 || value.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || !char.IsLetterOrDigit(value[0]))
            throw new InvalidOperationException("Name may contain only a-z, 0-9 and hyphen, and must start with a letter or number.");
        return value;
    }

    public static string Slugify(string value)
    {
        value = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        var pendingDash = false;
        foreach (var c in value)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingDash && builder.Length > 0) builder.Append('-');
                builder.Append(lower);
                pendingDash = false;
            }
            else pendingDash = builder.Length > 0;
        }
        return builder.ToString().Trim('-');
    }

    public static string ToTitle(string name) => string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static bool ValidAnimal(string? animal) => animal is not null && MonitorAvatarService.AnimalNames.Contains(animal, StringComparer.OrdinalIgnoreCase);
    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Foreign Keys=True");
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<MonitorRecord> Read(SqliteCommand command)
    {
        var result = new List<MonitorRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _ = Enum.TryParse<RemoteAccessMode>(reader.GetString(11), true, out var remote);
            _ = Enum.TryParse<RemoteSecurityMode>(reader.GetString(12), true, out var security);
            result.Add(new MonitorRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8) != 0,
                reader.GetString(9), reader.GetString(10), remote, security, NullableString(reader, 13),
                reader.GetInt32(14) != 0, reader.GetInt32(15) != 0, reader.GetInt32(16) != 0));
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
                    vmu_id TEXT PRIMARY KEY, friendly_name TEXT NOT NULL, device_name TEXT NULL UNIQUE, instance_id TEXT NULL UNIQUE,
                    width INTEGER NOT NULL,height INTEGER NOT NULL,refresh_rate INTEGER NOT NULL,portrait INTEGER NOT NULL DEFAULT 0,
                    remote_access TEXT NOT NULL DEFAULT 'Disabled',password_enabled INTEGER NOT NULL DEFAULT 0,password_hash TEXT NULL,
                    api_key_enabled INTEGER NOT NULL DEFAULT 0,api_key TEXT NULL UNIQUE,approval_enabled INTEGER NOT NULL DEFAULT 0,created_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        AddColumn(connection, "canonical_name", "TEXT NULL");
        AddColumn(connection, "title", "TEXT NULL");
        AddColumn(connection, "avatar_kind", "TEXT NULL");
        AddColumn(connection, "avatar_value", "TEXT NULL");
        AddColumn(connection, "security_mode", "TEXT NULL");
        AddColumn(connection, "collaboration_clipboard", "INTEGER NOT NULL DEFAULT 1");
        AddColumn(connection, "collaboration_mouse", "INTEGER NOT NULL DEFAULT 1");
        AddColumn(connection, "collaboration_keyboard", "INTEGER NOT NULL DEFAULT 1");

        var rows = new List<(string Id, string Friendly, bool Password, bool Api, bool Approval)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT vmu_id,friendly_name,password_enabled,api_key_enabled,approval_enabled FROM monitors WHERE canonical_name IS NULL OR title IS NULL OR avatar_value IS NULL OR security_mode IS NULL";
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0, reader.GetInt32(3) != 0, reader.GetInt32(4) != 0));
        }
        foreach (var row in rows)
        {
            var baseName = Slugify(row.Friendly);
            if (baseName.Length == 0) baseName = "virtual-monitor";
            var name = baseName; var suffix = 2;
            while (NameExistsForMigration(connection, name, row.Id)) name = $"{baseName}-{suffix++}";
            var security = row.Approval ? RemoteSecurityMode.Approval : row.Api ? RemoteSecurityMode.ApiKey : row.Password ? RemoteSecurityMode.Password : RemoteSecurityMode.Public;
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE monitors SET canonical_name=COALESCE(canonical_name,$name),title=COALESCE(title,friendly_name),avatar_kind=COALESCE(avatar_kind,'animal'),avatar_value=COALESCE(avatar_value,$avatar),security_mode=COALESCE(security_mode,$security) WHERE vmu_id=$id";
            update.Parameters.AddWithValue("$id", row.Id);
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$avatar", MonitorAvatarService.RandomAnimal());
            update.Parameters.AddWithValue("$security", security.ToString());
            update.ExecuteNonQuery();
        }

        using var table = connection.CreateCommand();
        table.CommandText = """
            CREATE TABLE IF NOT EXISTS monitor_access_rules(
                id INTEGER PRIMARY KEY AUTOINCREMENT,vmu_id TEXT NOT NULL,client_id TEXT NOT NULL,ip_address TEXT NULL,mac_address TEXT NULL,computer_name TEXT NULL,user_name TEXT NULL,
                permission TEXT NOT NULL DEFAULT 'Deferred',last_seen_utc TEXT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,
                FOREIGN KEY(vmu_id) REFERENCES monitors(vmu_id) ON DELETE CASCADE, UNIQUE(vmu_id,client_id));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_monitors_api_key ON monitors(api_key) WHERE api_key IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_monitors_instance_id ON monitors(instance_id) WHERE instance_id IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_monitors_canonical_name ON monitors(canonical_name);
            """;
        table.ExecuteNonQuery();
    }

    private static bool NameExistsForMigration(SqliteConnection connection, string name, string exceptId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM monitors WHERE canonical_name=$name AND vmu_id<>$id LIMIT 1";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", exceptId);
        return command.ExecuteScalar() is not null;
    }

    private static void AddColumn(SqliteConnection connection, string name, string definition)
    {
        if (HasColumn(connection, "monitors", name)) return;
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE monitors ADD COLUMN {name} {definition}";
        command.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private const string SelectColumns = "SELECT vmu_id,canonical_name,title,device_name,instance_id,width,height,refresh_rate,portrait,avatar_kind,avatar_value,remote_access,security_mode,api_key,collaboration_clipboard,collaboration_mouse,collaboration_keyboard";
}
