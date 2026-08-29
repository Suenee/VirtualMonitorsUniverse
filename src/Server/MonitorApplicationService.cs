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
    private readonly WindowsAlphaVddIdentityService _identity = new();
    private readonly WindowsVddNodeService _vddNodes = new();

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

    public MonitorSnapshot Create(string friendlyName, int width, int height, int refreshRate, bool portrait, bool connect)
    {
        if (width < 320 || height < 200) throw new ArgumentOutOfRangeException(nameof(width), "Monitor resolution is too small.");
        if (refreshRate is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(refreshRate), "Refresh rate is outside the supported configuration range.");

        var before = _vddNodes.GetInstanceIds();
        string? newInstanceId = null;
        using var payload = _vddNodes.PreparePayload();
        try
        {
            _vddNodes.InstallOne(payload);
            if (!WindowsVddNodeService.WaitUntil(() => _vddNodes.GetInstanceIds().Length == before.Length + 1, TimeSpan.FromSeconds(20)))
                throw new TimeoutException("The new VDD device node did not appear in Windows.");

            var after = _vddNodes.GetInstanceIds();
            var created = after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
            if (created.Length != 1)
                throw new InvalidOperationException($"Expected exactly one new VDD PnP identity, found {created.Length}.");
            newInstanceId = created[0];

            if (!WindowsVddNodeService.WaitUntil(() => TryResolveActive(newInstanceId, out _), TimeSpan.FromSeconds(20)))
                throw new TimeoutException($"The new VDD '{newInstanceId}' did not acquire an active CCD identity.");

            var identity = _identity.ResolveActive(newInstanceId);
            var targetWidth = portrait && width > height ? height : width;
            var targetHeight = portrait && width > height ? width : height;
            _reflow.SetMode(identity.GdiName, checked((uint)targetWidth), checked((uint)targetHeight));

            var display = ResolveDisplay(identity.GdiName);
            var actualRefresh = checked((int)(display.Mode?.RefreshRate ?? (uint)refreshRate));
            var record = _store.CreateBound(friendlyName, identity.GdiName, newInstanceId, targetWidth, targetHeight, actualRefresh, portrait);
            _logStore.Write("INFO", "VMU", "MONITOR_INSTALL", $"{record.FriendlyName} installed as {newInstanceId}", record.VmuId);

            if (!connect)
            {
                _topology.DisconnectExact(identity.GdiName);
                _logStore.Write("INFO", "VMU", "MONITOR_DISCONNECT", $"{record.FriendlyName} installed and left disconnected", record.VmuId);
            }

            return Get(record.VmuId)!;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(newInstanceId))
            {
                try { _vddNodes.RemoveOne(newInstanceId); } catch { }
            }
            throw;
        }
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
        if (string.IsNullOrWhiteSpace(monitor.Configuration.InstanceId))
            throw new InvalidOperationException("This monitor does not yet have a stable PnP instance identity. Restart VMU while the monitor is connected so the identity can be synchronized.");

        _vddNodes.RemoveOne(monitor.Configuration.InstanceId);
        _store.Delete(vmuId);
        _logStore.Write("INFO", "VMU", "MONITOR_UNINSTALL", $"{monitor.Configuration.FriendlyName} uninstalled", vmuId);
    }

    private void SynchronizeDiscoveredMonitors()
    {
        var instanceIds = _vddNodes.GetInstanceIds();
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instanceId in instanceIds)
        {
            if (TryResolveActive(instanceId, out var identity) && identity is not null)
                identities[identity.GdiName] = instanceId;
        }

        foreach (var display in ReadVirtualDisplays().Values)
        {
            var mode = display.Mode;
            identities.TryGetValue(display.DeviceName, out var instanceId);
            _store.EnsureForDevice(display.DeviceName, instanceId, checked((int)(mode?.Width ?? 1920u)), checked((int)(mode?.Height ?? 1080u)), checked((int)(mode?.RefreshRate ?? 60u)));
        }
    }

    private bool TryResolveActive(string instanceId, out WindowsVddIdentity? identity)
    {
        try
        {
            identity = _identity.ResolveActive(instanceId);
            return !string.IsNullOrWhiteSpace(identity.GdiName);
        }
        catch (InvalidOperationException)
        {
            identity = null;
            return false;
        }
    }

    private WindowsDisplayInfo ResolveDisplay(string deviceName) => _displayModes.GetDisplays()
        .FirstOrDefault(display => string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Windows display '{deviceName}' was not found.");

    private Dictionary<string, WindowsDisplayInfo> ReadVirtualDisplays() => _displayModes.GetDisplays()
        .Where(display => display.IsVirtual)
        .ToDictionary(display => display.DeviceName, StringComparer.OrdinalIgnoreCase);

    private static MonitorSnapshot ToSnapshot(MonitorRecord record, IReadOnlyDictionary<string, WindowsDisplayInfo> displays)
    {
        if (record.DeviceName is null || !displays.TryGetValue(record.DeviceName, out var display))
            return new MonitorSnapshot(record, !string.IsNullOrWhiteSpace(record.InstanceId), false, record.DeviceName, record.Width, record.Height, record.RefreshRate);

        var mode = display.Mode;
        return new MonitorSnapshot(
            record,
            true,
            display.IsAttached,
            display.DeviceName,
            checked((int)(mode?.Width ?? (uint)record.Width)),
            checked((int)(mode?.Height ?? (uint)record.Height)),
            checked((int)(mode?.RefreshRate ?? (uint)record.RefreshRate)));
    }
}
