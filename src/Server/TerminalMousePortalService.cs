using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Owns only the mouse-exit side of an active Terminal portal session.
///
/// Mouse entry remains the established TerminalInputService behavior. Once entry
/// succeeds, this service observes the same event-driven Windows cursor stream
/// already used by Terminal capture recovery. It projects the target virtual
/// display edges back to the active image rectangle in the browser and returns
/// the system cursor when the user reaches or crosses an edge.
///
/// The service is intentionally isolated. Removing this file and the existing
/// Begin/Cancel calls restores the pre-exit behavior without changing mouse entry.
/// </summary>
internal static class TerminalMousePortalService
{
    private const int ReturnOffset = 2;
    private const int EdgeThreshold = 2;
    private const int ArmDistance = 2;

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
                checked(displayX + displayWidth),
                checked(displayY + displayHeight),
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

        // The first cursor events after entry include the deliberate one-pixel
        // DXGI wake-up nudge. Record the real entry point and arm edge detection
        // only after the user has moved far enough to distinguish real movement
        // from that synthetic wake-up sequence.
        if (session.EntryPosition is null)
        {
            session.EntryPosition = current;
            session.LastPosition = current;
            return;
        }

        if (!session.Armed)
        {
            if (DistanceFrom(session.EntryPosition, current) < ArmDistance)
            {
                session.LastPosition = current;
                return;
            }

            session.Armed = true;
        }

        var previous = session.LastPosition ?? observation.PreviousPosition ?? current;
        session.LastPosition = current;

        var currentOnTarget = observation.CurrentDisplay is not null &&
            observation.CurrentDisplay.DeviceName.Equals(session.DeviceName, StringComparison.OrdinalIgnoreCase);

        if (!currentOnTarget)
        {
            // Windows may cross directly from the virtual monitor into an adjacent
            // display in a single mouse move. Treat that as portal exit instead of
            // silently cancelling the session as the previous implementation did.
            if (InsideDisplay(session, previous))
            {
                var crossedEdge = ChooseCrossedEdge(session, previous, current);
                ReturnToBrowser(session, crossedEdge, previous);
            }
            else
            {
                Cancel(session.VmuId);
            }

            return;
        }

        var dx = current.X - previous.X;
        var dy = current.Y - previous.Y;
        var edge = DetectReachedEdge(session, current, dx, dy);
        if (edge is null) return;

        ReturnToBrowser(session, edge.Value, current);
    }

    private static PortalEdge? DetectReachedEdge(PortalSession session, CursorPoint current, int dx, int dy)
    {
        var leftDistance = current.X - session.DisplayLeft;
        var rightDistance = session.DisplayRight - 1 - current.X;
        var topDistance = current.Y - session.DisplayTop;
        var bottomDistance = session.DisplayBottom - 1 - current.Y;

        var left = leftDistance <= EdgeThreshold && dx < 0;
        var right = rightDistance <= EdgeThreshold && dx > 0;
        var top = topDistance <= EdgeThreshold && dy < 0;
        var bottom = bottomDistance <= EdgeThreshold && dy > 0;

        if (!left && !right && !top && !bottom) return null;
        return ChooseEdge(left, right, top, bottom, dx, dy);
    }

    private static PortalEdge ChooseCrossedEdge(PortalSession session, CursorPoint previous, CursorPoint current)
    {
        var dx = current.X - previous.X;
        var dy = current.Y - previous.Y;

        var crossedLeft = current.X < session.DisplayLeft;
        var crossedRight = current.X >= session.DisplayRight;
        var crossedTop = current.Y < session.DisplayTop;
        var crossedBottom = current.Y >= session.DisplayBottom;

        if (crossedLeft || crossedRight || crossedTop || crossedBottom)
            return ChooseEdge(crossedLeft, crossedRight, crossedTop, crossedBottom, dx, dy);

        // A topology hand-off can report another display even when the sampled
        // point is numerically on the shared edge. Fall back to travel direction.
        if (Math.Abs(dx) >= Math.Abs(dy))
            return dx < 0 ? PortalEdge.Left : PortalEdge.Right;
        return dy < 0 ? PortalEdge.Top : PortalEdge.Bottom;
    }

    private static PortalEdge ChooseEdge(bool left, bool right, bool top, bool bottom, int dx, int dy)
    {
        if ((left || right) && !(top || bottom)) return left ? PortalEdge.Left : PortalEdge.Right;
        if ((top || bottom) && !(left || right)) return top ? PortalEdge.Top : PortalEdge.Bottom;

        if (Math.Abs(dx) >= Math.Abs(dy))
            return left ? PortalEdge.Left : PortalEdge.Right;
        return top ? PortalEdge.Top : PortalEdge.Bottom;
    }

    private static void ReturnToBrowser(PortalSession session, PortalEdge edge, CursorPoint source)
    {
        var destination = MapReturnPoint(session, edge, source);

        // Clear the session before SetCursorPos. The generated cursor event must
        // never be interpreted as another portal movement.
        lock (Sync)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
        }

        if (!SetCursorPos(destination.X, destination.Y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not return the cursor from the Terminal portal.");
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

    private static bool InsideDisplay(PortalSession session, CursorPoint point)
        => point.X >= session.DisplayLeft && point.X < session.DisplayRight &&
           point.Y >= session.DisplayTop && point.Y < session.DisplayBottom;

    private static double DistanceFrom(CursorPoint origin, CursorPoint point)
    {
        var dx = point.X - origin.X;
        var dy = point.Y - origin.Y;
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    private enum PortalEdge { Left, Right, Top, Bottom }

    private sealed class PortalSession(
        string vmuId,
        string deviceName,
        int displayLeft,
        int displayTop,
        int displayRight,
        int displayBottom,
        double browserLeft,
        double browserTop,
        double browserRight,
        double browserBottom)
    {
        public string VmuId { get; } = vmuId;
        public string DeviceName { get; } = deviceName;
        public int DisplayLeft { get; } = displayLeft;
        public int DisplayTop { get; } = displayTop;
        public int DisplayRight { get; } = displayRight;
        public int DisplayBottom { get; } = displayBottom;
        public double BrowserLeft { get; } = browserLeft;
        public double BrowserTop { get; } = browserTop;
        public double BrowserRight { get; } = browserRight;
        public double BrowserBottom { get; } = browserBottom;
        public CursorPoint? EntryPosition { get; set; }
        public CursorPoint? LastPosition { get; set; }
        public bool Armed { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
