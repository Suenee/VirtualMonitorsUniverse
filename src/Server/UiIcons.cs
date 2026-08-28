using System.Drawing.Drawing2D;

namespace VirtualMonitorsUniverse.Server;

internal enum UiIconKind
{
    Start,
    Stop,
    Restart,
    Exit,
    Settings,
    Log,
    Running,
    Stopped,
    Server,
    Web,
    Socket,
    Monitors,
    Open,
    About,
}

internal static class UiIcons
{
    public static Bitmap Create(UiIconKind kind, int size = 20)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        var scale = size / 20f;

        switch (kind)
        {
            case UiIconKind.Start:
                using (var brush = new SolidBrush(Color.FromArgb(40, 170, 60)))
                    graphics.FillPolygon(brush, [Point(5, 3, scale), Point(17, 10, scale), Point(5, 17, scale)]);
                break;
            case UiIconKind.Stop:
                using (var brush = new SolidBrush(Color.FromArgb(220, 45, 45)))
                    graphics.FillRectangle(brush, 4 * scale, 4 * scale, 12 * scale, 12 * scale);
                break;
            case UiIconKind.Restart:
                using (var pen = new Pen(Color.FromArgb(35, 120, 210), Math.Max(1.5f, 2.3f * scale)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    graphics.DrawArc(pen, 3 * scale, 3 * scale, 14 * scale, 14 * scale, 45, 285);
                }
                using (var brush = new SolidBrush(Color.FromArgb(35, 120, 210)))
                    graphics.FillPolygon(brush, [Point(15, 2, scale), Point(18, 7, scale), Point(12, 7, scale)]);
                break;
            case UiIconKind.Exit:
                DrawExit(graphics, scale);
                break;
            case UiIconKind.Settings:
                DrawSettings(graphics, scale);
                break;
            case UiIconKind.Log:
                DrawLog(graphics, scale);
                break;
            case UiIconKind.Running:
                DrawDot(graphics, scale, Color.FromArgb(35, 170, 70));
                break;
            case UiIconKind.Stopped:
                DrawDot(graphics, scale, Color.FromArgb(215, 55, 55));
                break;
            case UiIconKind.Server:
                DrawBox(graphics, scale, Color.FromArgb(40, 120, 210), "V");
                break;
            case UiIconKind.Web:
                DrawBox(graphics, scale, Color.FromArgb(35, 160, 90), "W");
                break;
            case UiIconKind.Socket:
                DrawBox(graphics, scale, Color.FromArgb(130, 80, 190), "S");
                break;
            case UiIconKind.Monitors:
                using (var pen = new Pen(Color.Teal, Math.Max(1.2f, 1.6f * scale)))
                {
                    graphics.DrawRectangle(pen, 3 * scale, 4 * scale, 14 * scale, 10 * scale);
                    graphics.DrawLine(pen, 8 * scale, 17 * scale, 12 * scale, 17 * scale);
                    graphics.DrawLine(pen, 10 * scale, 14 * scale, 10 * scale, 17 * scale);
                }
                break;
            case UiIconKind.Open:
                using (var pen = new Pen(Color.RoyalBlue, Math.Max(1.2f, 1.7f * scale)))
                {
                    graphics.DrawRectangle(pen, 3 * scale, 6 * scale, 10 * scale, 10 * scale);
                    graphics.DrawLine(pen, 9 * scale, 4 * scale, 17 * scale, 4 * scale);
                    graphics.DrawLine(pen, 17 * scale, 4 * scale, 17 * scale, 12 * scale);
                    graphics.DrawLine(pen, 17 * scale, 4 * scale, 9 * scale, 12 * scale);
                }
                break;
            case UiIconKind.About:
                using (var brush = new SolidBrush(Color.RoyalBlue)) graphics.FillEllipse(brush, 3 * scale, 3 * scale, 14 * scale, 14 * scale);
                using (var font = new Font("Segoe UI", 11f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White)) graphics.DrawString("i", font, brush, 8 * scale, 3 * scale);
                break;
        }

        return bitmap;
    }

    private static void DrawDot(Graphics graphics, float scale, Color color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 3 * scale, 3 * scale, 14 * scale, 14 * scale);
    }

    private static void DrawBox(Graphics graphics, float scale, Color color, string text)
    {
        using var brush = new SolidBrush(color);
        graphics.FillRoundedRectangle(brush, new RectangleF(2 * scale, 2 * scale, 16 * scale, 16 * scale), 3 * scale);
        using var font = new Font("Segoe UI", 10f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, textBrush, 10 * scale - size.Width / 2, 10 * scale - size.Height / 2);
    }

    private static void DrawSettings(Graphics graphics, float scale)
    {
        using var pen = new Pen(Color.FromArgb(85, 85, 85), Math.Max(1.2f, 1.8f * scale));
        graphics.DrawEllipse(pen, 6 * scale, 6 * scale, 8 * scale, 8 * scale);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            graphics.DrawLine(pen, 10 * scale + (float)Math.Cos(angle) * 6 * scale, 10 * scale + (float)Math.Sin(angle) * 6 * scale, 10 * scale + (float)Math.Cos(angle) * 8 * scale, 10 * scale + (float)Math.Sin(angle) * 8 * scale);
        }
    }

    private static void DrawLog(Graphics graphics, float scale)
    {
        using var pen = new Pen(Color.FromArgb(70, 70, 70), Math.Max(1.2f, 1.6f * scale));
        graphics.DrawRectangle(pen, 4 * scale, 3 * scale, 12 * scale, 14 * scale);
        graphics.DrawLine(pen, 7 * scale, 7 * scale, 14 * scale, 7 * scale);
        graphics.DrawLine(pen, 7 * scale, 10 * scale, 14 * scale, 10 * scale);
        graphics.DrawLine(pen, 7 * scale, 13 * scale, 12 * scale, 13 * scale);
    }

    private static void DrawExit(Graphics graphics, float scale)
    {
        using var pen = new Pen(Color.FromArgb(200, 45, 45), Math.Max(1.5f, 2.1f * scale));
        graphics.DrawRectangle(pen, 3 * scale, 4 * scale, 8 * scale, 12 * scale);
        graphics.DrawLine(pen, 9 * scale, 10 * scale, 17 * scale, 10 * scale);
        using var brush = new SolidBrush(Color.FromArgb(200, 45, 45));
        graphics.FillPolygon(brush, [Point(14, 6, scale), Point(18, 10, scale), Point(14, 14, scale)]);
    }

    private static PointF Point(float x, float y, float scale) => new(x * scale, y * scale);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
