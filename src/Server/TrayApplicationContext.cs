using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Owns the VMU notification-area user interface for the lifetime of the application.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly string _settingsPath;
    private readonly LogStore _logStore;
    private readonly WebServerService _webService;
    private readonly WebSocketServerService _socketService;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private ToolStripMenuItem? _webMenu;
    private ToolStripMenuItem? _socketMenu;
    private LogForm? _logForm;
    private SettingsForm? _settingsForm;
    private bool _stopLogged;

    public TrayApplicationContext()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        var dataRoot = !string.IsNullOrWhiteSpace(repoRoot) ? Path.Combine(repoRoot, "data") : Path.Combine(AppContext.BaseDirectory, "data");
        _settingsPath = Path.Combine(dataRoot, "settings.json");
        _logStore = new LogStore(Path.Combine(dataRoot, "vmu.db"));
        _webService = new WebServerService(_logStore);
        _socketService = new WebSocketServerService(_logStore);
        ApplyLogRetention();
        _logStore.Write("INFO", "SERVER", "APPLICATION_START", "Virtual Monitors Universe Server started");

        _icon = TrayIconFactory.Create(Application.ExecutablePath);
        _menu = BuildMenu();
        _singleClickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(100, SystemInformation.DoubleClickTime) };
        _singleClickTimer.Tick += (_, _) => { _singleClickTimer.Stop(); _menu.Show(Cursor.Position); };
        _notifyIcon = new NotifyIcon { Icon = _icon, Text = "Virtual Monitors Universe", ContextMenuStrip = _menu, Visible = true };
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;
    }

    public void LogCrash(Exception exception)
    {
        try { _logStore.Write("ERROR", "SERVER", "APPLICATION_CRASH", $"Virtual Monitors Universe Server crashed: {exception.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { exception = exception.ToString() })); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _webService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _socketService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            LogStopOnce();
            _singleClickTimer.Stop();
            _singleClickTimer.Dispose();
            _logForm?.Dispose();
            _settingsForm?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Virtual Monitors Universe", _icon.ToBitmap()) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateVmuServerMenu());
        _webMenu = CreateNetworkServiceMenu("Web Server", _webService, includeOpenClient: true);
        _socketMenu = CreateNetworkServiceMenu("Web Socket", _socketService);
        menu.Items.Add(_webMenu);
        menu.Items.Add(_socketMenu);
        menu.Items.Add(CreateMonitorsMenu());
        menu.Items.Add(new ToolStripMenuItem("Settings", CreateGlyph("⚙"), (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("View log...", CreateGlyph("≡"), (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("About", CreateGlyph("i"), (_, _) => { }));
        menu.Items.Add(new ToolStripMenuItem("Exit", CreateGlyph("×"), OnExit));
        return menu;
    }

    private ToolStripMenuItem CreateVmuServerMenu()
    {
        var item = new ToolStripMenuItem("VMU Server", CreateGlyph("V"));
        item.DropDownItems.Add(new ToolStripMenuItem("Running", CreateStatusImage(Color.ForestGreen)) { Enabled = false });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Start", CreateGlyph("▶"), (_, _) => { }) { Enabled = false });
        item.DropDownItems.Add(new ToolStripMenuItem("Stop", CreateGlyph("■"), (_, _) => { }) { Enabled = false });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Restart", CreateGlyph("↻"), (_, _) => { }) { Enabled = false });
        return item;
    }

    private ToolStripMenuItem CreateNetworkServiceMenu(string text, NetworkService service, bool includeOpenClient = false)
    {
        var item = new ToolStripMenuItem(text, CreateGlyph(text == "Web Server" ? "W" : "S"));
        item.DropDownOpening += (_, _) => RefreshServiceMenu(item, service, includeOpenClient);
        RefreshServiceMenu(item, service, includeOpenClient);
        return item;
    }

    private void RefreshServiceMenu(ToolStripMenuItem item, NetworkService service, bool includeOpenClient)
    {
        item.DropDownItems.Clear();
        item.DropDownItems.Add(new ToolStripMenuItem(service.IsRunning ? "Running" : "Stopped", CreateStatusImage(service.IsRunning ? Color.ForestGreen : Color.Firebrick)) { Enabled = false });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Start", CreateGlyph("▶"), async (_, _) => await RunServiceActionAsync(service, ServiceAction.Start)) { Enabled = !service.IsRunning });
        item.DropDownItems.Add(new ToolStripMenuItem("Stop", CreateGlyph("■"), async (_, _) => await RunServiceActionAsync(service, ServiceAction.Stop)) { Enabled = service.IsRunning });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Restart", CreateGlyph("↻"), async (_, _) => await RunServiceActionAsync(service, ServiceAction.Restart)) { Enabled = service.IsRunning });
        if (includeOpenClient)
        {
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add(new ToolStripMenuItem("Open Client...", CreateGlyph("↗"), (_, _) => OpenWebClient()) { Enabled = service.IsRunning });
        }
    }

    private async Task RunServiceActionAsync(NetworkService service, ServiceAction action)
    {
        try
        {
            var settings = ServerSettings.Load(_settingsPath);
            var endpoint = service == _webService ? settings.Web : settings.Socket;
            switch (action)
            {
                case ServiceAction.Start: await service.StartAsync(endpoint); break;
                case ServiceAction.Stop: await service.StopAsync(); break;
                case ServiceAction.Restart: await service.RestartAsync(endpoint); break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{service.Name} operation failed.\r\n\r\n{ex.Message}", "Virtual Monitors Universe", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (_webMenu is not null) RefreshServiceMenu(_webMenu, _webService, includeOpenClient: true);
            if (_socketMenu is not null) RefreshServiceMenu(_socketMenu, _socketService, includeOpenClient: false);
        }
    }

    private static Bitmap CreateStatusImage(Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 3, 3, 10, 10);
        return bitmap;
    }

    private static Bitmap CreateGlyph(string glyph)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI Symbol", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(SystemColors.ControlText);
        var size = graphics.MeasureString(glyph, font);
        graphics.DrawString(glyph, font, brush, (16 - size.Width) / 2f, (16 - size.Height) / 2f);
        return bitmap;
    }

    private static ToolStripMenuItem CreateMonitorsMenu()
    {
        var item = new ToolStripMenuItem("Monitors", CreateGlyph("▣"));
        item.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
        return item;
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _singleClickTimer.Stop();
        _singleClickTimer.Start();
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
        try
        {
            var settings = ServerSettings.Load(_settingsPath);
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{settings.Web.Port}/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the web client.\r\n\r\n{ex.Message}", "VMU Web Client", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        if (_webService.IsRunning && EndpointChanged(oldSettings.Web, newSettings.Web)) await RunServiceActionAsync(_webService, ServiceAction.Restart);
        if (_socketService.IsRunning && EndpointChanged(oldSettings.Socket, newSettings.Socket)) await RunServiceActionAsync(_socketService, ServiceAction.Restart);
    }

    private static bool EndpointChanged(ServiceEndpointSettings oldEndpoint, ServiceEndpointSettings newEndpoint) => oldEndpoint.Port != newEndpoint.Port || !oldEndpoint.Interface.Equals(newEndpoint.Interface, StringComparison.OrdinalIgnoreCase);

    private void ApplyLogRetention()
    {
        var settings = ServerSettings.Load(_settingsPath);
        _logStore.DeleteOlderThan(settings.Logging.RetentionMinutes);
    }

    private void LogStopOnce()
    {
        if (_stopLogged) return;
        _stopLogged = true;
        _logStore.Write("INFO", "SERVER", "APPLICATION_STOP", "Virtual Monitors Universe Server stopped normally");
    }

    private void OnExit(object? sender, EventArgs e)
    {
        LogStopOnce();
        _notifyIcon.Visible = false;
        ExitThread();
    }

    private enum ServiceAction { Start, Stop, Restart }
}
