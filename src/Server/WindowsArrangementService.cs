namespace VirtualMonitorsUniverse.Server;

internal sealed record WindowsArrangementDisplay(int WindowsNumber, string DeviceName, int X, int Y, int Width, int Height, bool Primary);

/// <summary>
/// Provides the active Windows desktop arrangement. Windows does not expose the Settings
/// app's Identify label as a documented property; Screen enumeration is used as the
/// best-effort public ordering while GDI names remain diagnostic only.
/// </summary>
internal static class WindowsArrangementService
{
    public static IReadOnlyList<WindowsArrangementDisplay> GetActive()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return Screen.AllScreens.Select((screen,index)=>new WindowsArrangementDisplay(index+1,screen.DeviceName,screen.Bounds.X,screen.Bounds.Y,screen.Bounds.Width,screen.Bounds.Height,screen.Primary)).ToArray();
    }

    public static int? GetWindowsNumber(string? deviceName)
        => string.IsNullOrWhiteSpace(deviceName) ? null : GetActive().FirstOrDefault(x=>x.DeviceName.Equals(deviceName,StringComparison.OrdinalIgnoreCase))?.WindowsNumber;
}
