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

    public TrayApplicationContext()
    {
        _menu = BuildMenu();
        _notifyIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "Virtual Monitors Universe",
            ContextMenuStrip = _menu,
            Visible = true,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var title = new ToolStripMenuItem("Virtual Monitors Universe")
        {
            Enabled = false,
        };

        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateServiceMenu("Server"));
        menu.Items.Add(CreateServiceMenu("Web Server"));
        menu.Items.Add(CreateServiceMenu("WebSocket"));
        menu.Items.Add(CreateMonitorsMenu());
        menu.Items.Add(CreatePlaceholderItem("Settings"));
        menu.Items.Add(CreatePlaceholderItem("View Log"));
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

    private static ToolStripMenuItem CreatePlaceholderItem(string text)
    {
        return new ToolStripMenuItem(text, image: null, (_, _) => { });
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        ExitThread();
    }
}
