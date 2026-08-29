using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Applies local Terminal mouse input to the Windows desktop coordinates occupied
/// by a connected VMU monitor. The browser sends normalized monitor coordinates;
/// this service performs the final Windows coordinate translation and input call.
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

    public void Apply(MonitorSnapshot monitor, TerminalMouseRequest request)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Terminal mouse control is supported only on Windows.");
        if (!monitor.Connected || monitor.Health.IsError || monitor.PositionX is null || monitor.PositionY is null)
            throw new InvalidOperationException("The monitor must be connected and healthy before mouse input can be applied.");
        if (!monitor.Configuration.CollaborationMouse)
            throw new InvalidOperationException("Mouse collaboration is disabled for this monitor.");

        if (request.X is not null || request.Y is not null)
        {
            if (request.X is null || request.Y is null || request.X < 0 || request.X > 1 || request.Y < 0 || request.Y > 1)
                throw new ArgumentOutOfRangeException(nameof(request), "Mouse coordinates must be normalized values from 0 to 1.");

            var x = monitor.PositionX.Value + Math.Clamp((int)Math.Round(request.X.Value * Math.Max(0, monitor.Width - 1)), 0, Math.Max(0, monitor.Width - 1));
            var y = monitor.PositionY.Value + Math.Clamp((int)Math.Round(request.Y.Value * Math.Max(0, monitor.Height - 1)), 0, Math.Max(0, monitor.Height - 1));
            if (!SetCursorPos(x, y))
                throw new InvalidOperationException($"Windows rejected cursor positioning at {x},{y}.");
        }

        switch (request.Type.ToLowerInvariant())
        {
            case "move":
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

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
