using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Server;

internal sealed record MonitorSnapshot(MonitorRecord Configuration, bool Installed, bool Connected, string? DeviceName, int Width, int Height, int RefreshRate);

internal sealed class MonitorApplicationService
{
    private readonly MonitorStore _store;
    private readonly LogStore _logStore;
    private readonly WindowsDisplayModeService _displayModes = new();
    private readonly WindowsDisplayConfigTopologyService _topology = new();
    private readonly WindowsAlphaReflowService _reflow = new();

    public MonitorApplicationService(MonitorStore store, LogStore logStore)
    {
        _store = store;
        _logStore = logStore;
    }

    public IReadOnlyList<MonitorSnapshot> List()
    {
        SynchronizeDiscoveredMonitors();
        var displays = ReadVirtualDisplays();
        return _store.List().Select(record => ToSnapshot(record, displays)).ToArray();
    }

    public MonitorSnapshot? Get(string vmuId)
    {
        SynchronizeDiscoveredMonitors();
        var record = _store.Get(vmuId);
        return record is null ? null : ToSnapshot(record, ReadVirtualDisplays());
    }

    public MonitorSnapshot UpdateProperties(
        string vmuId,
        string friendlyName,
        int width,
        int height,
        int refreshRate,
        bool portrait,
        RemoteAccessMode remoteAccess,
        bool passwordEnabled,
        string? password,
        bool apiKeyEnabled,
        bool regenerateApiKey,
        bool approvalEnabled)
    {
        if (width < 320 || height < 200) throw new ArgumentOutOfRangeException(nameof(width), "Monitor resolution is too small.");
        if (refreshRate is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(refreshRate), "Refresh rate is outside the supported configuration range.");

        var current = Get(vmuId) ?? throw new KeyNotFoundException($"Monitor '{vmuId}' was not found.");
        if (current.Connected && current.DeviceName is not null)
        {
            if (refreshRate != current.RefreshRate)
                throw new NotSupportedException("Changing refresh rate is not yet exposed by the validated VMU Core reflow path. The existing refresh rate has been preserved.");

            var targetWidth = portrait && width > height ? height : width;
            var targetHeight = portrait && width > height ? width : height;
            if (targetWidth != current.Width || targetHeight != current.Height)
                _reflow.SetMode(current.DeviceName, checked((uint)targetWidth), checked((uint)targetHeight));
        }

        var updated = _store.Update(vmuId, friendlyName, width, height, refreshRate, portrait, remoteAccess, passwordEnabled, password, apiKeyEnabled, regenerateApiKey, approvalEnabled);
        _logStore.Write("INFO", "VMU", "MONITOR_PROPERTIES", $"Properties updated for {updated.FriendlyName}", updated.VmuId);
        return Get(vmuId)!;
    }

    public MonitorSnapshot Connect(string vmuId)
    {
        var monitor = Get(vmuId) ?? throw new KeyNotFoundException($"Monitor '{vmuId}' was not found.");
        if (!monitor.Installed || string.IsNullOrWhiteSpace(monitor.DeviceName))
            throw new InvalidOperationException("This VMU monitor is not currently bound to an installed Windows virtual display.");
        if (monitor.Connected) return monitor;
        if (!_topology.HasSavedTopology(monitor.DeviceName))
            throw new InvalidOperationException("No saved CCD topology exists for this monitor. VMU will not invent a reconnect topology.");

        _topology.ReconnectSaved(monitor.DeviceName);
        _logStore.Write("INFO", "VMU", "MONITOR_CONNECT", $"{monitor.Configuration.FriendlyName} connected", monitor.Configuration.VmuId);
        return Get(vmuId)!;
    }

    public MonitorSnapshot Disconnect(string vmuId)
    {
        var monitor = Get(vmuId) ?? throw new KeyNotFoundException($"Monitor '{vmuId}' was not found.");
        if (!monitor.Installed || string.IsNullOrWhiteSpace(monitor.DeviceName))
            throw new InvalidOperationException("This VMU monitor is not currently bound to an installed Windows virtual display.");
        if (!monitor.Connected) return monitor;

        _topology.DisconnectExact(monitor.DeviceName);
        _logStore.Write("INFO", "VMU", "MONITOR_DISCONNECT", $"{monitor.Configuration.FriendlyName} disconnected", monitor.Configuration.VmuId);
        return Get(vmuId)!;
    }

    public void Uninstall(string vmuId)
    {
        var monitor = Get(vmuId) ?? throw new KeyNotFoundException($"Monitor '{vmuId}' was not found.");
        throw new NotSupportedException($"Uninstalling '{monitor.Configuration.FriendlyName}' is intentionally blocked because the validated Core API cannot yet remove one specific VDD target by vmu_id. VMU will not fall back to unstable Windows display numbers.");
    }

    private void SynchronizeDiscoveredMonitors()
    {
        foreach (var display in ReadVirtualDisplays().Values)
        {
            var mode = display.Mode;
            _store.EnsureForDevice(display.DeviceName, checked((int)(mode?.Width ?? 1920)), checked((int)(mode?.Height ?? 1080)), checked((int)(mode?.RefreshRate ?? 60)));
        }
    }

    private Dictionary<string, WindowsDisplayInfo> ReadVirtualDisplays() => _displayModes.GetDisplays()
        .Where(display => display.IsVirtual)
        .ToDictionary(display => display.DeviceName, StringComparer.OrdinalIgnoreCase);

    private static MonitorSnapshot ToSnapshot(MonitorRecord record, IReadOnlyDictionary<string, WindowsDisplayInfo> displays)
    {
        if (record.DeviceName is null || !displays.TryGetValue(record.DeviceName, out var display))
            return new MonitorSnapshot(record, false, false, record.DeviceName, record.Width, record.Height, record.RefreshRate);

        var mode = display.Mode;
        return new MonitorSnapshot(
            record,
            true,
            display.IsAttached,
            display.DeviceName,
            checked((int)(mode?.Width ?? record.Width)),
            checked((int)(mode?.Height ?? record.Height)),
            checked((int)(mode?.RefreshRate ?? record.RefreshRate)));
    }
}
