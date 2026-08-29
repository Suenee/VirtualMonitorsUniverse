using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace VirtualMonitorsUniverse.Server;

internal static class MonitorAvatarService
{
    private static readonly string[] Animals = ["fox", "owl", "panda", "cat", "dog", "rabbit", "bear", "koala", "tiger", "lion", "penguin", "frog"];
    private static readonly Dictionary<string, string> Emoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fox"] = "🦊", ["owl"] = "🦉", ["panda"] = "🐼", ["cat"] = "🐱", ["dog"] = "🐶", ["rabbit"] = "🐰",
        ["bear"] = "🐻", ["koala"] = "🐨", ["tiger"] = "🐯", ["lion"] = "🦁", ["penguin"] = "🐧", ["frog"] = "🐸",
    };

    public static string RandomAnimal() => Animals[RandomNumberGenerator.GetInt32(Animals.Length)];

    public static IReadOnlyList<string> AnimalNames => Animals;

    public static string GetEmoji(string? avatarKind, string? avatarValue)
        => avatarKind?.Equals("animal", StringComparison.OrdinalIgnoreCase) == true && avatarValue is not null && Emoji.TryGetValue(avatarValue, out var emoji) ? emoji : "🖥️";

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
                catch { }
            }
        }

        var bitmap = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var font = new Font("Segoe UI Emoji", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString(GetEmoji(monitor.AvatarKind, monitor.AvatarValue), font, Brushes.Black, new PointF(1, 1));
        return bitmap;
    }

    public static string SaveCustom(string dataRoot, string vmuId, string fileName, Stream content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".png" or ".ico" or ".gif"))
            throw new InvalidOperationException("Avatar must be a PNG, ICO or GIF file.");

        var directory = Path.Combine(dataRoot, "avatars");
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, vmuId + ".*"))
        {
            try { File.Delete(old); } catch { }
        }

        var storedName = vmuId + extension;
        using var output = File.Create(Path.Combine(directory, storedName));
        content.CopyTo(output);
        return storedName;
    }

    public static byte[]? ReadCustom(string dataRoot, string? avatarValue)
    {
        if (string.IsNullOrWhiteSpace(avatarValue)) return null;
        var path = Path.Combine(dataRoot, "avatars", avatarValue);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
