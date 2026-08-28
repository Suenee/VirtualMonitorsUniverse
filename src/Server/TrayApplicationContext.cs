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
    private LogForm? _logForm;
    private SettingsForm? _settingsForm;
    private bool _stopLogged;

    public TrayApplicationContext()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        var dataRoot = !string.IsNullOrWhiteSpace(repoRoot)
            ? Path.Combine(repoRoot, "data")
            : Path.Combine(AppContext.BaseDirectory, "data");
        _settingsPath = Path.Combine(dataRoot, "settings.json");
        _logStore = new LogStore(Path.Combine(dataRoot, "vmu.db"));
        ApplyLogRetention();
        _logStore.Write("INFO", "SERVER", "APPLICATION_START", "Virtual Monitors Universe Server started");

        _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _menu = BuildMenu();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Virtual Monitors Universe",
            ContextMenuStrip = _menu,
            Visible = true,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LogStopOnce();
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
        menu.Items.Add(new ToolStripMenuItem("Virtual Monitors Universe") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateServiceMenu("Server"));
        menu.Items.Add(CreateServiceMenu("Web Server"));
        menu.Items.Add(CreateServiceMenu("WebSocket"));
        menu.Items.Add(CreateMonitorsMenu());
        menu.Items.Add(new ToolStripMenuItem("Settings", image: null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("View log...", image: null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreatePlaceholderItem("About"));
        menu.Items.Add(new ToolStripMenuItem("Exit", image: null, OnExit));
        return menu;
    }

    private static ToolStripMenuItem CreateServiceMenu(string text)
    {
        var item = new ToolStripMenuItem(text);
        item.DropDownItems.Add(CreatePlaceholderItem("Start"));
        item.DropDownItems.Add(CreatePlaceholderItem("Stop"));
        return item;
    }

    private static ToolStripMenuItem CreateMonitorsMenu()
    {
        var item = new ToolStripMenuItem("Monitors");
        item.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
        return item;
    }

    private static ToolStripMenuItem CreatePlaceholderItem(string text) => new(text, image: null, (_, _) => { });

    private void OpenLog()
    {
        if (_logForm is null || _logForm.IsDisposed)
        {
            _logForm = new LogForm(_logStore, _icon);
            _logForm.FormClosed += (_, _) => _logForm = null;
            _logForm.Show();
        }
        else
        {
            _logForm.Activate();
        }
    }

    private void OpenSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settingsPath, ApplyLogRetention, _icon);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
        }
        else
        {
            _settingsForm.Activate();
        }
    }

    private void ApplyLogRetention()
    {
        var settings = ServerSettings.Load(_settingsPath);
        _logStore.DeleteOlderThan(settings.Logging.RetentionMinutes);
    }

    private void LogStopOnce()
    {
        if (_stopLogged) return;
        _stopLogged = true;
        _logStore.Write("INFO", "SERVER", "APPLICATION_STOP", "Virtual Monitors Universe Server stopped");
    }

    private void OnExit(object? sender, EventArgs e)
    {
        LogStopOnce();
        _notifyIcon.Visible = false;
        ExitThread();
    }
}
