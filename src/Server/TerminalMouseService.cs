using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Applies Terminal mouse input to the Windows desktop coordinates occupied by a
/// connected VMU monitor. Localhost can use a portal hand-off: the real Windows
/// cursor enters the virtual display and a lightweight watcher returns it to the
/// corresponding edge of the browser-side Terminal image.
/// </summary>
internal sealed class TerminalMouseService
{
    private const uint InputMouse = 0;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfWheel = 0x0800;
    private readonly object _portalSync = new();
    private CancellationTokenSource? _portalCancellation;
    private int? _pendingPortalLeft;
    private int? _pendingPortalTop;

    public void Apply(MonitorSnapshot monitor, TerminalMouseRequest request)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Terminal mouse control is supported only on Windows.");
        if (!monitor.Connected || monitor.Health.IsError || monitor.PositionX is null || monitor.PositionY is null)
            throw new InvalidOperationException("The monitor must be connected and healthy before mouse input can be applied.");
        if (!monitor.Configuration.CollaborationMouse)
            throw new InvalidOperationException("Mouse collaboration is disabled for this monitor.");

        var type = request.Type.ToLowerInvariant();
        if (type == "portalbounds")
        {
            SetPortalBoundsStart(request.Button, request.Delta);
            return;
        }

        if (request.X is not null || request.Y is not null)
        {
            if (request.X is null || request.Y is null || request.X < 0 || request.X > 1 || request.Y < 0 || request.Y > 1)
                throw new ArgumentOutOfRangeException(nameof(request), "Mouse coordinates must be normalized values from 0 to 1.");

            var x = monitor.PositionX.Value + Math.Clamp((int)Math.Round(request.X.Value * Math.Max(0, monitor.Width - 1)), 0, Math.Max(0, monitor.Width - 1));
            var y = monitor.PositionY.Value + Math.Clamp((int)Math.Round(request.Y.Value * Math.Max(0, monitor.Height - 1)), 0, Math.Max(0, monitor.Height - 1));
            if (!SetCursorPos(x, y))
                throw new InvalidOperationException($"Windows rejected cursor positioning at {x},{y}.");
        }

        switch (type)
        {
            case "move":
                return;
            case "portal":
                StartLocalPortalWatcher(monitor, request.Button, request.Delta);
                return;
            case "down":
                SendButton(request.Button, down: true);
                return;
            case "up":
                SendButton(request.Button, down: false);
                return;
            case "wheel":
                SendMouse(MouseeventfWheel, request.Delta);
                return;
            default:
                throw new ArgumentException($"Unsupported Terminal mouse event '{request.Type}'.", nameof(request));
        }
    }

    private void SetPortalBoundsStart(int left, int top)
    {
        lock (_portalSync)
        {
            _pendingPortalLeft = left;
            _pendingPortalTop = top;
        }
    }

    private void StartLocalPortalWatcher(MonitorSnapshot monitor, int browserRight, int browserBottom)
    {
        CancellationTokenSource cancellation;
        int browserLeft;
        int browserTop;
        lock (_portalSync)
        {
            browserLeft = _pendingPortalLeft ?? browserRight;
            browserTop = _pendingPortalTop ?? browserBottom;
            _pendingPortalLeft = null;
            _pendingPortalTop = null;
            _portalCancellation?.Cancel();
            _portalCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _portalCancellation = cancellation;
        }

        var left = monitor.PositionX!.Value;
        var top = monitor.PositionY!.Value;
        var right = left + Math.Max(1, monitor.Width) - 1;
        var bottom = top + Math.Max(1, monitor.Height) - 1;
        _ = Task.Run(async () =>
        {
            try
            {
                // Do not immediately interpret an entry close to an edge as an exit.
                await Task.Delay(140, cancellation.Token);
                while (!cancellation.IsCancellationRequested)
                {
                    if (!GetCursorPos(out var point)) return;
                    if (point.X < left || point.X > right || point.Y < top || point.Y > bottom) return;
                    if (point.X <= left + 1 || point.X >= right - 1 || point.Y <= top + 1 || point.Y >= bottom - 1)
                    {
                        var width = Math.Max(1, right - left);
                        var height = Math.Max(1, bottom - top);
                        var nx = Math.Clamp((point.X - left) / (double)width, 0, 1);
                        var ny = Math.Clamp((point.Y - top) / (double)height, 0, 1);
                        var browserWidth = Math.Max(1, browserRight - browserLeft);
                        var browserHeight = Math.Max(1, browserBottom - browserTop);

                        int returnX;
                        int returnY;
                        if (point.X <= left + 1)
                        {
                            returnX = browserLeft;
                            returnY = browserTop + (int)Math.Round(ny * browserHeight);
                        }
                        else if (point.X >= right - 1)
                        {
                            returnX = browserRight;
                            returnY = browserTop + (int)Math.Round(ny * browserHeight);
                        }
                        else if (point.Y <= top + 1)
                        {
                            returnX = browserLeft + (int)Math.Round(nx * browserWidth);
                            returnY = browserTop;
                        }
                        else
                        {
                            returnX = browserLeft + (int)Math.Round(nx * browserWidth);
                            returnY = browserBottom;
                        }

                        SetCursorPos(returnX, returnY);
                        return;
                    }
                    await Task.Delay(8, cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_portalSync)
                {
                    if (ReferenceEquals(_portalCancellation, cancellation))
                    {
                        _portalCancellation.Dispose();
                        _portalCancellation = null;
                    }
                }
            }
        });
    }

    private static void SendButton(int button, bool down)
    {
        var flags = (button, down) switch
        {
            (0, true) => MouseeventfLeftdown,
            (0, false) => MouseeventfLeftup,
            (1, true) => MouseeventfMiddledown,
            (1, false) => MouseeventfMiddleup,
            (2, true) => MouseeventfRightdown,
            (2, false) => MouseeventfRightup,
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Only left, middle and right mouse buttons are supported.")
        };
        SendMouse(flags, 0);
    }

    private static void SendMouse(uint flags, int data)
    {
        var input = new Input
        {
            type = InputMouse,
            union = new InputUnion
            {
                mouse = new MouseInput
                {
                    mouseData = unchecked((uint)data),
                    dwFlags = flags
                }
            }
        };

        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("Windows SendInput rejected Terminal mouse input.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }
}
