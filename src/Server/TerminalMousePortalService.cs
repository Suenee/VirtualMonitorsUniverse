using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Owns the optional mouse-portal session used after the browser enters a VMU
/// Terminal. The service is deliberately isolated from normal Terminal capture
/// and input forwarding: removing this file and its two integration calls restores
/// the pre-portal behavior.
/// </summary>
internal static class TerminalMousePortalService
{
    private const int ReturnOffset = 2;
    private static readonly object Sync = new();
    private static PortalSession? _session;

    static TerminalMousePortalService()
    {
        WindowsCursorTransitionService.MoveObserved += OnCursorMove;
    }

    public static void Begin(
        string vmuId,
        string deviceName,
        int displayX,
        int displayY,
        int displayWidth,
        int displayHeight,
        double browserLeft,
        double browserTop,
        double browserRight,
        double browserBottom)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (displayWidth <= 0 || displayHeight <= 0) return;
        if (browserRight <= browserLeft || browserBottom <= browserTop) return;

        lock (Sync)
        {
            _session = new PortalSession(
                vmuId,
                deviceName,
                displayX,
                displayY,
                displayX + displayWidth,
                displayY + displayHeight,
                browserLeft,
                browserTop,
                browserRight,
                browserBottom);
        }
    }

    public static void Cancel(string? vmuId = null)
    {
        lock (Sync)
        {
            if (vmuId is null || _session?.VmuId.Equals(vmuId, StringComparison.OrdinalIgnoreCase) == true)
                _session = null;
        }
    }

    private static void OnCursorMove(CursorMoveObservation observation)
    {
        PortalSession? session;
        lock (Sync) session = _session;
        if (session is null) return;

        var current = observation.CurrentPosition;
        var previous = observation.PreviousPosition;

        // A move generated while the cursor is no longer on the target display
        // closes the session without attempting another warp. This also protects
        // against Windows topology changes while a portal is active.
        if (observation.CurrentDisplay is null ||
            !observation.CurrentDisplay.DeviceName.Equals(session.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            Cancel(session.VmuId);
            return;
        }

        var left = current.X <= session.DisplayLeft;
        var right = current.X >= session.DisplayRight - 1;
        var top = current.Y <= session.DisplayTop;
        var bottom = current.Y >= session.DisplayBottom - 1;
        if (!left && !right && !top && !bottom) return;

        var edge = ChooseEdge(left, right, top, bottom, previous, current);
        var destination = MapReturnPoint(session, edge, current);

        // Clear the session before SetCursorPos. The resulting hook event must not
        // be interpreted as another portal movement.
        lock (Sync)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
        }

        if (!SetCursorPos(destination.X, destination.Y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not return the cursor from the Terminal portal.");
    }

    private static PortalEdge ChooseEdge(bool left, bool right, bool top, bool bottom, CursorPoint? previous, CursorPoint current)
    {
        if ((left || right) && !(top || bottom)) return left ? PortalEdge.Left : PortalEdge.Right;
        if ((top || bottom) && !(left || right)) return top ? PortalEdge.Top : PortalEdge.Bottom;

        if (previous is not null)
        {
            var dx = Math.Abs(current.X - previous.X);
            var dy = Math.Abs(current.Y - previous.Y);
            if (dx >= dy) return left ? PortalEdge.Left : PortalEdge.Right;
        }

        return top ? PortalEdge.Top : PortalEdge.Bottom;
    }

    private static CursorPoint MapReturnPoint(PortalSession session, PortalEdge edge, CursorPoint current)
    {
        var displayWidth = Math.Max(1, session.DisplayRight - session.DisplayLeft - 1);
        var displayHeight = Math.Max(1, session.DisplayBottom - session.DisplayTop - 1);
        var nx = Math.Clamp((current.X - session.DisplayLeft) / (double)displayWidth, 0d, 1d);
        var ny = Math.Clamp((current.Y - session.DisplayTop) / (double)displayHeight, 0d, 1d);

        var browserWidth = session.BrowserRight - session.BrowserLeft;
        var browserHeight = session.BrowserBottom - session.BrowserTop;
        var x = edge switch
        {
            PortalEdge.Left => session.BrowserLeft - ReturnOffset,
            PortalEdge.Right => session.BrowserRight + ReturnOffset,
            _ => session.BrowserLeft + nx * browserWidth
        };
        var y = edge switch
        {
            PortalEdge.Top => session.BrowserTop - ReturnOffset,
            PortalEdge.Bottom => session.BrowserBottom + ReturnOffset,
            _ => session.BrowserTop + ny * browserHeight
        };

        return new CursorPoint(checked((int)Math.Round(x)), checked((int)Math.Round(y)));
    }

    private enum PortalEdge { Left, Right, Top, Bottom }

    private sealed record PortalSession(
        string VmuId,
        string DeviceName,
        int DisplayLeft,
        int DisplayTop,
        int DisplayRight,
        int DisplayBottom,
        double BrowserLeft,
        double BrowserTop,
        double BrowserRight,
        double BrowserBottom);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
