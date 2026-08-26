namespace VirtualMonitorsUniverse.Core;

public interface IVirtualMonitorService
{
    IReadOnlyList<VirtualMonitorInfo> GetMonitors();

    bool IsDriverAvailable(TimeSpan? timeout = null);

    void SetDisplayCount(int count, TimeSpan? timeout = null);

    bool WaitForConnectedCount(int expectedCount, TimeSpan timeout, TimeSpan? pollingInterval = null);
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
