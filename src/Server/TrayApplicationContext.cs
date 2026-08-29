using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

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
    private readonly ToolStripMenuItem _vmuStateItem;
    private readonly ToolStripMenuItem _vmuStartItem;
    private readonly ToolStripMenuItem _vmuStopItem;
    private readonly ToolStripMenuItem _vmuRestartItem;
    private readonly ToolStripMenuItem _webStateItem;
    private readonly ToolStripMenuItem _webStartItem;
    private readonly ToolStripMenuItem _webStopItem;
    private readonly ToolStripMenuItem _webRestartItem;
    private readonly ToolStripMenuItem _webOpenItem;
    private readonly ToolStripMenuItem _socketStateItem;
    private readonly ToolStripMenuItem _socketStartItem;
    private readonly ToolStripMenuItem _socketStopItem;
    private readonly ToolStripMenuItem _socketRestartItem;
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
        _logStore.Write("INFO", "VMU", "APPLICATION_START", $"{ProjectInfo.ProductName} {ProjectInfo.Version} started", detailsJson: JsonSerializer.Serialize(new
        {
            dataRoot = Path.GetFullPath(_dataRoot),
            settingsPath = Path.GetFullPath(_settingsPath),
            databasePath = _logStore.DatabasePath
        }));

        _icon = TrayIconFactory.Create(Application.ExecutablePath);
        _menu = new ContextMenuStrip
        {
            ImageScalingSize = new Size(20, 20),
            ShowImageMargin = true,
            ShowCheckMargin = false,
            AutoClose = true
        };
        _menu.Opening += (_, _) => RefreshServiceMenuStates();
        _menu.Opened += (_, _) => ActivateTrayMenu();
        _menu.Items.Add(new ToolStripMenuItem(ProjectInfo.ProductName, _icon.ToBitmap()) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var vmu = new ToolStripMenuItem("VMU Server", UiIcons.Create(UiIconKind.Server));
        (_vmuStateItem, _vmuStartItem, _vmuStopItem, _vmuRestartItem) = PopulateServiceMenu(vmu, false, OnVmuAction);
        ConfigureDropDownDirection(vmu);
        _menu.Items.Add(vmu);

        var web = new ToolStripMenuItem("Web Server", UiIcons.Create(UiIconKind.Web));
        (_webStateItem, _webStartItem, _webStopItem, _webRestartItem) = PopulateServiceMenu(web, true, OnWebAction);
        _webOpenItem = web.DropDownItems.OfType<ToolStripMenuItem>().Last();
        ConfigureDropDownDirection(web);
        _menu.Items.Add(web);

        var socket = new ToolStripMenuItem("Socket Server", UiIcons.Create(UiIconKind.Socket));
        (_socketStateItem, _socketStartItem, _socketStopItem, _socketRestartItem) = PopulateServiceMenu(socket, false, OnSocketAction);
        ConfigureDropDownDirection(socket);
        _menu.Items.Add(socket);

        _monitorsMenu = new ToolStripMenuItem("Monitors", UiIcons.Create(UiIconKind.Monitors));
        _monitorsMenu.DropDownOpening += (_, _) => RefreshMonitorMenu();
        ConfigureDropDownDirection(_monitorsMenu);
        _menu.Items.Add(_monitorsMenu);

        _menu.Items.Add(new ToolStripMenuItem("Settings...", UiIcons.Create(UiIconKind.Settings), (_, _) => CloseMenuThen(OpenSettings)));
        _menu.Items.Add(new ToolStripMenuItem("View log...", UiIcons.Create(UiIconKind.Log), (_, _) => CloseMenuThen(OpenLog)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("About...", UiIcons.Create(UiIconKind.About), (_, _) => CloseMenuThen(OpenAbout)));
        _menu.Items.Add(new ToolStripMenuItem("Exit", UiIcons.Create(UiIconKind.Exit), OnExit));

        _singleClickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(100, SystemInformation.DoubleClickTime) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            ShowTrayMenu();
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = $"{ProjectInfo.ProductName} {ProjectInfo.Version}",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;

        RefreshServiceMenuStates();
        _ = RestoreServicesAsync();
    }

    public void LogCrash(Exception ex)
    {
        try { _logStore.Write("ERROR", "VMU", "APPLICATION_CRASH", $"VMU crashed: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { exception = ex.ToString() })); }
        catch { }
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
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private (ToolStripMenuItem State, ToolStripMenuItem Start, ToolStripMenuItem Stop, ToolStripMenuItem Restart) PopulateServiceMenu(ToolStripMenuItem parent, bool includeOpen, Action<int> action)
    {
        var state = new ToolStripMenuItem("Stopped", UiIcons.Create(UiIconKind.Stopped)) { Enabled = false };
        var start = new ToolStripMenuItem("Start", UiIcons.Create(UiIconKind.Start), (_, _) => action(0));
        var stop = new ToolStripMenuItem("Stop", UiIcons.Create(UiIconKind.Stop), (_, _) => action(1));
        var restart = new ToolStripMenuItem("Restart", UiIcons.Create(UiIconKind.Restart), (_, _) => action(2));
        parent.DropDownItems.Add(state);
        parent.DropDownItems.Add(new ToolStripSeparator());
        parent.DropDownItems.Add(start);
        parent.DropDownItems.Add(stop);
        parent.DropDownItems.Add(new ToolStripSeparator());
        parent.DropDownItems.Add(restart);
        if (includeOpen)
        {
            parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add(new ToolStripMenuItem("Open Client...", UiIcons.Create(UiIconKind.Open), (_, _) => CloseMenuThen(OpenWebClient)));
        }
        return (state, start, stop, restart);
    }

    private static void ConfigureDropDownDirection(ToolStripMenuItem item)
    {
        item.DropDownOpening += (_, _) =>
        {
            if (item.Owner is null) return;
            var topLeft = item.Owner.PointToScreen(item.Bounds.Location);
            var bounds = new Rectangle(topLeft, item.Bounds.Size);
            var area = Screen.FromRectangle(bounds).WorkingArea;
            var preferred = item.DropDown.GetPreferredSize(Size.Empty);
            var right = area.Right - bounds.Right;
            var left = bounds.Left - area.Left;
            item.DropDownDirection = right >= preferred.Width || right >= left ? ToolStripDropDownDirection.Right : ToolStripDropDownDirection.Left;
        };
    }

    private void RefreshMonitorMenu()
    {
        foreach (ToolStripItem existing in _monitorsMenu.DropDownItems)
        {
            existing.Image?.Dispose();
        }
        _monitorsMenu.DropDownItems.Clear();

        IReadOnlyList<MonitorSnapshot> monitors;
        try { monitors = _monitorService.List(); }
        catch (Exception ex)
        {
            _monitorsMenu.DropDownItems.Add(new ToolStripMenuItem($"Unavailable: {ex.Message}") { Enabled = false });
            return;
        }

        if (monitors.Count == 0)
        {
            _monitorsMenu.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
            return;
        }

        foreach (var monitor in monitors)
        {
            var terminal = _vmuServerRunning && monitor.Connected && !monitor.Health.IsError;
            var item = new ToolStripMenuItem(monitor.Configuration.Title, MonitorAvatarService.CreateTrayImage(monitor.Configuration, _dataRoot))
            {
                ImageScaling = ToolStripItemImageScaling.SizeToFit,
                ShortcutKeyDisplayString = "     ",
                ToolTipText = terminal ? "Connected — open Terminal" : "Open monitor properties"
            };
            var statusColor = monitor.Health.IsError ? Color.Firebrick : terminal ? Color.SeaGreen : Color.DarkGray;
            item.Paint += (_, e) =>
            {
                var diameter = 9;
                var x = Math.Max(2, item.Width - 18);
                var y = Math.Max(2, (item.Height - diameter) / 2);
                using var brush = new SolidBrush(statusColor);
                e.Graphics.FillEllipse(brush, x, y, diameter, diameter);
            };
            item.Click += (_, _) => CloseMenuThen(() => OpenMonitor(monitor.Configuration.Name, terminal));
            _monitorsMenu.DropDownItems.Add(item);
        }
    }

    private void OnVmuAction(int action)
    {
        if (action == 0) SetVmuServerState(true);
        else if (action == 1) SetVmuServerState(false);
        else RestartVmuServer();
    }

    private void SetVmuServerState(bool running)
    {
        if (_vmuServerRunning == running) return;
        _vmuServerRunning = running;
        _logStore.Write("INFO", "VMU_SERVER", running ? "SERVICE_START" : "SERVICE_STOP", running ? "VMU Server started (simulated)" : "VMU Server stopped (simulated)");
        PersistServiceStateIfEnabled();
        RefreshServiceMenuStates();
    }

    private void RestartVmuServer()
    {
        if (!_vmuServerRunning) return;
        _vmuServerRunning = false;
        _vmuServerRunning = true;
        _logStore.Write("INFO", "VMU_SERVER", "SERVICE_START", "VMU Server restarted (simulated)");
        PersistServiceStateIfEnabled();
        RefreshServiceMenuStates();
    }

    private void OnWebAction(int action) => _ = RunServiceActionAsync(_webService, action);
    private void OnSocketAction(int action) => _ = RunServiceActionAsync(_socketService, action);

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
            PersistServiceStateIfEnabled();
            RefreshServiceMenuStates();
        }
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
        state.Text = running ? "Running" : "Stopped";
        state.Image?.Dispose();
        state.Image = UiIcons.Create(running ? UiIconKind.Running : UiIconKind.Stopped);
        start.Enabled = !running;
        stop.Enabled = running;
        restart.Enabled = running;
    }

    private IReadOnlyDictionary<string, bool> GetStatusSnapshot() => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        ["VMU"] = true,
        ["VMU_SERVER"] = _vmuServerRunning,
        ["WEB"] = _webService.IsRunning,
        ["SOCKET"] = _socketService.IsRunning
    };

    private void ShowTrayMenu()
    {
        if (_menu.Visible)
        {
            _menu.Close(ToolStripDropDownCloseReason.AppClicked);
            return;
        }
        RefreshServiceMenuStates();
        _menu.Show(Cursor.Position);
        ActivateTrayMenu();
    }

    private void ActivateTrayMenu()
    {
        if (!_menu.IsHandleCreated) return;
        SetForegroundWindow(_menu.Handle);
    }

    private void CloseMenuThen(Action action)
    {
        if (_menu.Visible) _menu.Close(ToolStripDropDownCloseReason.ItemClicked);
        action();
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Right)
        {
            _singleClickTimer.Stop();
            return;
        }
        if (eventArgs.Button != MouseButtons.Left) return;
        _singleClickTimer.Stop();
        _singleClickTimer.Start();
    }

    private void OnNotifyIconMouseDoubleClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left) return;
        _singleClickTimer.Stop();
        OpenWebClient();
    }

    private void OpenWebClient()
    {
        if (!_webService.IsRunning)
        {
            MessageBox.Show("Web Server is not running.", "VMU Web Client");
            return;
        }
        OpenUrl($"http://127.0.0.1:{ServerSettings.Load(_settingsPath).Web.Port}/");
    }

    private void OpenMonitor(string name, bool terminal)
    {
        if (!_webService.IsRunning)
        {
            MessageBox.Show("Web Server is not running.", "VMU Monitor");
            return;
        }
        var path = terminal ? $"/monitor/{Uri.EscapeDataString(name)}" : $"/monitors/{Uri.EscapeDataString(name)}";
        OpenUrl($"http://127.0.0.1:{ServerSettings.Load(_settingsPath).Web.Port}{path}");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "VMU", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenLog()
    {
        if (_logForm is null || _logForm.IsDisposed)
        {
            _logForm = new LogForm(_logStore, _icon, GetStatusSnapshot);
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

    private void OpenAbout()
    {
        if (_aboutForm is null || _aboutForm.IsDisposed)
        {
            _aboutForm = new AboutForm(_icon);
            _aboutForm.FormClosed += (_, _) => _aboutForm = null;
            _aboutForm.Show();
        }
        else _aboutForm.Activate();
    }

    private bool IsOwnedListener(int port) => _webService.ActivePort == port || _socketService.ActivePort == port;

    private void OnSettingsSaved(ServerSettings oldSettings, ServerSettings newSettings)
    {
        ApplyLogRetention();
        PersistServiceStateIfEnabled();
        _ = RestartChangedServicesAsync(oldSettings, newSettings);
    }

    private async Task RestartChangedServicesAsync(ServerSettings oldSettings, ServerSettings newSettings)
    {
        if (_webService.IsRunning && EndpointChanged(oldSettings.Web, newSettings.Web)) await RunServiceActionAsync(_webService, 2);
        if (_socketService.IsRunning && EndpointChanged(oldSettings.Socket, newSettings.Socket)) await RunServiceActionAsync(_socketService, 2);
    }

    private async Task<WebSettingsSaveResult> SaveSettingsFromWebAsync(ServerSettings proposed)
    {
        var current = ServerSettings.Load(_settingsPath);
        proposed.ServiceState = current.ServiceState;
        proposed.Save(_settingsPath);
        ApplyLogRetention();
        PersistServiceStateIfEnabled();

        var changed = EndpointChanged(current.Web, proposed.Web);
        var target = $"http://127.0.0.1:{proposed.Web.Port}/";
        if (changed && _webService.IsRunning)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(400);
                try { await _webService.RestartAsync(proposed.Web); } catch { }
            });
        }
        if (_socketService.IsRunning && EndpointChanged(current.Socket, proposed.Socket)) _ = _socketService.RestartAsync(proposed.Socket);
        return new WebSettingsSaveResult(target, changed, 10000);
    }

    private static bool EndpointChanged(ServiceEndpointSettings left, ServiceEndpointSettings right) =>
        left.Port != right.Port || !left.Interface.Equals(right.Interface, StringComparison.OrdinalIgnoreCase);

    private void ApplyLogRetention()
    {
        var settings = ServerSettings.Load(_settingsPath);
        var result = _logStore.CleanupOlderThan(settings.Logging.RetentionMinutes);
        var details = JsonSerializer.Serialize(result);
        if (result.SafetyBlocked)
            _logStore.Write("WARN", "VMU", "LOG_RETENTION_SAFETY_BLOCK", $"Retention cleanup was blocked because it would delete {result.Candidates} of {result.TotalBefore} log records.", detailsJson: details);
        else if (result.Deleted > 0)
            _logStore.Write("INFO", "VMU", "LOG_RETENTION_CLEANUP", $"Retention cleanup deleted {result.Deleted} old log records; {result.TotalAfter} remained.", detailsJson: details);
        else
            _logStore.Write("INFO", "VMU", "LOG_RETENTION_CHECK", $"Retention cleanup checked {result.TotalBefore} records; none were deleted.", detailsJson: details);
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
            _logStore.Write("INFO", "VMU", "SERVICES_RESTORED", "Persisted service states were restored after application start.");
        }
        catch (Exception ex)
        {
            _logStore.Write("ERROR", "VMU", "SERVICES_RESTORE_FAILED", $"Service restore failed: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { exception = ex.ToString() }));
        }
        finally
        {
            PersistServiceStateIfEnabled();
            RefreshServiceMenuStates();
        }
    }

    private void PersistServiceStateIfEnabled()
    {
        var settings = ServerSettings.Load(_settingsPath);
        if (!settings.Exit.RestoreServices) return;
        settings.ServiceState.VmuServerRunning = _vmuServerRunning;
        settings.ServiceState.WebRunning = _webService.IsRunning;
        settings.ServiceState.SocketRunning = _socketService.IsRunning;
        settings.Save(_settingsPath);
    }

    private void SaveServiceState()
    {
        var settings = ServerSettings.Load(_settingsPath);
        settings.ServiceState.VmuServerRunning = settings.Exit.RestoreServices && _vmuServerRunning;
        settings.ServiceState.WebRunning = settings.Exit.RestoreServices && _webService.IsRunning;
        settings.ServiceState.SocketRunning = settings.Exit.RestoreServices && _socketService.IsRunning;
        settings.Save(_settingsPath);
    }

    private void ApplyMonitorExitPolicy()
    {
        var settings = ServerSettings.Load(_settingsPath);
        if (settings.Exit.MonitorAction != MonitorExitAction.Disconnect) return;

        foreach (var monitor in _monitorService.List().Where(x => x.Connected))
        {
            try { _monitorService.Disconnect(monitor.Configuration.VmuId); }
            catch (Exception ex)
            {
                _logStore.Write("ERROR", "VMU", "MONITOR_EXIT_DISCONNECT_FAILED", $"Could not disconnect {monitor.Configuration.Title} during application exit: {ex.Message}", monitor.Configuration.VmuId, JsonSerializer.Serialize(new { exception = ex.ToString() }));
            }
        }
    }

    private void CloseOwnedWindows()
    {
        try { _logForm?.Close(); } catch { }
        try { _settingsForm?.Close(); } catch { }
        try { _aboutForm?.Close(); } catch { }
    }

    private void LogStopOnce()
    {
        if (_stopLogged) return;
        _stopLogged = true;
        _logStore.Write("INFO", "VMU", "APPLICATION_STOP", $"{ProjectInfo.ProductName} stopped normally");
    }

    private async void OnExit(object? sender, EventArgs eventArgs)
    {
        if (_exiting) return;
        _exiting = true;
        _singleClickTimer.Stop();
        _menu.Close();
        CloseOwnedWindows();
        SaveServiceState();
        ApplyMonitorExitPolicy();
        try { await _webService.StopAsync(); } catch { }
        try { await _socketService.StopAsync(); } catch { }
        LogStopOnce();
        _notifyIcon.Visible = false;
        ExitThread();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
