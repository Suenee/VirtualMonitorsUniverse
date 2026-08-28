using System.Diagnostics;

namespace VirtualMonitorsUniverse.Server;

internal sealed class AboutForm : Form
{
    public AboutForm(Icon icon)
    {
        Text = $"About {ProjectInfo.ProductName}";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));

        var picture = new PictureBox { Image = icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(56, 56), Margin = new Padding(0, 2, 12, 0) };
        root.Controls.Add(picture, 0, 0);

        var info = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        var baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        info.Controls.Add(new Label { Text = ProjectInfo.ProductName, AutoSize = true, Font = new Font(baseFont, 12f, FontStyle.Bold) });
        info.Controls.Add(new Label { Text = $"Version {ProjectInfo.Version}", AutoSize = true, Margin = new Padding(0, 5, 0, 0) });
        info.Controls.Add(new Label { Text = ".NET 10 • Windows", AutoSize = true });
        info.Controls.Add(new Label { Text = "Virtual display, remote desktop and control platform.", AutoSize = true, MaximumSize = new Size(350, 0), Margin = new Padding(0, 10, 0, 0) });
        root.Controls.Add(info, 1, 0);

        var links = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
        links.Controls.Add(CreateLink("GitHub", ProjectInfo.RepositoryUrl));
        links.Controls.Add(CreateLink("Documentation", ProjectInfo.DocumentationUrl));
        links.Controls.Add(CreateLink("User Guide", ProjectInfo.GuideUrl));
        root.Controls.Add(links, 1, 1);

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right, Margin = new Padding(0, 16, 0, 0) };
        root.Controls.Add(close, 1, 2);
        AcceptButton = close;
        CancelButton = close;
        Controls.Add(root);
    }

    private static LinkLabel CreateLink(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 18, 0) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        return link;
    }
}
