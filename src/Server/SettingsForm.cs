using System.Net.NetworkInformation;

namespace VirtualMonitorsUniverse.Server;

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
    private readonly ComboBox _monitorExit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly CheckBox _restoreServices = new() { AutoSize = true };
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
        _retentionDays.Value = Math.Clamp((int)Math.Ceiling(_originalSettings.Logging.RetentionMinutes / 1440d), 1, 3650);
        _monitorExit.Items.AddRange(["Disconnect", "Keep", "Uninstall"]);
        _monitorExit.SelectedItem = _originalSettings.Exit.MonitorAction.ToString();
        _restoreServices.Checked = _originalSettings.Exit.RestoreServices;

        _tips.SetToolTip(_monitorExit, "Disconnect keeps virtual monitors installed, Keep leaves them unchanged, and Uninstall removes them when VMU exits.");
        _tips.SetToolTip(_restoreServices, "Remember running service states on a normal exit and restore them on the next VMU start. When disabled, services start stopped.");

        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        AddHeader(layout, "Service", "Interface", "Port");
        AddServiceRow(layout, "VMU Server", _vmuInterface, _vmuPort);
        AddServiceRow(layout, "Web Server", _webInterface, _webPort);
        AddServiceRow(layout, "Web Socket", _socketInterface, _socketPort);
        AddSettingRow(layout, "Log Retention", _retentionDays, new Label { Text = "days", AutoSize = true, Anchor = AnchorStyles.Left });

        var groupRow = layout.RowCount++;
        var exitGroup = new GroupBox { Text = "On Exit", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 10, 0, 2) };
        var exitLayout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill };
        exitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        exitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        exitLayout.Controls.Add(CreateExitLabel("Monitors"), 0, 0);
        _monitorExit.Margin = new Padding(0, 1, 0, 3);
        exitLayout.Controls.Add(_monitorExit, 1, 0);
        exitLayout.Controls.Add(CreateExitLabel("Restore Services"), 0, 1);
        _restoreServices.Margin = new Padding(0, 3, 0, 0);
        exitLayout.Controls.Add(_restoreServices, 1, 1);
        exitGroup.Controls.Add(exitLayout);
        layout.Controls.Add(exitGroup, 0, groupRow);
        layout.SetColumnSpan(exitGroup, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        var buttonRow = layout.RowCount++;
        layout.Controls.Add(buttons, 1, buttonRow);
        layout.SetColumnSpan(buttons, 2);

        foreach (var port in new[] { _vmuPort, _webPort, _socketPort })
            port.ValueChanged += (_, _) => ValidatePorts(showDialog: false);

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    private static ComboBox CreateInterfaceBox()
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, Margin = new Padding(0, 2, 8, 0) };
        box.Items.AddRange(["localhost", "any"]);
        return box;
    }

    private static NumericUpDown CreatePortBox() => new() { Minimum = 1, Maximum = 65535, Width = 96, Margin = Padding.Empty };
    private static Label CreateLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 12, 0) };
    private static Label CreateExitLabel(string text) => new() { Text = text, AutoSize = false, Width = 112, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left, Margin = Padding.Empty };

    private static void AddHeader(TableLayoutPanel layout, string service, string iface, string port)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        var row = layout.RowCount++;
        foreach (var (text, column) in new[] { (service, 0), (iface, 1), (port, 2) })
            layout.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font(baseFont, FontStyle.Bold), Margin = new Padding(0, 0, 8, 5) }, column, row);
    }

    private void AddServiceRow(TableLayoutPanel layout, string name, ComboBox interfaceBox, NumericUpDown portBox)
    {
        var row = layout.RowCount++;
        layout.Controls.Add(CreateLabel(name), 0, row);
        var border = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(1), Margin = Padding.Empty, BackColor = SystemColors.Control };
        portBox.Margin = Padding.Empty;
        border.Controls.Add(portBox);
        _portBorders[portBox] = border;
        layout.Controls.Add(interfaceBox, 1, row);
        layout.Controls.Add(border, 2, row);
    }

    private static void AddSettingRow(TableLayoutPanel layout, string label, Control control, Control? suffix)
    {
        var row = layout.RowCount++;
        layout.Controls.Add(CreateLabel(label), 0, row);
        control.Margin = new Padding(0, 3, 8, 0);
        layout.Controls.Add(control, 1, row);
        if (suffix is not null)
        {
            suffix.Margin = new Padding(0, 4, 0, 0);
            layout.Controls.Add(suffix, 2, row);
        }
    }

    private static void SetInterface(ComboBox box, string value) => box.SelectedItem = value.Equals("any", StringComparison.OrdinalIgnoreCase) ? "any" : "localhost";
    private static string GetInterface(ComboBox box) => Convert.ToString(box.SelectedItem) ?? "localhost";

    private void SaveSettings()
    {
        if (!ValidatePorts(showDialog: true)) return;

        var settings = ServerSettings.Load(_settingsPath);
        settings.Vmu.Interface = GetInterface(_vmuInterface);
        settings.Vmu.Port = (int)_vmuPort.Value;
        settings.Web.Interface = GetInterface(_webInterface);
        settings.Web.Port = (int)_webPort.Value;
        settings.Socket.Interface = GetInterface(_socketInterface);
        settings.Socket.Port = (int)_socketPort.Value;
        settings.Logging.RetentionMinutes = checked((int)_retentionDays.Value * 1440);
        settings.Exit.MonitorAction = Enum.TryParse<MonitorExitAction>(Convert.ToString(_monitorExit.SelectedItem), out var action) ? action : MonitorExitAction.Disconnect;
        settings.Exit.RestoreServices = _restoreServices.Checked;
        settings.Save(_settingsPath);
        _saved(_originalSettings, settings);
        Close();
    }

    private bool ValidatePorts(bool showDialog)
    {
        ClearAllPortErrors();
        var endpoints = new[]
        {
            new EndpointInput("VMU Server", _vmuPort),
            new EndpointInput("Web Server", _webPort),
            new EndpointInput("Web Socket", _socketPort),
        };
        var errors = new List<string>();

        foreach (var group in endpoints.GroupBy(x => x.Port).Where(x => x.Count() > 1))
        {
            var names = string.Join(" and ", group.Select(x => x.Name));
            foreach (var endpoint in group)
                SetPortError(endpoint.PortBox, $"Port {endpoint.Port} is configured for both {names}.");
            errors.Add($"Port {group.Key} is configured for more than one VMU service.");
        }

        var activePorts = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(x => x.Port).ToHashSet();
        foreach (var endpoint in endpoints)
        {
            if (!activePorts.Contains(endpoint.Port) || _isOwnedListener(endpoint.Port)) continue;
            var message = $"{endpoint.Name} port {endpoint.Port} is already used by another TCP listener on this computer.";
            SetPortError(endpoint.PortBox, message);
            errors.Add(message);
        }

        if (showDialog && errors.Count > 0)
            MessageBox.Show(string.Join(Environment.NewLine, errors.Distinct()), "VMU Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        return errors.Count == 0;
    }

    private void SetPortError(NumericUpDown port, string message)
    {
        if (_portBorders.TryGetValue(port, out var border)) border.BackColor = Color.Firebrick;
        port.ForeColor = Color.Firebrick;
        _tips.SetToolTip(port, message);
    }

    private void ClearPortError(NumericUpDown port)
    {
        if (_portBorders.TryGetValue(port, out var border)) border.BackColor = SystemColors.Control;
        port.ForeColor = SystemColors.WindowText;
        _tips.SetToolTip(port, string.Empty);
    }

    private void ClearAllPortErrors()
    {
        foreach (var port in _portBorders.Keys) ClearPortError(port);
    }

    private sealed record EndpointInput(string Name, NumericUpDown PortBox)
    {
        public int Port => (int)PortBox.Value;
    }
}
