using System.Net.NetworkInformation;

namespace VirtualMonitorsUniverse.Server;

internal sealed class SettingsForm : Form
{
    private const string LocalhostLabel = "localhost";
    private const string AllInterfacesLabel = "All Interfaces";

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
    private readonly NumericUpDown _retentionDays = new() { Minimum = 1, Maximum = 3650, Width = 96 };
    private readonly NumericUpDown _arrangementSnapTolerance = new() { Minimum = 5, Maximum = 50, Width = 96 };
    private readonly ComboBox _monitorExit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly CheckBox _restoreServices = new() { AutoSize = true, Margin = Padding.Empty, Padding = Padding.Empty };
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
        _arrangementSnapTolerance.Value = Math.Clamp(_originalSettings.WebUi.ArrangementSnapTolerancePx, 5, 50);
        _monitorExit.Items.AddRange(["Disconnect", "Keep"]);
        _monitorExit.SelectedItem = _originalSettings.Exit.MonitorAction == MonitorExitAction.Keep ? "Keep" : "Disconnect";
        _restoreServices.Checked = _originalSettings.Exit.RestoreServices;

        _tips.SetToolTip(_monitorExit, "Disconnect turns off connected VMU monitors when the application exits. Keep leaves them unchanged.");
        _tips.SetToolTip(_restoreServices, "Remember running service states and restore them after a normal or maintenance restart.");
        _tips.SetToolTip(_arrangementSnapTolerance, "Distance in Windows desktop pixels at which monitor edges snap together in Arrangement.");

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), ColumnCount = 1 };
        var services = new GroupBox { Text = "Services", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10) };
        var serviceLayout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Dock = DockStyle.Fill };
        serviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        serviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        serviceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        AddHeader(serviceLayout, "Service", "Interface", "Port");
        AddServiceRow(serviceLayout, "VMU Server", _vmuInterface, _vmuPort);
        AddServiceRow(serviceLayout, "Web Server", _webInterface, _webPort);
        AddServiceRow(serviceLayout, "Web Socket", _socketInterface, _socketPort);
        services.Controls.Add(serviceLayout);
        root.Controls.Add(services);

        var general = new GroupBox { Text = "Web and Logging", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10), Margin = new Padding(0, 10, 0, 0) };
        var generalLayout = CreateTwoColumnLayout();
        AddTwoColumnRow(generalLayout, "Log Retention", WrapWithSuffix(_retentionDays, "days"));
        AddTwoColumnRow(generalLayout, "Arrangement Snap", WrapWithSuffix(_arrangementSnapTolerance, "px"));
        general.Controls.Add(generalLayout);
        root.Controls.Add(general);

        var exitGroup = new GroupBox { Text = "On Exit", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10), Margin = new Padding(0, 10, 0, 0) };
        var exitLayout = CreateTwoColumnLayout();
        AddTwoColumnRow(exitLayout, "Monitors", _monitorExit);
        AddTwoColumnRow(exitLayout, "Restore Services", CreateCheckboxHost(_restoreServices));
        exitGroup.Controls.Add(exitLayout);
        root.Controls.Add(exitGroup);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons);

        foreach (var port in new[] { _vmuPort, _webPort, _socketPort }) port.ValueChanged += (_, _) => ValidatePorts(false);
        _vmuInterface.SelectedIndexChanged += (_, _) => ApplyInterfaceDependency();
        _webInterface.SelectedIndexChanged += (_, _) => ApplyInterfaceDependency();
        ApplyInterfaceDependency();
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(root);
    }

    private static TableLayoutPanel CreateTwoColumnLayout()
    {
        var layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        return layout;
    }

    private static void AddTwoColumnRow(TableLayoutPanel layout, string text, Control control)
    {
        var row = layout.RowCount++;
        layout.Controls.Add(new Label { Text = text, AutoSize = false, Width = 132, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, Anchor = AnchorStyles.Left }, 0, row);
        control.Margin = new Padding(0, 2, 0, 4);
        control.Anchor = AnchorStyles.Left;
        layout.Controls.Add(control, 1, row);
    }

    private static Control WrapWithSuffix(Control control, string suffix)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        control.Margin = Padding.Empty;
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(8, 5, 0, 0) });
        return panel;
    }

    private static Control CreateCheckboxHost(CheckBox checkBox)
    {
        var panel = new Panel { Width = 145, Height = 26, Margin = Padding.Empty };
        checkBox.Location = new Point(0, 4);
        panel.Controls.Add(checkBox);
        return panel;
    }

    private static ComboBox CreateInterfaceBox()
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145, Margin = new Padding(0, 2, 8, 0) };
        box.Items.AddRange([LocalhostLabel, AllInterfacesLabel]);
        return box;
    }

    private static NumericUpDown CreatePortBox() => new() { Minimum = 1, Maximum = 65535, Width = 96, Margin = Padding.Empty };

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
        layout.Controls.Add(new Label { Text = name, AutoSize = false, Width = 112, Height = 27, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, row);
        var border = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(1), Margin = Padding.Empty, BackColor = SystemColors.Control };
        border.Controls.Add(portBox);
        _portBorders[portBox] = border;
        layout.Controls.Add(interfaceBox, 1, row);
        layout.Controls.Add(border, 2, row);
    }

    private static void SetInterface(ComboBox box, string value) => box.SelectedItem = value.Equals("any", StringComparison.OrdinalIgnoreCase) ? AllInterfacesLabel : LocalhostLabel;
    private static string GetInterface(ComboBox box) => string.Equals(Convert.ToString(box.SelectedItem), AllInterfacesLabel, StringComparison.OrdinalIgnoreCase) ? "any" : "localhost";

    private void ApplyInterfaceDependency()
    {
        var vmuAny = GetInterface(_vmuInterface).Equals("any", StringComparison.OrdinalIgnoreCase);
        if (vmuAny) SetInterface(_webInterface, "any");
        _webInterface.Enabled = !vmuAny;
        _tips.SetToolTip(_webInterface, vmuAny ? "Web Server must use All Interfaces while VMU Server uses All Interfaces." : string.Empty);
    }

    private void SaveSettings()
    {
        ApplyInterfaceDependency();
        if (!ValidatePorts(true)) return;

        var settings = ServerSettings.Load(_settingsPath);
        settings.Vmu.Interface = GetInterface(_vmuInterface);
        settings.Vmu.Port = (int)_vmuPort.Value;
        settings.Web.Interface = GetInterface(_webInterface);
        settings.Web.Port = (int)_webPort.Value;
        settings.Socket.Interface = GetInterface(_socketInterface);
        settings.Socket.Port = (int)_socketPort.Value;
        settings.Logging.RetentionMinutes = checked((int)_retentionDays.Value * 1440);
        settings.WebUi.ArrangementSnapTolerancePx = (int)_arrangementSnapTolerance.Value;
        settings.Exit.MonitorAction = string.Equals(Convert.ToString(_monitorExit.SelectedItem), "Keep", StringComparison.OrdinalIgnoreCase) ? MonitorExitAction.Keep : MonitorExitAction.Disconnect;
        settings.Exit.RestoreServices = _restoreServices.Checked;
        settings.Save(_settingsPath);
        _saved(_originalSettings, settings);
        Close();
    }

    private bool ValidatePorts(bool showDialog)
    {
        ClearAllPortErrors();
        var endpoints = new[] { new EndpointInput("VMU Server", _vmuPort), new EndpointInput("Web Server", _webPort), new EndpointInput("Web Socket", _socketPort) };
        var errors = new List<string>();

        foreach (var group in endpoints.GroupBy(x => x.Port).Where(x => x.Count() > 1))
        {
            var names = string.Join(" and ", group.Select(x => x.Name));
            foreach (var endpoint in group) SetPortError(endpoint.PortBox, $"Port {endpoint.Port} is configured for both {names}.");
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

        if (GetInterface(_vmuInterface).Equals("any", StringComparison.OrdinalIgnoreCase) && !GetInterface(_webInterface).Equals("any", StringComparison.OrdinalIgnoreCase))
            errors.Add("Web Server interface must use All Interfaces while VMU Server uses All Interfaces.");

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

    private void ClearAllPortErrors()
    {
        foreach (var port in _portBorders.Keys)
        {
            if (_portBorders.TryGetValue(port, out var border)) border.BackColor = SystemColors.Control;
            port.ForeColor = SystemColors.WindowText;
            _tips.SetToolTip(port, string.Empty);
        }
    }

    private sealed record EndpointInput(string Name, NumericUpDown PortBox)
    {
        public int Port => (int)PortBox.Value;
    }
}
