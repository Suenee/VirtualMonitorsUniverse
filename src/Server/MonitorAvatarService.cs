using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Provides VMU monitor avatars. Built-in avatars are discovered from the
/// Assets/Avatars directory, validated once, cached in memory and refreshed in
/// the background when files change. The PNG file itself is the authoritative
/// built-in representation: VMU does not map avatar IDs through system emoji.
/// Cosmetic avatar work must never block capture/input paths or make the UI
/// dependent on a malformed image file.
/// </summary>
internal static partial class MonitorAvatarService
{
    public const int BuiltInWidth = 256;
    public const int BuiltInHeight = 256;
    public const long BuiltInMaxFileBytes = 256 * 1024;

    private static readonly object CatalogSync = new();
    private static readonly string BuiltInDirectory = ResolveBuiltInDirectory();
    private static AvatarCatalog? _catalog;
    private static FileSystemWatcher? _watcher;
    private static int _reloadScheduled;
    private static long _revision;

    public static IReadOnlyList<string> AnimalNames => GetCatalog().Ids;
    public static long Revision => GetCatalog().Revision;

    public static void WarmCache() => _ = GetCatalog();

    public static string RandomAnimal()
    {
        var ids = GetCatalog().Ids;
        return ids.Count == 0 ? "monitor" : ids[RandomNumberGenerator.GetInt32(ids.Count)];
    }

    public static bool BuiltInExists(string? id) => id is not null && GetCatalog().Images.ContainsKey(id);

    public static byte[]? ReadBuiltIn(string? id)
    {
        if (id is null) return null;
        return GetCatalog().Images.TryGetValue(id, out var bytes) ? bytes : null;
    }

    // Kept under the historical method name so older renderer call sites remain
    // binary/source compatible. It now emits the authoritative PNG directly.
    public static string GetEmoji(string? avatarKind, string? avatarValue)
    {
        if (avatarKind?.Equals("animal", StringComparison.OrdinalIgnoreCase) != true ||
            string.IsNullOrWhiteSpace(avatarValue) || !BuiltInExists(avatarValue))
            return "<span class=\"avatarFallback\" aria-hidden=\"true\">▣</span>";

        var id = Uri.EscapeDataString(avatarValue);
        return $"<img class=\"builtInAvatar\" src=\"/api/avatars/{id}?v={Revision}\" alt=\"\">";
    }

    public static Image CreateTrayImage(MonitorRecord monitor, string dataRoot)
    {
        if (monitor.AvatarKind.Equals("custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(monitor.AvatarValue))
        {
            var path = Path.Combine(dataRoot, "avatars", monitor.AvatarValue);
            if (File.Exists(path))
            {
                try
                {
                    using var source = Image.FromFile(path);
                    return new Bitmap(source, new Size(20, 20));
                }
                catch
                {
                    // A broken custom avatar must never break the Tray menu.
                }
            }
        }
        else if (monitor.AvatarKind.Equals("animal", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = ReadBuiltIn(monitor.AvatarValue);
            if (bytes is not null)
            {
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
                    return new Bitmap(source, new Size(20, 20));
                }
                catch
                {
                    // The catalog already validates images; keep a defensive fallback.
                }
            }
        }

        return CreateFallbackTrayImage();
    }

    public static string SaveCustom(string dataRoot, string vmuId, string fileName, Stream content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".png" or ".ico" or ".gif")) throw new InvalidOperationException("Avatar must be a PNG, ICO or GIF file.");
        var directory = Path.Combine(dataRoot, "avatars");
        Directory.CreateDirectory(directory);
        DeleteCustom(dataRoot, vmuId);
        var storedName = vmuId + extension;
        using var output = File.Create(Path.Combine(directory, storedName));
        content.CopyTo(output);
        return storedName;
    }

    public static void DeleteCustom(string dataRoot, string vmuId)
    {
        var directory = Path.Combine(dataRoot, "avatars");
        if (!Directory.Exists(directory)) return;
        foreach (var old in Directory.EnumerateFiles(directory, vmuId + ".*"))
            try { File.Delete(old); } catch { }
    }

    public static byte[]? ReadCustom(string dataRoot, string? avatarValue)
    {
        if (string.IsNullOrWhiteSpace(avatarValue)) return null;
        var path = Path.Combine(dataRoot, "avatars", avatarValue);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static AvatarCatalog GetCatalog()
    {
        lock (CatalogSync)
        {
            if (_catalog is not null) return _catalog;
            _catalog = LoadCatalog();
            EnsureWatcher();
            return _catalog;
        }
    }

    private static AvatarCatalog LoadCatalog()
    {
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(BuiltInDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(BuiltInDirectory, "*.png", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length is <= 0 or > BuiltInMaxFileBytes) continue;
                    var id = Path.GetFileNameWithoutExtension(path);
                    if (!AvatarIdRegex().IsMatch(id)) continue;

                    var bytes = File.ReadAllBytes(path);
                    if (!ValidateBuiltInPng(bytes)) continue;
                    images[id] = bytes;
                }
                catch
                {
                    // Invalid, locked or disappearing files are simply excluded.
                }
            }
        }

        var revision = Interlocked.Increment(ref _revision);
        return new AvatarCatalog(images.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), images, revision);
    }

    private static bool ValidateBuiltInPng(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (source.RawFormat.Guid != ImageFormat.Png.Guid || source.Width != BuiltInWidth || source.Height != BuiltInHeight) return false;

            using var bitmap = new Bitmap(source);
            for (var y = 0; y < bitmap.Height; y += 4)
            {
                for (var x = 0; x < bitmap.Width; x += 4)
                {
                    if (bitmap.GetPixel(x, y).A < 255) return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureWatcher()
    {
        if (_watcher is not null || !Directory.Exists(BuiltInDirectory)) return;
        try
        {
            _watcher = new FileSystemWatcher(BuiltInDirectory, "*.png")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Deleted += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
        }
        catch
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private static void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180).ConfigureAwait(false);
                var replacement = LoadCatalog();
                lock (CatalogSync) _catalog = replacement;
            }
            catch
            {
                // Keep the last known-good catalog on any reload failure.
            }
            finally
            {
                Interlocked.Exchange(ref _reloadScheduled, 0);
            }
        });
    }

    private static string ResolveBuiltInDirectory()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            var sourceDirectory = Path.Combine(repoRoot, "src", "Server", "Assets", "Avatars");
            if (Directory.Exists(sourceDirectory)) return sourceDirectory;
        }
        return Path.Combine(AppContext.BaseDirectory, "Assets", "Avatars");
    }

    private static Image CreateFallbackTrayImage()
    {
        var bitmap = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var pen = new Pen(Color.DimGray, 2f);
        graphics.DrawRectangle(pen, 3, 4, 18, 13);
        graphics.DrawLine(pen, 9, 20, 15, 20);
        graphics.DrawLine(pen, 12, 17, 12, 20);
        return bitmap;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AvatarIdRegex();

    private sealed record AvatarCatalog(IReadOnlyList<string> Ids, IReadOnlyDictionary<string, byte[]> Images, long Revision);
}
