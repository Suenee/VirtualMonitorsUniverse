using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Performs the small set of native input operations intentionally exposed by
/// the VMU Terminal. Browser coordinates are resolved by the caller against a
/// specific virtual display before entering this service.
/// </summary>
internal static class TerminalInputService
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyF11 = 0x7A;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint GaRoot = 2;

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;

    public static void EnterMouse(int displayX, int displayY, int displayWidth, int displayHeight, double normalizedX, double normalizedY, string? button)
    {
        ValidateDisplay(displayWidth, displayHeight);
        var x = displayX + (int)Math.Round(Math.Clamp(normalizedX, 0d, 1d) * Math.Max(0, displayWidth - 1));
        var y = displayY + (int)Math.Round(Math.Clamp(normalizedY, 0d, 1d) * Math.Max(0, displayHeight - 1));
        if (!SetCursorPos(x, y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not move the cursor to the virtual monitor.");

        if (string.IsNullOrWhiteSpace(button) || button.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;

        var (down, up) = button.ToLowerInvariant() switch
        {
            "left" => (MouseLeftDown, MouseLeftUp),
            "right" => (MouseRightDown, MouseRightUp),
            "middle" => (MouseMiddleDown, MouseMiddleUp),
            _ => throw new ArgumentException($"Unsupported mouse button '{button}'.", nameof(button))
        };

        SendMouseClick(down, up);
    }

    public static void PressF11(int displayX, int displayY, int displayWidth, int displayHeight)
    {
        ValidateDisplay(displayWidth, displayHeight);
        var target = FindTargetWindow(displayX, displayY, displayWidth, displayHeight);
        if (target == IntPtr.Zero)
            throw new InvalidOperationException("VMU could not find an application window on the target virtual monitor.");

        if (SetForegroundWindow(target))
        {
            Thread.Sleep(10);
            if (GetForegroundWindow() == target)
            {
                var inputs = new[]
                {
                    KeyboardInput(VirtualKeyF11, 0),
                    KeyboardInput(VirtualKeyF11, KeyEventKeyUp)
                };
                Send(inputs, "Windows could not send F11 to the Terminal target.");
                return;
            }
        }

        // Foreground activation can be rejected by Windows focus-stealing rules.
        // WM_KEY* is a safe fallback for ordinary desktop applications and avoids
        // sending F11 back into the browser that originated the forwarding hotkey.
        const int f11ScanCode = 0x57;
        var downParam = (nint)((f11ScanCode << 16) | 1);
        var upParam = (nint)((f11ScanCode << 16) | unchecked((int)0xC0000001));
        if (!PostMessage(target, WmKeyDown, (nint)VirtualKeyF11, downParam) ||
            !PostMessage(target, WmKeyUp, (nint)VirtualKeyF11, upParam))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not forward F11 to the Terminal target window.");
    }

    private static IntPtr FindTargetWindow(int displayX, int displayY, int displayWidth, int displayHeight)
    {
        var center = new Point { X = displayX + displayWidth / 2, Y = displayY + displayHeight / 2 };
        var centerWindow = GetAncestor(WindowFromPoint(center), GaRoot);
        if (IsUsableTarget(centerWindow) && Intersects(centerWindow, displayX, displayY, displayWidth, displayHeight))
            return centerWindow;

        IntPtr selected = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsUsableTarget(window) || !Intersects(window, displayX, displayY, displayWidth, displayHeight)) return true;
            selected = window;
            return false;
        }, IntPtr.Zero);
        return selected;
    }

    private static bool IsUsableTarget(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return false;
        var className = new StringBuilder(128);
        _ = GetClassName(window, className, className.Capacity);
        return className.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd");
    }

    private static bool Intersects(IntPtr window, int x, int y, int width, int height)
    {
        if (!GetWindowRect(window, out var rect)) return false;
        var overlapWidth = Math.Max(0, Math.Min(rect.Right, x + width) - Math.Max(rect.Left, x));
        var overlapHeight = Math.Max(0, Math.Min(rect.Bottom, y + height) - Math.Max(rect.Top, y));
        return overlapWidth > 0 && overlapHeight > 0;
    }

    private static void ValidateDisplay(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("The target display has invalid dimensions.");
    }

    private static void SendMouseClick(uint down, uint up)
    {
        var inputs = new[]
        {
            MouseInput(down),
            MouseInput(up)
        };
        Send(inputs, "Windows could not send the mouse click to the Terminal target.");
    }

    private static void Send(Input[] inputs, string message)
    {
        var expected = (uint)inputs.Length;
        var sent = SendInput(expected, inputs, Marshal.SizeOf<Input>());
        if (sent != expected)
            throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    private static Input MouseInput(uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInputData { Flags = flags }
        }
    };

    private static Input KeyboardInput(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData { VirtualKey = virtualKey, Flags = flags }
        }
    };

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, nint wParam, nint lParam);
}
