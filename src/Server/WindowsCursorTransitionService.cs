using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Server;

internal sealed record CursorPoint(int X, int Y);
internal sealed record CursorDisplayIdentity(
    int WindowsNumber,
    string DeviceName,
    bool IsVirtual,
    string? CName,
    string? VmuId);
internal sealed record CursorMoveObservation(
    CursorPoint? PreviousPosition,
    CursorPoint CurrentPosition,
    CursorDisplayIdentity? CurrentDisplay);
internal sealed record CursorDisplayTransition(
    CursorDisplayIdentity? PreviousDisplay,
    CursorDisplayIdentity? CurrentDisplay,
    CursorPoint PreviousPosition,
    CursorPoint CurrentPosition);

/// <summary>
/// Publishes cursor movement and display-boundary transitions from the Windows
/// low-level mouse hook. No timer or cursor polling is used: work happens only
/// when Windows reports a mouse-move event. The hook is installed only while at
/// least one Terminal live stream is active.
/// </summary>
internal sealed class WindowsCursorTransitionService
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const uint WmQuit = 0x0012;

    private readonly Func<IReadOnlyList<MonitorSnapshot>> _monitorProvider;
    private readonly LogStore _logStore;
    private readonly object _lifecycleGate = new();
    private readonly LowLevelMouseProc _hookProc;

    private DisplayBounds[] _displays = [];
    private Thread? _hookThread;
    private uint _hookThreadId;
    private int _leaseCount;
    private CursorPoint? _previousPosition;
    private CursorDisplayIdentity? _previousDisplay;

    public WindowsCursorTransitionService(Func<IReadOnlyList<MonitorSnapshot>> monitorProvider, LogStore logStore)
    {
        _monitorProvider = monitorProvider;
        _logStore = logStore;
        _hookProc = HookCallback;
    }

    /// <summary>
    /// Process-wide move observation used by the isolated Terminal mouse portal.
    /// The portal can therefore be removed without coupling browser input state to
    /// the normal display-transition logic.
    /// </summary>
    public static event Action<CursorMoveObservation>? MoveObserved;

    public event Action<CursorDisplayTransition>? Transition;

    public IDisposable Acquire()
    {
        lock (_lifecycleGate)
        {
            RefreshDisplayTopology();
            _leaseCount++;
            if (_leaseCount == 1) StartHook();
        }

        return new Lease(this);
    }

    public void RefreshDisplayTopology()
    {
        var monitors = _monitorProvider()
            .Where(x => !string.IsNullOrWhiteSpace(x.DeviceName))
            .ToDictionary(x => x.DeviceName!, StringComparer.OrdinalIgnoreCase);

        _displays = WindowsArrangementService.GetActive()
            .Select(display =>
            {
                monitors.TryGetValue(display.DeviceName, out var monitor);
                return new DisplayBounds(
                    display.X,
                    display.Y,
                    display.X + display.Width,
                    display.Y + display.Height,
                    new CursorDisplayIdentity(
                        display.WindowsNumber,
                        display.DeviceName,
                        monitor is not null,
                        monitor?.Configuration.Name,
                        monitor?.Configuration.VmuId));
            })
            .ToArray();
    }

    private void StartHook()
    {
        if (!OperatingSystem.IsWindows() || _hookThread is not null) return;

        var started = new ManualResetEventSlim(false);
        _hookThread = new Thread(() => HookThreadMain(started))
        {
            IsBackground = true,
            Name = "VMU cursor transition hook"
        };
        _hookThread.Start();
        started.Wait(TimeSpan.FromSeconds(2));
    }

    private void HookThreadMain(ManualResetEventSlim started)
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            _hookThreadId = GetCurrentThreadId();
            hook = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _logStore.Write("WARN", "WEB", "CURSOR_HOOK_FAILED", $"Windows cursor hook could not be installed (Win32 {error}).");
                return;
            }

            started.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _logStore.Write("WARN", "WEB", "CURSOR_HOOK_FAILED", ex.Message);
        }
        finally
        {
            started.Set();
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            lock (_lifecycleGate)
            {
                _hookThread = null;
                _hookThreadId = 0;
                _previousPosition = null;
                _previousDisplay = null;
            }
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == WmMouseMove)
        {
            try
            {
                var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                ProcessMove(new CursorPoint(mouse.Point.X, mouse.Point.Y));
            }
            catch
            {
                // A mouse hook must never propagate exceptions into User32.
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void ProcessMove(CursorPoint currentPosition)
    {
        var currentDisplay = ResolveDisplay(currentPosition);
        var previousPosition = _previousPosition;
        var previousDisplay = _previousDisplay;
        _previousPosition = currentPosition;
        _previousDisplay = currentDisplay;

        try
        {
            MoveObserved?.Invoke(new CursorMoveObservation(previousPosition, currentPosition, currentDisplay));
        }
        catch
        {
            // Observers run from a low-level hook and must never disturb User32.
        }

        if (previousPosition is null) return;
        if (SameDisplay(previousDisplay, currentDisplay)) return;

        Transition?.Invoke(new CursorDisplayTransition(
            previousDisplay,
            currentDisplay,
            previousPosition,
            currentPosition));
    }

    private CursorDisplayIdentity? ResolveDisplay(CursorPoint point)
    {
        var displays = _displays;
        for (var index = 0; index < displays.Length; index++)
        {
            var display = displays[index];
            if (point.X >= display.Left && point.X < display.Right && point.Y >= display.Top && point.Y < display.Bottom)
                return display.Identity;
        }

        return null;
    }

    private static bool SameDisplay(CursorDisplayIdentity? left, CursorDisplayIdentity? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.DeviceName.Equals(right.DeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private void Release()
    {
        uint threadId = 0;
        lock (_lifecycleGate)
        {
            if (_leaseCount > 0) _leaseCount--;
            if (_leaseCount == 0) threadId = _hookThreadId;
        }

        if (threadId != 0) PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    private sealed class Lease(WindowsCursorTransitionService owner) : IDisposable
    {
        private WindowsCursorTransitionService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed record DisplayBounds(int Left, int Top, int Right, int Bottom, CursorDisplayIdentity Identity);

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minFilter, uint maxFilter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
