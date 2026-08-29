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

        var bitmap = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var font = new Font("Segoe UI Emoji", 16f, FontStyle.Regular, GraphicsUnit.Pixel);
        var glyph = GetEmoji(monitor.AvatarKind, monitor.AvatarValue);

        // Prefer the native WinForms text path because it can preserve color emoji.
        TextRenderer.DrawText(
            graphics,
            glyph,
            font,
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Color.Black,
            Color.Transparent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        // Some Windows/graphics combinations produce a completely transparent
        // bitmap for color emoji. In that case use GDI+ as a deterministic visible
        // fallback. It can be monochrome, but the selected avatar remains legible.
        if (!HasVisiblePixels(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoClip
            };
            using var brush = new SolidBrush(Color.Black);
            graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, bitmap.Width, bitmap.Height), format);
        }

        return bitmap;
    }

    private static bool HasVisiblePixels(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A > 8) return true;
            }
        }
        return false;
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
