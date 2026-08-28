using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace VirtualMonitorsUniverse.Server;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon; private readonly ContextMenuStrip _menu; private readonly Icon _icon; private readonly string _settingsPath; private readonly LogStore _logStore;
    private readonly WebServerService _webService; private readonly WebSocketServerService _socketService; private readonly System.Windows.Forms.Timer _singleClickTimer;
    private ToolStripMenuItem? _webMenu; private ToolStripMenuItem? _socketMenu; private LogForm? _logForm; private SettingsForm? _settingsForm; private bool _stopLogged; private bool _exiting;

    public TrayApplicationContext()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT"); var dataRoot = !string.IsNullOrWhiteSpace(repoRoot) ? Path.Combine(repoRoot, "data") : Path.Combine(AppContext.BaseDirectory, "data");
        _settingsPath = Path.Combine(dataRoot, "settings.json"); _logStore = new LogStore(Path.Combine(dataRoot, "vmu.db")); _webService = new WebServerService(_logStore); _socketService = new WebSocketServerService(_logStore);
        ApplyLogRetention(); _logStore.Write("INFO", "VMU", "APPLICATION_START", "Virtual Monitors Universe started");
        _icon = TrayIconFactory.Create(Application.ExecutablePath); _menu = BuildMenu(); _singleClickTimer = new System.Windows.Forms.Timer { Interval = Math.Max(100, SystemInformation.DoubleClickTime) }; _singleClickTimer.Tick += (_, _) => { _singleClickTimer.Stop(); _menu.Show(Cursor.Position); };
        _notifyIcon = new NotifyIcon { Icon = _icon, Text = "Virtual Monitors Universe", ContextMenuStrip = _menu, Visible = true }; _notifyIcon.MouseClick += OnNotifyIconMouseClick; _notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;
        _ = RestoreServicesAsync();
    }

    public void LogCrash(Exception exception) { try { _logStore.Write("ERROR", "VMU", "APPLICATION_CRASH", $"VMU crashed: {exception.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { exception = exception.ToString() })); } catch { } }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _singleClickTimer.Stop(); _singleClickTimer.Dispose(); CloseOwnedWindows(); try { _webService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { } try { _socketService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { } LogStopOnce(); _notifyIcon.Visible = false; _notifyIcon.Dispose(); _menu.Dispose(); _icon.Dispose(); }
        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip(); menu.Items.Add(new ToolStripMenuItem("Virtual Monitors Universe", _icon.ToBitmap()) { Enabled = false }); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(CreateVmuServerMenu());
        _webMenu = CreateNetworkServiceMenu("Web Server", _webService, true); _socketMenu = CreateNetworkServiceMenu("Socket Server", _socketService); menu.Items.Add(_webMenu); menu.Items.Add(_socketMenu); menu.Items.Add(CreateMonitorsMenu());
        menu.Items.Add(new ToolStripMenuItem("Settings", CreateGlyph("⚙", Color.SteelBlue), (_, _) => OpenSettings())); menu.Items.Add(new ToolStripMenuItem("View log...", CreateGlyph("≡", Color.MediumSeaGreen), (_, _) => OpenLog())); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(new ToolStripMenuItem("About", CreateGlyph("i", Color.RoyalBlue), (_, _) => { })); menu.Items.Add(new ToolStripMenuItem("Exit", CreateGlyph("×", Color.Firebrick), OnExit)); return menu;
    }

    private ToolStripMenuItem CreateVmuServerMenu() { var item = new ToolStripMenuItem("VMU Server", CreateGlyph("V", Color.DodgerBlue)); item.DropDownItems.Add(new ToolStripMenuItem("Running", CreateStatusImage(Color.ForestGreen)) { Enabled = false }); item.DropDownItems.Add(new ToolStripSeparator()); item.DropDownItems.Add(new ToolStripMenuItem("Start", CreateGlyph("▶", Color.ForestGreen)) { Enabled = false }); item.DropDownItems.Add(new ToolStripMenuItem("Stop", CreateGlyph("■", Color.Firebrick)) { Enabled = false }); item.DropDownItems.Add(new ToolStripSeparator()); item.DropDownItems.Add(new ToolStripMenuItem("Restart", CreateGlyph("↻", Color.DarkOrange)) { Enabled = false }); return item; }
    private ToolStripMenuItem CreateNetworkServiceMenu(string text, NetworkService service, bool includeOpenClient = false) { var item = new ToolStripMenuItem(text, CreateGlyph(text.StartsWith("Web") ? "W" : "S", text.StartsWith("Web") ? Color.MediumSeaGreen : Color.MediumPurple)); item.DropDownOpening += (_, _) => RefreshServiceMenu(item, service, includeOpenClient); RefreshServiceMenu(item, service, includeOpenClient); return item; }
    private void RefreshServiceMenu(ToolStripMenuItem item, NetworkService service, bool open) { item.DropDownItems.Clear(); item.DropDownItems.Add(new ToolStripMenuItem(service.IsRunning ? "Running" : "Stopped", CreateStatusImage(service.IsRunning ? Color.ForestGreen : Color.Firebrick)) { Enabled = false }); item.DropDownItems.Add(new ToolStripSeparator()); item.DropDownItems.Add(new ToolStripMenuItem("Start", CreateGlyph("▶", Color.ForestGreen), async (_, _) => await RunServiceActionAsync(service, 0)) { Enabled = !service.IsRunning }); item.DropDownItems.Add(new ToolStripMenuItem("Stop", CreateGlyph("■", Color.Firebrick), async (_, _) => await RunServiceActionAsync(service, 1)) { Enabled = service.IsRunning }); item.DropDownItems.Add(new ToolStripSeparator()); item.DropDownItems.Add(new ToolStripMenuItem("Restart", CreateGlyph("↻", Color.DarkOrange), async (_, _) => await RunServiceActionAsync(service, 2)) { Enabled = service.IsRunning }); if (open) { item.DropDownItems.Add(new ToolStripSeparator()); item.DropDownItems.Add(new ToolStripMenuItem("Open Client...", CreateGlyph("↗", Color.RoyalBlue), (_, _) => OpenWebClient()) { Enabled = service.IsRunning }); } }
    private async Task RunServiceActionAsync(NetworkService service, int action) { try { var s = ServerSettings.Load(_settingsPath); var endpoint = service == _webService ? s.Web : s.Socket; if (action == 0) await service.StartAsync(endpoint); else if (action == 1) await service.StopAsync(); else await service.RestartAsync(endpoint); } catch (Exception ex) { MessageBox.Show($"{service.Name} operation failed.\r\n\r\n{ex.Message}", "VMU", MessageBoxButtons.OK, MessageBoxIcon.Error); } finally { RefreshMenus(); } }
    private void RefreshMenus() { if (_webMenu is not null) RefreshServiceMenu(_webMenu, _webService, true); if (_socketMenu is not null) RefreshServiceMenu(_socketMenu, _socketService, false); }
    private static Bitmap CreateStatusImage(Color c) { var b = new Bitmap(16, 16); using var g = Graphics.FromImage(b); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var brush = new SolidBrush(c); g.FillEllipse(brush, 1, 1, 14, 14); return b; }
    private static Bitmap CreateGlyph(string glyph, Color color) { var b = new Bitmap(20, 20); using var g = Graphics.FromImage(b); g.Clear(Color.Transparent); g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; using var font = new Font("Segoe UI Symbol", 15f, FontStyle.Bold, GraphicsUnit.Pixel); using var brush = new SolidBrush(color); var size = g.MeasureString(glyph, font); g.DrawString(glyph, font, brush, (20-size.Width)/2, (20-size.Height)/2); return b; }
    private static ToolStripMenuItem CreateMonitorsMenu() { var item = new ToolStripMenuItem("Monitors", CreateGlyph("▣", Color.Teal)); item.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false }); return item; }
    private void OnNotifyIconMouseClick(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _singleClickTimer.Stop(); _singleClickTimer.Start(); } }
    private void OnNotifyIconMouseDoubleClick(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _singleClickTimer.Stop(); OpenWebClient(); } }
    private void OpenWebClient() { if (!_webService.IsRunning) { MessageBox.Show("Web Server is not running.", "VMU Web Client", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } var s = ServerSettings.Load(_settingsPath); try { Process.Start(new ProcessStartInfo($"http://127.0.0.1:{s.Web.Port}/") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "VMU Web Client", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    private void OpenLog() { if (_logForm is null || _logForm.IsDisposed) { _logForm = new LogForm(_logStore, _icon); _logForm.FormClosed += (_, _) => _logForm = null; _logForm.Show(); } else _logForm.Activate(); }
    private void OpenSettings() { if (_settingsForm is null || _settingsForm.IsDisposed) { _settingsForm = new SettingsForm(_settingsPath, OnSettingsSaved, IsOwnedListener, _icon); _settingsForm.FormClosed += (_, _) => _settingsForm = null; _settingsForm.Show(); } else _settingsForm.Activate(); }
    private bool IsOwnedListener(int port) => _webService.ActivePort == port || _socketService.ActivePort == port;
    private void OnSettingsSaved(ServerSettings oldS, ServerSettings newS) { ApplyLogRetention(); _ = RestartChangedServicesAsync(oldS, newS); }
    private async Task RestartChangedServicesAsync(ServerSettings oldS, ServerSettings newS) { if (_webService.IsRunning && Changed(oldS.Web,newS.Web)) await RunServiceActionAsync(_webService,2); if (_socketService.IsRunning && Changed(oldS.Socket,newS.Socket)) await RunServiceActionAsync(_socketService,2); }
    private static bool Changed(ServiceEndpointSettings a, ServiceEndpointSettings b) => a.Port != b.Port || !a.Interface.Equals(b.Interface,StringComparison.OrdinalIgnoreCase);
    private void ApplyLogRetention() { var s=ServerSettings.Load(_settingsPath); _logStore.DeleteOlderThan(s.Logging.RetentionMinutes); }
    private async Task RestoreServicesAsync() { var s=ServerSettings.Load(_settingsPath); if (!s.Exit.RestoreServices) return; try { if (s.ServiceState.WebRunning) await _webService.StartAsync(s.Web); if (s.ServiceState.SocketRunning) await _socketService.StartAsync(s.Socket); } catch { } finally { RefreshMenus(); } }
    private void SaveServiceState() { var s=ServerSettings.Load(_settingsPath); if (s.Exit.RestoreServices) { s.ServiceState.WebRunning=_webService.IsRunning; s.ServiceState.SocketRunning=_socketService.IsRunning; } else { s.ServiceState.WebRunning=false; s.ServiceState.SocketRunning=false; } s.Save(_settingsPath); }
    private void CloseOwnedWindows() { try { _logForm?.Close(); } catch { } try { _settingsForm?.Close(); } catch { } foreach (Form form in Application.OpenForms.Cast<Form>().ToArray()) { try { form.Close(); } catch { } } }
    private void LogStopOnce() { if (_stopLogged) return; _stopLogged=true; _logStore.Write("INFO","VMU","APPLICATION_STOP","Virtual Monitors Universe stopped normally"); }
    private async void OnExit(object? sender, EventArgs e) { if (_exiting) return; _exiting=true; _singleClickTimer.Stop(); CloseOwnedWindows(); SaveServiceState(); try { await _webService.StopAsync(); } catch { } try { await _socketService.StopAsync(); } catch { } LogStopOnce(); _notifyIcon.Visible=false; ExitThread(); }
}
