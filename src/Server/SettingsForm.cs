namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Edits VMU service endpoints and operational log retention.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly string _settingsPath;
    private readonly Action _saved;
    private readonly ComboBox _vmuInterface = CreateInterfaceBox();
    private readonly ComboBox _webInterface = CreateInterfaceBox();
    private readonly ComboBox _socketInterface = CreateInterfaceBox();
    private readonly NumericUpDown _vmuPort = CreatePortBox();
    private readonly NumericUpDown _webPort = CreatePortBox();
    private readonly NumericUpDown _socketPort = CreatePortBox();
    private readonly NumericUpDown _retentionDays = new() { Minimum = 1, Maximum = 3650 };

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

        SetInterface(_vmuInterface, settings.Vmu.Interface);
        SetInterface(_webInterface, settings.Web.Interface);
        SetInterface(_socketInterface, settings.Socket.Interface);
        _vmuPort.Value = settings.Vmu.Port;
        _webPort.Value = settings.Web.Port;
        _socketPort.Value = settings.Socket.Port;
        _retentionDays.Value = Math.Clamp((int)Math.Ceiling(settings.Logging.RetentionMinutes / 1440d), (int)_retentionDays.Minimum, (int)_retentionDays.Maximum);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            ColumnCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        AddHeader(layout, "Service", "Interface", "Port");
        AddServiceRow(layout, "VMU Server", _vmuInterface, _vmuPort);
        AddServiceRow(layout, "Web Server", _webInterface, _webPort);
        AddServiceRow(layout, "Web Socket", _socketInterface, _socketPort);

        var retentionRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateLabel("Log retention"), 0, retentionRow);
        _retentionDays.Dock = DockStyle.Fill;
        _retentionDays.Margin = new Padding(0, 6, 0, 3);
        layout.Controls.Add(_retentionDays, 1, retentionRow);
        layout.Controls.Add(new Label { Text = "days", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 6, 0, 3) }, 2, retentionRow);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0),
        };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        var buttonRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons, 1, buttonRow);
        layout.SetColumnSpan(buttons, 2);

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    private static ComboBox CreateInterfaceBox()
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        box.Items.AddRange(["localhost", "any"]);
        return box;
    }

    private static NumericUpDown CreatePortBox() => new() { Minimum = 1, Maximum = 65535, Dock = DockStyle.Fill };

    private static Label CreateLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 3) };

    private static void AddHeader(TableLayoutPanel layout, string service, string iface, string port)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var (text, column) in new[] { (service, 0), (iface, 1), (port, 2) })
        {
            layout.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(0, 0, 8, 6) }, column, row);
        }
    }

    private static void AddServiceRow(TableLayoutPanel layout, string name, ComboBox interfaceBox, NumericUpDown portBox)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateLabel(name), 0, row);
        interfaceBox.Margin = new Padding(0, 3, 8, 3);
        portBox.Margin = new Padding(0, 3, 0, 3);
        layout.Controls.Add(interfaceBox, 1, row);
        layout.Controls.Add(portBox, 2, row);
    }

    private static void SetInterface(ComboBox box, string value)
    {
        box.SelectedItem = value.Equals("any", StringComparison.OrdinalIgnoreCase) ? "any" : "localhost";
    }

    private void SaveSettings()
    {
        var vmuPort = (int)_vmuPort.Value;
        var webPort = (int)_webPort.Value;
        var socketPort = (int)_socketPort.Value;
        if (new[] { vmuPort, webPort, socketPort }.Distinct().Count() != 3)
        {
            MessageBox.Show("VMU Server, Web Server and Web Socket must use different ports.", "VMU Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var settings = ServerSettings.Load(_settingsPath);
        settings.Vmu.Interface = Convert.ToString(_vmuInterface.SelectedItem) ?? "localhost";
        settings.Vmu.Port = vmuPort;
        settings.Web.Interface = Convert.ToString(_webInterface.SelectedItem) ?? "localhost";
        settings.Web.Port = webPort;
        settings.Socket.Interface = Convert.ToString(_socketInterface.SelectedItem) ?? "localhost";
        settings.Socket.Port = socketPort;
        settings.Logging.RetentionMinutes = checked((int)_retentionDays.Value * 1440);
        settings.Save(_settingsPath);
        _saved();
        Close();
    }
}
