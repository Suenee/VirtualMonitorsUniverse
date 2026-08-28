namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Hosts VMU Server application settings. The first implemented option mirrors the
/// SUB/VoicePrompterBridge traffic-log retention control.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly string _settingsPath;
    private readonly Action _saved;
    private readonly NumericUpDown _retention = new() { Minimum = 1, Maximum = 10080 };

    public SettingsForm(string settingsPath, Action saved, Icon icon)
    {
        _settingsPath = settingsPath;
        _saved = saved;
        var settings = ServerSettings.Load(settingsPath);

        Text = "Virtual Monitors Universe - Settings";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _retention.Value = Math.Clamp(settings.Logging.RetentionMinutes, (int)_retention.Minimum, (int)_retention.Maximum);

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245));

        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Log retention", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 3) }, 0, row);
        var retentionRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 3) };
        retentionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        retentionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _retention.Dock = DockStyle.Fill;
        retentionRow.Controls.Add(_retention, 0, 0);
        retentionRow.Controls.Add(new Label { Text = "min", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 0, 0) }, 1, 0);
        layout.Controls.Add(retentionRow, 1, row);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons, 1, row);

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    private void SaveSettings()
    {
        var settings = ServerSettings.Load(_settingsPath);
        settings.Logging.RetentionMinutes = (int)_retention.Value;
        settings.Save(_settingsPath);
        _saved();
        Close();
    }
}
