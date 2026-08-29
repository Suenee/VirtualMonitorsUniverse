using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace VirtualMonitorsUniverse.Server;

internal static class MonitorAvatarService
{
    private static readonly string[] Animals = ["fox", "owl", "panda", "cat", "dog", "rabbit", "bear", "koala", "tiger", "lion", "penguin", "frog", "mouse", "cow", "pig", "monkey"];
    private static readonly Dictionary<string, string> Emoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fox"] = "🦊", ["owl"] = "🦉", ["panda"] = "🐼", ["cat"] = "🐱", ["dog"] = "🐶", ["rabbit"] = "🐰",
        ["bear"] = "🐻", ["koala"] = "🐨", ["tiger"] = "🐯", ["lion"] = "🦁", ["penguin"] = "🐧", ["frog"] = "🐸",
        ["mouse"] = "🐭", ["cow"] = "🐮", ["pig"] = "🐷", ["monkey"] = "🐵",
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
                try { using var source = Image.FromFile(path); return new Bitmap(source, new Size(20, 20)); }
                catch { }
            }
        }

        // GDI+ DrawString flattens Segoe UI Emoji to monochrome glyphs on many
        // Windows builds. WinForms TextRenderer follows the native Windows text
        // path and preserves the system's color emoji rendering where available.
        var bitmap = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var font = new Font("Segoe UI Emoji", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(
            graphics,
            GetEmoji(monitor.AvatarKind, monitor.AvatarValue),
            font,
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Color.Black,
            Color.Transparent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return bitmap;
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
}
