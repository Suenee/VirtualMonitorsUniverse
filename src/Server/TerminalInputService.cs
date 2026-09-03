using System.ComponentModel;
using System.Runtime.InteropServices;

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

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;

    public static void EnterMouse(int displayX, int displayY, int displayWidth, int displayHeight, double normalizedX, double normalizedY, string? button)
    {
        if (displayWidth <= 0 || displayHeight <= 0)
            throw new InvalidOperationException("The target display has invalid dimensions.");

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

    public static void PressF11()
    {
        var inputs = new[]
        {
            KeyboardInput(VirtualKeyF11, 0),
            KeyboardInput(VirtualKeyF11, KeyEventKeyUp)
        };
        Send(inputs, "Windows could not send F11 to the Terminal target.");
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
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
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
}
