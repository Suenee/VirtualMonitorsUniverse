using System.Net.NetworkInformation;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Edits VMU service endpoints and operational log retention.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly string _settingsPath;
    private readonly Action<ServerSettings, ServerSettings> _saved;
    private readonly Func<int, bool> _isOwnedListener;
    private readonly ServerSettings _originalSettings;
    private readonly ComboBox _vmuInterface = CreateInterfaceBox();
    private readonly ComboBox _webInterface = CreateInterfaceBox();
    private readonly ComboBox _socketInterface = CreateInterfaceBox();
    private readonly NumericUpDown _vmuPort = CreatePortBox();
    private readonly NumericUpDown _webPort = CreatePortBox();
    private readonly NumericUpDown _socketPort = CreatePortBox();
    private readonly NumericUpDown _retentionDays = new() { Minimum = 1, Maximum = 3650, Width = 90 };
    private readonly Dictionary<NumericUpDown, Panel> _portBorders = new();
    private readonly ToolTip _tips = new();

    public SettingsForm(string settingsPath, Action<ServerSettings, ServerSettings> saved, Func<int, bool> isOwnedListener, Icon icon)
    {
        _settingsPath = settingsPath;
        _saved = saved;
        _isOwnedListener = isOwnedListener;
        _originalSettings = ServerSettings.Load(settingsPath);

        Text = "Virtual Monitors Universe - Settings";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        SetInterface(_vmuInterface, _originalSettings.Vmu.Interface);
        SetInterface(_webInterface, _originalSettings.Web.Interface);
        SetInterface(_socketInterface, _originalSettings.Socket.Interface);
        _vmuPort.Value = _originalSettings.Vmu.Port;
        _webPort.Value = _originalSettings.Web.Port;
        _socketPort.Value = _originalSettings.Socket.Port;
        _retentionDays.Value = Math.Clamp((int)Math.Ceiling(_originalSettings.Logging.RetentionMinutes / 1440d), (int)_retentionDays.Minimum, (int)_retentionDays.Maximum);

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), ColumnCount = 3 };
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
        _retentionDays.Margin = new Padding(0, 3, 8, 3);
        layout.Controls.Add(_retentionDays, 1, retentionRow);
        layout.Controls.Add(new Label { Text = "days", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 3) }, 2, retentionRow);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
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

        foreach (var port in new[] { _vmuPort, _webPort, _socketPort }) port.ValueChanged += (_, _) => ClearPortError(port);
        foreach (var interfaceBox in new[] { _vmuInterface, _webInterface, _socketInterface }) interfaceBox.SelectedIndexChanged += (_, _) => ClearAllPortErrors();

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    private static ComboBox CreateInterfaceBox()
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
        box.Items.AddRange(["localhost", "any"]);
        return box;
    }

    private static NumericUpDown CreatePortBox() => new() { Minimum = 1, Maximum = 65535, Width = 96, BorderStyle = BorderStyle.None };
    private static Label CreateLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 12, 3) };

    private static void AddHeader(TableLayoutPanel layout, string service, string iface, string port)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var (text, column) in new[] { (service, 0), (iface, 1), (port, 2) })
            layout.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font(baseFont, FontStyle.Bold), Margin = new Padding(0, 0, 8, 6) }, column, row);
    }

    private void AddServiceRow(TableLayoutPanel layout, string name, ComboBox interfaceBox, NumericUpDown portBox)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        layout.Controls.Add(CreateLabel(name), 0, row);
        interfaceBox.Margin = new Padding(0, 3, 8, 3);
        var border = new Panel { Size = new Size(98, 24), Padding = new Padding(1), Margin = new Padding(0, 3, 0, 3), BackColor = SystemColors.WindowFrame };
        portBox.Location = new Point(1, 1);
        portBox.Size = new Size(96, 22);
        portBox.Margin = Padding.Empty;
        border.Controls.Add(portBox);
        _portBorders[portBox] = border;
        layout.Controls.Add(interfaceBox, 1, row);
        layout.Controls.Add(border, 2, row);
    }

    private static void SetInterface(ComboBox box, string value) => box.SelectedItem = value.Equals("any", StringComparison.OrdinalIgnoreCase) ? "any" : "localhost";
    private static string GetInterface(ComboBox box) => Convert.ToString(box.SelectedItem) ?? "localhost";

    private void SaveSettings()
    {
        ClearAllPortErrors();
        var endpoints = new[]
        {
            new EndpointInput("VMU Server", _vmuPort),
            new EndpointInput("Web Server", _webPort),
            new EndpointInput("Web Socket", _socketPort),
        };
        var valid = true;

        foreach (var duplicateGroup in endpoints.GroupBy(x => x.Port).Where(x => x.Count() > 1))
        {
            foreach (var endpoint in duplicateGroup) SetPortError(endpoint.PortBox, $"Port {endpoint.Port} is also configured for another VMU service.");
            valid = false;
        }

        var activePorts = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(x => x.Port).ToHashSet();
        foreach (var endpoint in endpoints)
        {
            if (activePorts.Contains(endpoint.Port) && !_isOwnedListener(endpoint.Port))
            {
                SetPortError(endpoint.PortBox, $"Port {endpoint.Port} is already used by a TCP listener on this computer.");
                valid = false;
            }
        }

        if (!valid)
        {
            MessageBox.Show("One or more service ports are already in use or conflict with another VMU service. Correct the red fields and save again.", "VMU Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var settings = ServerSettings.Load(_settingsPath);
        settings.Vmu.Interface = GetInterface(_vmuInterface);
        settings.Vmu.Port = (int)_vmuPort.Value;
        settings.Web.Interface = GetInterface(_webInterface);
        settings.Web.Port = (int)_webPort.Value;
        settings.Socket.Interface = GetInterface(_socketInterface);
        settings.Socket.Port = (int)_socketPort.Value;
        settings.Logging.RetentionMinutes = checked((int)_retentionDays.Value * 1440);
        settings.Save(_settingsPath);
        _saved(_originalSettings, settings);
        Close();
    }

    private void SetPortError(NumericUpDown port, string message)
    {
        if (_portBorders.TryGetValue(port, out var border)) border.BackColor = Color.Firebrick;
        _tips.SetToolTip(port, message);
        if (_portBorders.TryGetValue(port, out var panel)) _tips.SetToolTip(panel, message);
    }

    private void ClearPortError(NumericUpDown port)
    {
        if (_portBorders.TryGetValue(port, out var border)) border.BackColor = SystemColors.WindowFrame;
        _tips.SetToolTip(port, string.Empty);
        if (_portBorders.TryGetValue(port, out var panel)) _tips.SetToolTip(panel, string.Empty);
    }

    private void ClearAllPortErrors() { foreach (var port in _portBorders.Keys) ClearPortError(port); }
    private sealed record EndpointInput(string Name, NumericUpDown PortBox) { public int Port => (int)PortBox.Value; }
}
