using System.Text.Json;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Shows a reusable detail view for the currently selected operational log entry.
/// </summary>
internal sealed class LogDetailForm : Form
{
    private readonly Label _time = new() { AutoSize = true };
    private readonly Label _id = new() { AutoSize = true };
    private readonly Label _level = new() { AutoSize = true };
    private readonly Label _service = new() { AutoSize = true };
    private readonly Label _monitor = new() { AutoSize = true };
    private readonly Label _event = new() { AutoSize = true };
    private readonly TextBox _message = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly RichTextBox _details = new() { ReadOnly = true, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };

    public LogDetailForm(Icon icon)
    {
        Text = "Log detail";
        Icon = icon;
        StartPosition = FormStartPosition.CenterParent;
        Width = 820;
        Height = 620;
        MinimumSize = new Size(620, 440);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 3 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddField(header, 0, "Time", _time, "#", _id);
        AddField(header, 1, "Level", _level, "Service", _service);
        AddField(header, 2, "Monitor", _monitor, "Event", _event);
        root.Controls.Add(header, 0, 0);

        var messageGroup = new GroupBox { Text = "Message", Dock = DockStyle.Fill, Padding = new Padding(8) };
        messageGroup.Controls.Add(_message);
        root.Controls.Add(messageGroup, 0, 1);

        var detailsGroup = new GroupBox { Text = "Details", Dock = DockStyle.Fill, Padding = new Padding(8) };
        detailsGroup.Controls.Add(_details);
        root.Controls.Add(detailsGroup, 0, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);
    }

    public void ShowEntry(LogEntry entry)
    {
        _time.Text = entry.Timestamp.ToString("dd.MM.yyyy HH:mm:ss.fff");
        _id.Text = entry.Id.ToString();
        _level.Text = entry.Level;
        _service.Text = entry.Service;
        _monitor.Text = string.IsNullOrWhiteSpace(entry.MonitorId) ? "-" : entry.MonitorId;
        _event.Text = entry.Event;
        _message.Text = entry.Message;
        _details.Text = FormatDetails(entry.DetailsJson);
    }

    private static void AddField(TableLayoutPanel layout, int row, string leftName, Label leftValue, string rightName, Label rightValue)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateName(leftName), 0, row);
        layout.Controls.Add(leftValue, 1, row);
        layout.Controls.Add(CreateName(rightName), 2, row);
        layout.Controls.Add(rightValue, 3, row);
    }

    private static Label CreateName(string text)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        return new Label
        {
            Text = text + ":",
            AutoSize = true,
            Font = new Font(baseFont, FontStyle.Bold),
            Margin = new Padding(0, 2, 8, 2),
        };
    }

    private static string FormatDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(no structured details)";
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
