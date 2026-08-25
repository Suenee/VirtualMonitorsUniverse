namespace VirtualMonitorsUniverse.Core;

public interface IVirtualMonitorService
{
    IReadOnlyList<VirtualMonitorInfo> GetMonitors();
}

public sealed record VirtualMonitorInfo(
    string Id,
    string? GdiName,
    string? PnpInstanceId,
    bool IsConnected,
    int Width,
    int Height,
    int X,
    int Y);
