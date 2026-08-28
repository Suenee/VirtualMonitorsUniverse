using System.Diagnostics;

namespace VirtualMonitorsUniverse.Server;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Form _menuOwner;
    private readonly Icon _icon;
    private readonly string _settingsPath;
    private readonly LogStore _logStore;
    private readonly WebServerService _webService;
    private readonly WebSocketServerService _socketService;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private ToolStripMenuItem? _vmuMenu;
    private ToolStripMenuItem? _webMenu;
    private ToolStripMenuItem? _socketMenu;
    private LogForm? _logForm;
    private SettingsForm? _settingsForm;
    private bool _vmuServerRunning;
    private bool _stopLogged;
    private bool _exiting;

    public TrayApplicationContext()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        var dataRoot = !string.IsNullOrWhiteSpace(repoRoot) ? Path.Combine(repoRoot, "data") : Path.Combine(AppContext.BaseDirectory, "data");
        _settingsPath = Path.Combine(dataRoot, "settings.json");
        _logStore = new LogStore(Path.Combine(dataRoot, "vmu.db"));
        _webService = new WebServerService(_logStore);
        _socketService = new WebSocketServerService(_logStore);
        ApplyLogRetention();
        _logStore.Write("INFO", "VMU", "APPLICATION_START", "Virtual Monitors Universe started");

        _icon = TrayIconFactory.Create(Application.ExecutablePath);
        _menu = BuildMenu();
        _menu.AutoClose = true;
        _menuOwner = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(1, 1),
            Opacity = 0.01,
            TopMost = true,
        };
        _menuOwner.Deactivate += (_, _) => { if (_menu.Visible && !_menu.Bounds.Contains(Cursor.Position)) _menu.Close(ToolStripDropDownCloseReason.AppClicked); };
        _menu.Closed += (_, _) => { if (_menuOwner.Visible) _menuOwner.Hide(); };

        _singleClickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(100, SystemInformation.DoubleClickTime) };
        _singleClickTimer.Tick += (_, _) => { _singleClickTimer.Stop(); ShowTrayMenu(); };
        _notifyIcon = new NotifyIcon { Icon = _icon, Text = "Virtual Monitors Universe", Visible = true };
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;
        _ = RestoreServicesAsync();
    }

    public void LogCrash(Exception exception)
    {
        try { _logStore.Write("ERROR", "VMU", "APPLICATION_CRASH", $"VMU crashed: {exception.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { exception = exception.ToString() })); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _singleClickTimer.Stop();
            _singleClickTimer.Dispose();
            CloseOwnedWindows();
            try { _webService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _socketService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            LogStopOnce();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _menuOwner.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ImageScalingSize = new Size(20, 20) };
        menu.Items.Add(new ToolStripMenuItem("Virtual Monitors Universe", _icon.ToBitmap()) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        _vmuMenu = CreateVmuServerMenu();
        _webMenu = CreateNetworkServiceMenu("Web Server", _webService, true);
        _socketMenu = CreateNetworkServiceMenu("Socket Server", _socketService, false);
        menu.Items.Add(_vmuMenu);
        menu.Items.Add(_webMenu);
        menu.Items.Add(_socketMenu);
        menu.Items.Add(CreateMonitorsMenu());
        menu.Items.Add(new ToolStripMenuItem("Settings", UiIcons.Create(UiIconKind.Settings), (_, _) => { _menu.Close(); OpenSettings(); }));
        menu.Items.Add(new ToolStripMenuItem("View log...", UiIcons.Create(UiIconKind.Log), (_, _) => { _menu.Close(); OpenLog(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("About", UiIcons.Create(UiIconKind.About), (_, _) => _menu.Close()));
        menu.Items.Add(new ToolStripMenuItem("Exit", UiIcons.Create(UiIconKind.Exit), OnExit));
        return menu;
    }

    private ToolStripMenuItem CreateVmuServerMenu()
    {
        var item = new ToolStripMenuItem("VMU Server", UiIcons.Create(UiIconKind.Server));
        item.DropDown.AutoClose = false;
        item.DropDownOpening += (_, _) => RefreshVmuServerMenu(item);
        RefreshVmuServerMenu(item);
        return item;
    }

    private void RefreshVmuServerMenu(ToolStripMenuItem item)
    {
        item.DropDownItems.Clear();
        item.DropDownItems.Add(new ToolStripMenuItem(_vmuServerRunning ? "Running" : "Stopped", UiIcons.Create(_vmuServerRunning ? UiIconKind.Running : UiIconKind.Stopped)) { Enabled = false });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Start", UiIcons.Create(UiIconKind.Start), (_, _) => SetVmuServerState(true)) { Enabled = !_vmuServerRunning });
        item.DropDownItems.Add(new ToolStripMenuItem("Stop", UiIcons.Create(UiIconKind.Stop), (_, _) => SetVmuServerState(false)) { Enabled = _vmuServerRunning });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Restart", UiIcons.Create(UiIconKind.Restart), (_, _) => RestartVmuServer()) { Enabled = _vmuServerRunning });
    }

    private void SetVmuServerState(bool running)
    {
        if (_vmuServerRunning == running) return;
        _vmuServerRunning = running;
        _logStore.Write("INFO", "VMU_SERVER", running ? "SERVICE_START" : "SERVICE_STOP", running ? "VMU Server started (simulated)" : "VMU Server stopped (simulated)");
        if (_vmuMenu is not null) RefreshVmuServerMenu(_vmuMenu);
    }

    private void RestartVmuServer()
    {
        if (!_vmuServerRunning) return;
        _vmuServerRunning = false;
        _logStore.Write("INFO", "VMU_SERVER", "SERVICE_STOP", "VMU Server stopped for restart (simulated)");
        _vmuServerRunning = true;
        _logStore.Write("INFO", "VMU_SERVER", "SERVICE_START", "VMU Server started after restart (simulated)");
        if (_vmuMenu is not null) RefreshVmuServerMenu(_vmuMenu);
    }

    private ToolStripMenuItem CreateNetworkServiceMenu(string text, NetworkService service, bool includeOpenClient)
    {
        var iconKind = service == _webService ? UiIconKind.Web : UiIconKind.Socket;
        var item = new ToolStripMenuItem(text, UiIcons.Create(iconKind));
        item.DropDown.AutoClose = false;
        item.DropDownOpening += (_, _) => RefreshServiceMenu(item, service, includeOpenClient);
        RefreshServiceMenu(item, service, includeOpenClient);
        return item;
    }

    private void RefreshServiceMenu(ToolStripMenuItem item, NetworkService service, bool includeOpenClient)
    {
        item.DropDownItems.Clear();
        item.DropDownItems.Add(new ToolStripMenuItem(service.IsRunning ? "Running" : "Stopped", UiIcons.Create(service.IsRunning ? UiIconKind.Running : UiIconKind.Stopped)) { Enabled = false });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Start", UiIcons.Create(UiIconKind.Start), async (_, _) => await RunServiceActionAsync(service, 0)) { Enabled = !service.IsRunning });
        item.DropDownItems.Add(new ToolStripMenuItem("Stop", UiIcons.Create(UiIconKind.Stop), async (_, _) => await RunServiceActionAsync(service, 1)) { Enabled = service.IsRunning });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Restart", UiIcons.Create(UiIconKind.Restart), async (_, _) => await RunServiceActionAsync(service, 2)) { Enabled = service.IsRunning });
        if (includeOpenClient)
        {
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(new ToolStripMenuItem("Open Client...", UiIcons.Create(UiIconKind.Open), (_, _) => { _menu.Close(); OpenWebClient(); }) { Enabled = service.IsRunning });
        }
    }

    private async Task RunServiceActionAsync(NetworkService service, int action)
    {
        try
        {
            var settings = ServerSettings.Load(_settingsPath);
            var endpoint = service == _webService ? settings.Web : settings.Socket;
            if (action == 0) await service.StartAsync(endpoint);
            else if (action == 1) await service.StopAsync();
            else await service.RestartAsync(endpoint);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{service.Name} operation failed.\r\n\r\n{ex.Message}", "VMU", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshMenus();
        }
    }

    private void RefreshMenus()
    {
        if (_vmuMenu is not null) RefreshVmuServerMenu(_vmuMenu);
        if (_webMenu is not null) RefreshServiceMenu(_webMenu, _webService, true);
        if (_socketMenu is not null) RefreshServiceMenu(_socketMenu, _socketService, false);
    }

    private static ToolStripMenuItem CreateMonitorsMenu()
    {
        var item = new ToolStripMenuItem("Monitors", UiIcons.Create(UiIconKind.Monitors));
        item.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
        return item;
    }

    private void ShowTrayMenu()
    {
        if (_menu.Visible)
        {
            _menu.Close(ToolStripDropDownCloseReason.AppClicked);
            return;
        }
        var cursor = Cursor.Position;
        _menuOwner.Location = cursor;
        if (!_menuOwner.Visible) _menuOwner.Show();
        _menuOwner.Activate();
        _menu.Show(cursor);
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { _singleClickTimer.Stop(); ShowTrayMenu(); return; }
        if (e.Button == MouseButtons.Left) { _singleClickTimer.Stop(); _singleClickTimer.Start(); }
    }

    private void OnNotifyIconMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _singleClickTimer.Stop();
        OpenWebClient();
    }

    private void OpenWebClient()
    {
        if (!_webService.IsRunning)
        {
            MessageBox.Show("Web Server is not running.", "VMU Web Client", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var settings = ServerSettings.Load(_settingsPath);
        try { Process.Start(new ProcessStartInfo($"http://127.0.0.1:{settings.Web.Port}/") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "VMU Web Client", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenLog()
    {
        if (_logForm is null || _logForm.IsDisposed)
        {
            _logForm = new LogForm(_logStore, _icon);
            _logForm.FormClosed += (_, _) => _logForm = null;
            _logForm.Show();
        }
        else _logForm.Activate();
    }

    private void OpenSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settingsPath, OnSettingsSaved, IsOwnedListener, _icon);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
        }
        else _settingsForm.Activate();
    }

    private bool IsOwnedListener(int port) => _webService.ActivePort == port || _socketService.ActivePort == port;

    private void OnSettingsSaved(ServerSettings oldSettings, ServerSettings newSettings)
    {
        ApplyLogRetention();
        _ = RestartChangedServicesAsync(oldSettings, newSettings);
    }

    private async Task RestartChangedServicesAsync(ServerSettings oldSettings, ServerSettings newSettings)
    {
        if (_webService.IsRunning && EndpointChanged(oldSettings.Web, newSettings.Web)) await RunServiceActionAsync(_webService, 2);
        if (_socketService.IsRunning && EndpointChanged(oldSettings.Socket, newSettings.Socket)) await RunServiceActionAsync(_socketService, 2);
    }

    private static bool EndpointChanged(ServiceEndpointSettings left, ServiceEndpointSettings right) => left.Port != right.Port || !left.Interface.Equals(right.Interface, StringComparison.OrdinalIgnoreCase);

    private void ApplyLogRetention()
    {
        var settings = ServerSettings.Load(_settingsPath);
        _logStore.DeleteOlderThan(settings.Logging.RetentionMinutes);
    }

    private async Task RestoreServicesAsync()
    {
        var settings = ServerSettings.Load(_settingsPath);
        if (!settings.Exit.RestoreServices) return;
        _vmuServerRunning = settings.ServiceState.VmuServerRunning;
        try
        {
            if (settings.ServiceState.WebRunning) await _webService.StartAsync(settings.Web);
            if (settings.ServiceState.SocketRunning) await _socketService.StartAsync(settings.Socket);
        }
        catch { }
        finally { RefreshMenus(); }
    }

    private void SaveServiceState()
    {
        var settings = ServerSettings.Load(_settingsPath);
        if (settings.Exit.RestoreServices)
        {
            settings.ServiceState.VmuServerRunning = _vmuServerRunning;
            settings.ServiceState.WebRunning = _webService.IsRunning;
            settings.ServiceState.SocketRunning = _socketService.IsRunning;
        }
        else
        {
            settings.ServiceState.VmuServerRunning = false;
            settings.ServiceState.WebRunning = false;
            settings.ServiceState.SocketRunning = false;
        }
        settings.Save(_settingsPath);
    }

    private void CloseOwnedWindows()
    {
        try { _logForm?.Close(); } catch { }
        try { _settingsForm?.Close(); } catch { }
        foreach (Form form in Application.OpenForms.Cast<Form>().Where(x => x != _menuOwner).ToArray())
        {
            try { form.Close(); } catch { }
        }
    }

    private void LogStopOnce()
    {
        if (_stopLogged) return;
        _stopLogged = true;
        _logStore.Write("INFO", "VMU", "APPLICATION_STOP", "Virtual Monitors Universe stopped normally");
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        if (_exiting) return;
        _exiting = true;
        _singleClickTimer.Stop();
        _menu.Close();
        CloseOwnedWindows();
        SaveServiceState();
        try { await _webService.StopAsync(); } catch { }
        try { await _socketService.StopAsync(); } catch { }
        LogStopOnce();
        _notifyIcon.Visible = false;
        ExitThread();
    }
}
