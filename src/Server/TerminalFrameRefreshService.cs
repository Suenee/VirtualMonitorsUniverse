using System.Drawing;
using System.Drawing.Imaging;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Produces a one-shot corrective Terminal frame after a cursor display-boundary
/// transition. GDI desktop copy is intentionally used only for this exceptional
/// refresh path: it snapshots the current desktop bitmap without carrying a stale
/// hardware-pointer image from a Desktop Duplication stream.
/// </summary>
internal static class TerminalFrameRefreshService
{
    public static Task<byte[]> CaptureAsync(string deviceName, CancellationToken cancellationToken)
    {
        return Task.Run(() => Capture(deviceName), cancellationToken);
    }

    private static byte[] Capture(string deviceName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Terminal refresh capture requires Windows.");

        var display = WindowsArrangementService.GetActive()
            .FirstOrDefault(x => x.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Windows display '{deviceName}' is not active.");

        if (display.Width <= 0 || display.Height <= 0)
            throw new InvalidOperationException($"Windows display '{deviceName}' has invalid desktop bounds.");

        using var source = new Bitmap(display.Width, display.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(
                display.X,
                display.Y,
                0,
                0,
                new Size(display.Width, display.Height),
                CopyPixelOperation.SourceCopy);
        }

        using var scaled = ScaleToMaximumWidth(source, 1920);
        return EncodeJpeg(scaled ?? source, 68L);
    }

    private static Bitmap? ScaleToMaximumWidth(Bitmap source, int maximumWidth)
    {
        if (source.Width <= maximumWidth) return null;

        var height = Math.Max(1, checked((int)Math.Round(source.Height * (maximumWidth / (double)source.Width))));
        var result = new Bitmap(maximumWidth, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
        graphics.DrawImage(source, 0, 0, result.Width, result.Height);
        return result;
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x => x.FormatID == ImageFormat.Jpeg.Guid)
            ?? throw new InvalidOperationException("Windows JPEG encoder is unavailable.");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }
}
