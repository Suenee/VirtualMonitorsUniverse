namespace VirtualMonitorsUniverse.Core;

public interface IVirtualMonitorService
{
    IReadOnlyList<VirtualMonitorInfo> GetMonitors();

    VddDriverDiagnostics GetDriverDiagnostics(TimeSpan? pipeTimeout = null);

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

public sealed record VddDriverDiagnostics(
    bool DevicePresent,
    bool DeviceActive,
    bool PipeAvailable,
    string? GdiName,
    string? PnpInstanceId,
    string? FriendlyName,
    int StateFlags);
