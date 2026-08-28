using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Produces an optically enlarged notification-area icon from the executable icon.
/// Windows reserves a square tray slot, while the VMU artwork is naturally wide;
/// cropping transparent pixels prevents the artwork from appearing unnecessarily small.
/// </summary>
internal static class TrayIconFactory
{
    public static Icon Create(string executablePath)
    {
        using var sourceIcon = Icon.ExtractAssociatedIcon(executablePath) ?? (Icon)SystemIcons.Application.Clone();
        using var source = sourceIcon.ToBitmap();
        var bounds = FindVisibleBounds(source);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return (Icon)sourceIcon.Clone();
        }

        using var target = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

            const int margin = 1;
            var available = 32 - (margin * 2);
            var scale = Math.Min(available / (double)bounds.Width, available / (double)bounds.Height);
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            var destination = new Rectangle((32 - width) / 2, (32 - height) / 2, width, height);
            graphics.DrawImage(source, destination, bounds, GraphicsUnit.Pixel);
        }

        var handle = target.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Rectangle FindVisibleBounds(Bitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A < 24) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left || bottom < top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
