using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

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
    private const string LogService = "WEB";

    private static readonly object Sync = new();
    private static PortalSession? _session;
    private static LogStore? _logStore;

    static TerminalMousePortalService()
    {
        WindowsCursorTransitionService.MoveObserved += OnCursorMove;
    }

    /// <summary>
    /// Supplies the normal VMU operational log. This optional dependency keeps
    /// portal diagnostics isolated from Terminal input and capture services.
    /// </summary>
    public static void ConfigureLogging(LogStore logStore) => _logStore = logStore;

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

        var session = new PortalSession(
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

        lock (Sync) _session = session;

        WriteLog("INFO", "TERMINAL_PORTAL_BEGIN", "Terminal mouse exit session started.", session,
            new
            {
                deviceName,
                display = new { left = session.DisplayLeft, top = session.DisplayTop, right = session.DisplayRight, bottom = session.DisplayBottom },
                browser = new { left = browserLeft, top = browserTop, right = browserRight, bottom = browserBottom }
            });
    }

    public static void Cancel(string? vmuId = null)
    {
        PortalSession? cancelled = null;
        lock (Sync)
        {
            if (vmuId is null || _session?.VmuId.Equals(vmuId, StringComparison.OrdinalIgnoreCase) == true)
            {
                cancelled = _session;
                _session = null;
            }
        }

        if (cancelled is not null)
            WriteLog("INFO", "TERMINAL_PORTAL_CANCEL", "Terminal mouse exit session was cancelled.", cancelled);
    }

    private static void OnCursorMove(CursorMoveObservation observation)
    {
        PortalSession? session;
        lock (Sync) session = _session;
        if (session is null) return;

        var current = observation.CurrentPosition;

        if (session.EntryPosition is null)
        {
            session.EntryPosition = current;
            session.LastPosition = current;
            session.LastZone = ZoneFor(session, current);
            WriteMoveLog(session, observation, current, "entry");
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
            WriteMoveLog(session, observation, current, "armed");
        }

        var previous = session.LastPosition ?? observation.PreviousPosition ?? current;
        session.LastPosition = current;

        var currentZone = ZoneFor(session, current);
        if (!string.Equals(currentZone, session.LastZone, StringComparison.Ordinal))
        {
            session.LastZone = currentZone;
            WriteMoveLog(session, observation, current, currentZone);
        }

        var currentOnTarget = observation.CurrentDisplay is not null &&
            observation.CurrentDisplay.DeviceName.Equals(session.DeviceName, StringComparison.OrdinalIgnoreCase);

        if (!currentOnTarget)
        {
            if (InsideDisplay(session, previous))
            {
                var crossedEdge = ChooseCrossedEdge(session, previous, current);
                WriteLog("INFO", "TERMINAL_PORTAL_EDGE", $"Cursor crossed Terminal edge {crossedEdge}.", session,
                    MoveDetails(session, observation, previous, current, crossedEdge.ToString()));
                ReturnToBrowser(session, crossedEdge, previous);
            }
            else
            {
                WriteLog("INFO", "TERMINAL_PORTAL_CANCEL", "Cursor left the target display without a usable edge crossing.", session,
                    MoveDetails(session, observation, previous, current, "outside-target"));
                Cancel(session.VmuId);
            }

            return;
        }

        var dx = current.X - previous.X;
        var dy = current.Y - previous.Y;
        var edge = DetectReachedEdge(session, current, dx, dy);
        if (edge is null) return;

        WriteLog("INFO", "TERMINAL_PORTAL_EDGE", $"Cursor reached Terminal edge {edge.Value}.", session,
            MoveDetails(session, observation, previous, current, edge.Value.ToString()));
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

        lock (Sync)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
        }

        WriteLog("INFO", "TERMINAL_PORTAL_RETURN", $"Returning cursor from Terminal through {edge} edge.", session,
            new { edge = edge.ToString(), source, destination });

        if (SetCursorPos(destination.X, destination.Y)) return;

        var error = Marshal.GetLastWin32Error();
        WriteLog("ERROR", "TERMINAL_PORTAL_RETURN_FAILED", $"Windows could not return the cursor from the Terminal portal (Win32 {error}).", session,
            new { edge = edge.ToString(), source, destination, win32Error = error });
        throw new Win32Exception(error, "Windows could not return the cursor from the Terminal portal.");
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

    private static string ZoneFor(PortalSession session, CursorPoint point)
    {
        if (!InsideDisplay(session, point)) return "outside";
        if (point.X - session.DisplayLeft <= EdgeThreshold) return "near-left";
        if (session.DisplayRight - 1 - point.X <= EdgeThreshold) return "near-right";
        if (point.Y - session.DisplayTop <= EdgeThreshold) return "near-top";
        if (session.DisplayBottom - 1 - point.Y <= EdgeThreshold) return "near-bottom";
        return "inside";
    }

    private static void WriteMoveLog(PortalSession session, CursorMoveObservation observation, CursorPoint current, string reason)
    {
        var previous = session.LastPosition ?? observation.PreviousPosition;
        WriteLog("DEBUG", "TERMINAL_PORTAL_MOVE", $"Terminal portal cursor observation: {reason}.", session,
            MoveDetails(session, observation, previous, current, reason));
    }

    private static object MoveDetails(PortalSession session, CursorMoveObservation observation, CursorPoint? previous, CursorPoint current, string reason)
        => new
        {
            reason,
            previous,
            current,
            currentDisplay = observation.CurrentDisplay?.DeviceName,
            targetDisplay = session.DeviceName,
            armed = session.Armed,
            distances = new
            {
                left = current.X - session.DisplayLeft,
                right = session.DisplayRight - 1 - current.X,
                top = current.Y - session.DisplayTop,
                bottom = session.DisplayBottom - 1 - current.Y
            }
        };

    private static void WriteLog(string level, string eventName, string message, PortalSession session, object? details = null)
    {
        try
        {
            _logStore?.Write(level, LogService, eventName, message, session.VmuId,
                details is null ? null : JsonSerializer.Serialize(details));
        }
        catch
        {
            // Diagnostics must never alter mouse behavior or escape into User32.
        }
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
        public string? LastZone { get; set; }
        public bool Armed { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
