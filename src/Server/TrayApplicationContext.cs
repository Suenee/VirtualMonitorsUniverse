using System.Diagnostics;

namespace VirtualMonitorsUniverse.Server;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly string _settingsPath;
    private readonly string _dataRoot;
    private readonly LogStore _logStore;
    private readonly MonitorApplicationService _monitorService;
    private readonly WebServerService _webService;
    private readonly WebSocketServerService _socketService;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private readonly ToolStripMenuItem _vmuStateItem, _vmuStartItem, _vmuStopItem, _vmuRestartItem;
    private readonly ToolStripMenuItem _webStateItem, _webStartItem, _webStopItem, _webRestartItem, _webOpenItem;
    private readonly ToolStripMenuItem _socketStateItem, _socketStartItem, _socketStopItem, _socketRestartItem;
    private readonly ToolStripMenuItem _monitorsMenu;
    private LogForm? _logForm;
    private SettingsForm? _settingsForm;
    private AboutForm? _aboutForm;
    private bool _vmuServerRunning;
    private bool _stopLogged;
    private bool _exiting;

    public TrayApplicationContext()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        _dataRoot = !string.IsNullOrWhiteSpace(repoRoot) ? Path.Combine(repoRoot, "data") : Path.Combine(AppContext.BaseDirectory, "data");
        var databasePath = Path.Combine(_dataRoot, "vmu.db");
        _settingsPath = Path.Combine(_dataRoot, "settings.json");
        _logStore = new LogStore(databasePath);
        _monitorService = new MonitorApplicationService(new MonitorStore(databasePath), _logStore, _dataRoot);
        _webService = new WebServerService(_logStore, _monitorService, GetStatusSnapshot, () => ServerSettings.Load(_settingsPath), SaveSettingsFromWebAsync, IsOwnedListener);
        _socketService = new WebSocketServerService(_logStore);
        ApplyLogRetention();
        _logStore.Write("INFO", "VMU", "APPLICATION_START", $"{ProjectInfo.ProductName} {ProjectInfo.Version} started");

        _icon = TrayIconFactory.Create(Application.ExecutablePath);
        _menu = new ContextMenuStrip { ImageScalingSize = new Size(20, 20), ShowImageMargin = true, ShowCheckMargin = false };
        _menu.Opening += (_, _) => RefreshServiceMenuStates();
        _menu.Items.Add(new ToolStripMenuItem(ProjectInfo.ProductName, _icon.ToBitmap()) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var vmuMenu = new ToolStripMenuItem("VMU Server", UiIcons.Create(UiIconKind.Server));
        (_vmuStateItem, _vmuStartItem, _vmuStopItem, _vmuRestartItem) = PopulateServiceMenu(vmuMenu, false, OnVmuAction);
        ConfigureDropDownDirection(vmuMenu); _menu.Items.Add(vmuMenu);

        var webMenu = new ToolStripMenuItem("Web Server", UiIcons.Create(UiIconKind.Web));
        (_webStateItem, _webStartItem, _webStopItem, _webRestartItem) = PopulateServiceMenu(webMenu, true, OnWebAction);
        _webOpenItem = webMenu.DropDownItems.OfType<ToolStripMenuItem>().Last();
        ConfigureDropDownDirection(webMenu); _menu.Items.Add(webMenu);

        var socketMenu = new ToolStripMenuItem("Socket Server", UiIcons.Create(UiIconKind.Socket));
        (_socketStateItem, _socketStartItem, _socketStopItem, _socketRestartItem) = PopulateServiceMenu(socketMenu, false, OnSocketAction);
        ConfigureDropDownDirection(socketMenu); _menu.Items.Add(socketMenu);

        _monitorsMenu = new ToolStripMenuItem("Monitors", UiIcons.Create(UiIconKind.Monitors));
        _monitorsMenu.DropDownOpening += (_, _) => RefreshMonitorMenu();
        ConfigureDropDownDirection(_monitorsMenu); _menu.Items.Add(_monitorsMenu);

        _menu.Items.Add(new ToolStripMenuItem("Settings", UiIcons.Create(UiIconKind.Settings), (_, _) => CloseMenuThen(OpenSettings)));
        _menu.Items.Add(new ToolStripMenuItem("View log...", UiIcons.Create(UiIconKind.Log), (_, _) => CloseMenuThen(OpenLog)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("About", UiIcons.Create(UiIconKind.About), (_, _) => CloseMenuThen(OpenAbout)));
        _menu.Items.Add(new ToolStripMenuItem("Exit", UiIcons.Create(UiIconKind.Exit), OnExit));

        _singleClickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(100, SystemInformation.DoubleClickTime) };
        _singleClickTimer.Tick += (_, _) => { _singleClickTimer.Stop(); ShowTrayMenu(); };
        _notifyIcon = new NotifyIcon { Icon = _icon, Text = $"{ProjectInfo.ProductName} {ProjectInfo.Version}", ContextMenuStrip = _menu, Visible = true };
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;
        RefreshServiceMenuStates();
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
            _singleClickTimer.Stop(); _singleClickTimer.Dispose(); CloseOwnedWindows();
            try { _webService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _socketService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            LogStopOnce(); _notifyIcon.Visible = false; _notifyIcon.Dispose(); _menu.Dispose(); _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private (ToolStripMenuItem State, ToolStripMenuItem Start, ToolStripMenuItem Stop, ToolStripMenuItem Restart) PopulateServiceMenu(ToolStripMenuItem parent, bool includeOpenClient, Action<int> action)
    {
        var state = new ToolStripMenuItem("Stopped", UiIcons.Create(UiIconKind.Stopped)) { Enabled = false };
        var start = new ToolStripMenuItem("Start", UiIcons.Create(UiIconKind.Start), (_, _) => action(0));
        var stop = new ToolStripMenuItem("Stop", UiIcons.Create(UiIconKind.Stop), (_, _) => action(1));
        var restart = new ToolStripMenuItem("Restart", UiIcons.Create(UiIconKind.Restart), (_, _) => action(2));
        parent.DropDownItems.Add(state); parent.DropDownItems.Add(new ToolStripSeparator()); parent.DropDownItems.Add(start); parent.DropDownItems.Add(stop); parent.DropDownItems.Add(new ToolStripSeparator()); parent.DropDownItems.Add(restart);
        if (includeOpenClient) { parent.DropDownItems.Add(new ToolStripSeparator()); parent.DropDownItems.Add(new ToolStripMenuItem("Open Client...", UiIcons.Create(UiIconKind.Open), (_, _) => CloseMenuThen(OpenWebClient))); }
        return (state, start, stop, restart);
    }

    private static void ConfigureDropDownDirection(ToolStripMenuItem item)
    {
        item.DropDownOpening += (_, _) =>
        {
            if (item.Owner is null) return;
            var topLeft = item.Owner.PointToScreen(item.Bounds.Location); var itemBounds = new Rectangle(topLeft, item.Bounds.Size); var workingArea = Screen.FromRectangle(itemBounds).WorkingArea; var preferred = item.DropDown.GetPreferredSize(Size.Empty);
            var roomRight = workingArea.Right - itemBounds.Right; var roomLeft = itemBounds.Left - workingArea.Left;
            item.DropDownDirection = roomRight >= preferred.Width || roomRight >= roomLeft ? ToolStripDropDownDirection.Right : ToolStripDropDownDirection.Left;
        };
    }

    private void RefreshMonitorMenu()
    {
        _monitorsMenu.DropDownItems.Clear();
        IReadOnlyList<MonitorSnapshot> monitors;
        try { monitors = _monitorService.List(); }
        catch (Exception ex) { _monitorsMenu.DropDownItems.Add(new ToolStripMenuItem($"Unavailable: {ex.Message}") { Enabled = false }); return; }
        if (monitors.Count == 0) { _monitorsMenu.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false }); return; }
        foreach (var monitor in monitors)
        {
            var item = new ToolStripMenuItem(monitor.Configuration.Title, MonitorAvatarService.CreateTrayImage(monitor.Configuration, _dataRoot))
            {
                ShortcutKeyDisplayString = monitor.Connected ? "🟢" : "⚪",
                ToolTipText = monitor.Connected ? "Connected — open Terminal" : "Disconnected — open Terminal",
            };
            item.Click += (_, _) => CloseMenuThen(() => OpenMonitorTerminal(monitor.Configuration.Name));
            _monitorsMenu.DropDownItems.Add(item);
        }
    }

    private void OnVmuAction(int action) { if (action == 0) SetVmuServerState(true); else if (action == 1) SetVmuServerState(false); else RestartVmuServer(); }
    private void SetVmuServerState(bool running) { if (_vmuServerRunning == running) return; _vmuServerRunning = running; _logStore.Write("INFO", "VMU_SERVER", running ? "SERVICE_START" : "SERVICE_STOP", running ? "VMU Server started (simulated)" : "VMU Server stopped (simulated)"); RefreshServiceMenuStates(); }
    private void RestartVmuServer() { if (!_vmuServerRunning) return; _vmuServerRunning = false; _logStore.Write("INFO", "VMU_SERVER", "SERVICE_STOP", "VMU Server stopped for restart (simulated)"); _vmuServerRunning = true; _logStore.Write("INFO", "VMU_SERVER", "SERVICE_START", "VMU Server started after restart (simulated)"); RefreshServiceMenuStates(); }
    private void OnWebAction(int action) => _ = RunServiceActionAsync(_webService, action);
    private void OnSocketAction(int action) => _ = RunServiceActionAsync(_socketService, action);

    private async Task RunServiceActionAsync(NetworkService service, int action)
    {
        try { var settings = ServerSettings.Load(_settingsPath); var endpoint = service == _webService ? settings.Web : settings.Socket; if (action == 0) await service.StartAsync(endpoint); else if (action == 1) await service.StopAsync(); else await service.RestartAsync(endpoint); }
        catch (Exception ex) { MessageBox.Show($"{service.Name} operation failed.\r\n\r\n{ex.Message}", "VMU", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { RefreshServiceMenuStates(); }
    }

    private void RefreshServiceMenuStates()
    {
        SetMenuState(_vmuStateItem, _vmuStartItem, _vmuStopItem, _vmuRestartItem, _vmuServerRunning);
        SetMenuState(_webStateItem, _webStartItem, _webStopItem, _webRestartItem, _webService.IsRunning);
        SetMenuState(_socketStateItem, _socketStartItem, _socketStopItem, _socketRestartItem, _socketService.IsRunning);
        _webOpenItem.Enabled = _webService.IsRunning;
    }

    private static void SetMenuState(ToolStripMenuItem state, ToolStripMenuItem start, ToolStripMenuItem stop, ToolStripMenuItem restart, bool running)
    {
        state.Text = running ? "Running" : "Stopped"; state.Image?.Dispose(); state.Image = UiIcons.Create(running ? UiIconKind.Running : UiIconKind.Stopped); start.Enabled = !running; stop.Enabled = running; restart.Enabled = running;
    }

    private IReadOnlyDictionary<string, bool> GetStatusSnapshot() => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["VMU"] = true, ["VMU_SERVER"] = _vmuServerRunning, ["WEB"] = _webService.IsRunning, ["SOCKET"] = _socketService.IsRunning };
    private void ShowTrayMenu() { if (_menu.Visible) { _menu.Close(ToolStripDropDownCloseReason.AppClicked); return; } RefreshServiceMenuStates(); _menu.Show(Cursor.Position); }
    private void CloseMenuThen(Action action) { if (_menu.Visible) _menu.Close(ToolStripDropDownCloseReason.ItemClicked); action(); }
    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; _singleClickTimer.Stop(); _singleClickTimer.Start(); }
    private void OnNotifyIconMouseDoubleClick(object? sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; _singleClickTimer.Stop(); OpenWebClient(); }

    private void OpenWebClient()
    {
        if (!_webService.IsRunning) { MessageBox.Show("Web Server is not running.", "VMU Web Client", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        OpenUrl($"http://127.0.0.1:{ServerSettings.Load(_settingsPath).Web.Port}/");
    }

    private void OpenMonitorTerminal(string name)
    {
        if (!_webService.IsRunning) { MessageBox.Show("Web Server is not running.", "VMU Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        OpenUrl($"http://127.0.0.1:{ServerSettings.Load(_settingsPath).Web.Port}/monitor/{Uri.EscapeDataString(name)}");
    }

    private static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "VMU", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    private void OpenLog() { if (_logForm is null || _logForm.IsDisposed) { _logForm = new LogForm(_logStore, _icon, GetStatusSnapshot); _logForm.FormClosed += (_, _) => _logForm = null; _logForm.Show(); } else _logForm.Activate(); }
    private void OpenSettings() { if (_settingsForm is null || _settingsForm.IsDisposed) { _settingsForm = new SettingsForm(_settingsPath, OnSettingsSaved, IsOwnedListener, _icon); _settingsForm.FormClosed += (_, _) => _settingsForm = null; _settingsForm.Show(); } else _settingsForm.Activate(); }
    private void OpenAbout() { if (_aboutForm is null || _aboutForm.IsDisposed) { _aboutForm = new AboutForm(_icon); _aboutForm.FormClosed += (_, _) => _aboutForm = null; _aboutForm.Show(); } else _aboutForm.Activate(); }
    private bool IsOwnedListener(int port) => _webService.ActivePort == port || _socketService.ActivePort == port;
    private void OnSettingsSaved(ServerSettings oldSettings, ServerSettings newSettings) { ApplyLogRetention(); _ = RestartChangedServicesAsync(oldSettings, newSettings); }

    private async Task RestartChangedServicesAsync(ServerSettings oldSettings, ServerSettings newSettings)
    {
        if (_webService.IsRunning && EndpointChanged(oldSettings.Web, newSettings.Web)) await RunServiceActionAsync(_webService, 2);
        if (_socketService.IsRunning && EndpointChanged(oldSettings.Socket, newSettings.Socket)) await RunServiceActionAsync(_socketService, 2);
    }

    private async Task<WebSettingsSaveResult> SaveSettingsFromWebAsync(ServerSettings proposed)
    {
        var current = ServerSettings.Load(_settingsPath); proposed.ServiceState = current.ServiceState; proposed.Save(_settingsPath); ApplyLogRetention();
        var webChanged = EndpointChanged(current.Web, proposed.Web); var target = $"http://127.0.0.1:{proposed.Web.Port}/";
        if (webChanged && _webService.IsRunning) _ = Task.Run(async () => { await Task.Delay(400); try { await _webService.RestartAsync(proposed.Web); } catch { } try { _menu.BeginInvoke((Action)RefreshServiceMenuStates); } catch { } });
        if (_socketService.IsRunning && EndpointChanged(current.Socket, proposed.Socket)) _ = _socketService.RestartAsync(proposed.Socket);
        return new WebSettingsSaveResult(target, webChanged, 10000);
    }

    private static bool EndpointChanged(ServiceEndpointSettings left, ServiceEndpointSettings right) => left.Port != right.Port || !left.Interface.Equals(right.Interface, StringComparison.OrdinalIgnoreCase);
    private void ApplyLogRetention() { var settings = ServerSettings.Load(_settingsPath); _logStore.DeleteOlderThan(settings.Logging.RetentionMinutes); }

    private async Task RestoreServicesAsync()
    {
        var settings = ServerSettings.Load(_settingsPath); if (!settings.Exit.RestoreServices) return; _vmuServerRunning = settings.ServiceState.VmuServerRunning;
        try { if (settings.ServiceState.WebRunning) await _webService.StartAsync(settings.Web); if (settings.ServiceState.SocketRunning) await _socketService.StartAsync(settings.Socket); } catch { } finally { RefreshServiceMenuStates(); }
    }

    private void SaveServiceState()
    {
        var settings = ServerSettings.Load(_settingsPath);
        settings.ServiceState.VmuServerRunning = settings.Exit.RestoreServices && _vmuServerRunning;
        settings.ServiceState.WebRunning = settings.Exit.RestoreServices && _webService.IsRunning;
        settings.ServiceState.SocketRunning = settings.Exit.RestoreServices && _socketService.IsRunning;
        settings.Save(_settingsPath);
    }

    private void CloseOwnedWindows() { try { _logForm?.Close(); } catch { } try { _settingsForm?.Close(); } catch { } try { _aboutForm?.Close(); } catch { } foreach (Form form in Application.OpenForms.Cast<Form>().ToArray()) try { form.Close(); } catch { } }
    private void LogStopOnce() { if (_stopLogged) return; _stopLogged = true; _logStore.Write("INFO", "VMU", "APPLICATION_STOP", $"{ProjectInfo.ProductName} stopped normally"); }

    private async void OnExit(object? sender, EventArgs e)
    {
        if (_exiting) return; _exiting = true; _singleClickTimer.Stop(); _menu.Close(); CloseOwnedWindows(); SaveServiceState();
        try { await _webService.StopAsync(); } catch { } try { await _socketService.StopAsync(); } catch { } LogStopOnce(); _notifyIcon.Visible = false; ExitThread();
    }
}
