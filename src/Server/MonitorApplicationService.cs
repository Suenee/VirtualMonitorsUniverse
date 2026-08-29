using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Server;

internal sealed record MonitorSnapshot(
    MonitorRecord Configuration,
    bool Installed,
    bool Connected,
    string? DeviceName,
    int Width,
    int Height,
    int RefreshRate,
    int? WindowsDisplay,
    int? PositionX,
    int? PositionY);

internal sealed class MonitorApplicationService
{
    public static readonly int[] SupportedRefreshRates = [60, 75, 90, 120, 144, 165, 240];
    public const int RecommendedRefreshRate = 60;

    private readonly MonitorStore _store;
    private readonly LogStore _logStore;
    private readonly string _dataRoot;
    private readonly WindowsDisplayModeService _displayModes = new();
    private readonly WindowsDisplayConfigTopologyService _topology = new();
    private readonly WindowsAlphaReflowService _reflow = new();
    private readonly WindowsAlphaVddIdentityService _identity = new();
    private readonly WindowsVddNodeService _vddNodes = new();

    public MonitorApplicationService(MonitorStore store, LogStore logStore, string dataRoot)
    {
        _store = store;
        _logStore = logStore;
        _dataRoot = dataRoot;
    }

    public IReadOnlyList<MonitorSnapshot> List()
    {
        SynchronizeDiscoveredMonitors();
        var displays = ReadVirtualDisplays();
        return _store.List().Select(record => ToSnapshot(record, displays)).ToArray();
    }

    public MonitorSnapshot? Get(string idOrName)
    {
        SynchronizeDiscoveredMonitors();
        var record = _store.Get(idOrName);
        return record is null ? null : ToSnapshot(record, ReadVirtualDisplays());
    }

    public bool NameAvailable(string name, string? except = null)
    {
        try
        {
            name = MonitorStore.NormalizeCanonical(name);
            var exceptId = string.IsNullOrWhiteSpace(except) ? null : _store.Get(except)?.VmuId;
            return !_store.NameExists(name, exceptId);
        }
        catch { return false; }
    }

    public (string Name, string Title) SuggestIdentity(string? name, string? title) => _store.NormalizeIdentity(name, title);

    public MonitorSnapshot Create(string? name, string? title, int width, int height, int refreshRate, bool portrait, string? avatarAnimal)
    {
        ValidateMode(width, height, refreshRate);
        var requestedIdentity = _store.NormalizeIdentity(name, title);
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
            if (created.Length != 1) throw new InvalidOperationException($"Expected exactly one new VDD PnP identity, found {created.Length}.");
            newInstanceId = created[0];

            if (!WindowsVddNodeService.WaitUntil(() => TryResolveActive(newInstanceId, out _), TimeSpan.FromSeconds(20)))
                throw new TimeoutException($"The new VDD '{newInstanceId}' did not acquire an active CCD identity.");

            var identity = _identity.ResolveActive(newInstanceId);
            var (targetWidth, targetHeight) = NormalizeOrientation(width, height, portrait);
            _reflow.SetMode(identity.GdiName, checked((uint)targetWidth), checked((uint)targetHeight));
            _displayModes.SetMode(identity.GdiName, checked((uint)targetWidth), checked((uint)targetHeight), checked((uint)refreshRate));

            var display = ResolveDisplay(identity.GdiName);
            var actualRefresh = checked((int)(display.Mode?.RefreshRate ?? (uint)refreshRate));
            if (Math.Abs(actualRefresh - refreshRate) > 1)
                throw new InvalidOperationException($"Requested {refreshRate} Hz, but Windows reports {actualRefresh} Hz for the new monitor.");

            var discovered = _store.EnsureForDevice(identity.GdiName, newInstanceId, targetWidth, targetHeight, actualRefresh);
            var configured = _store.ApplyCreationIdentity(discovered.VmuId, requestedIdentity.Name, requestedIdentity.Title, avatarAnimal);
            _store.Update(configured.VmuId, configured.Name, configured.Title, targetWidth, targetHeight, actualRefresh, portrait, RemoteAccessMode.Disabled, RemoteSecurityMode.Public, null, false, true, true, true);
            _logStore.Write("INFO", "VMU", "MONITOR_INSTALL", $"{configured.Title} installed as {newInstanceId}", configured.VmuId);

            // Installation deliberately leaves the monitor disconnected. Connection is a separate explicit action.
            _topology.DisconnectExact(identity.GdiName);
            _logStore.Write("INFO", "VMU", "MONITOR_DISCONNECT", $"{configured.Title} installed and left disconnected", configured.VmuId);
            return Get(configured.VmuId)!;
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
        string idOrName, string? name, string? title, int width, int height, int refreshRate, bool portrait,
        RemoteAccessMode remoteAccess, RemoteSecurityMode securityMode, string? password, bool regenerateApiKey,
        bool collaborationClipboard, bool collaborationMouse, bool collaborationKeyboard)
    {
        ValidateMode(width, height, refreshRate);
        var current = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        var (targetWidth, targetHeight) = NormalizeOrientation(width, height, portrait);
        if (current.Connected && current.DeviceName is not null)
        {
            if (targetWidth != current.Width || targetHeight != current.Height)
                _reflow.SetMode(current.DeviceName, checked((uint)targetWidth), checked((uint)targetHeight));
            if (refreshRate != current.RefreshRate || targetWidth != current.Width || targetHeight != current.Height)
                _displayModes.SetMode(current.DeviceName, checked((uint)targetWidth), checked((uint)targetHeight), checked((uint)refreshRate));
        }

        var updated = _store.Update(current.Configuration.VmuId, name, title, targetWidth, targetHeight, refreshRate, portrait, remoteAccess, securityMode, password, regenerateApiKey,
            collaborationClipboard, collaborationMouse, collaborationKeyboard);
        _logStore.Write("INFO", "VMU", "MONITOR_PROPERTIES", $"Properties updated for {updated.Title}", updated.VmuId);
        return Get(updated.VmuId)!;
    }

    public MonitorSnapshot SetAnimalAvatar(string idOrName, string animal) => Get(_store.SetAnimalAvatar(idOrName, animal).VmuId)!;

    public MonitorSnapshot SetCustomAvatar(string idOrName, string fileName, Stream content)
    {
        var monitor = _store.Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        var stored = MonitorAvatarService.SaveCustom(_dataRoot, monitor.VmuId, fileName, content);
        return Get(_store.SetCustomAvatar(monitor.VmuId, stored).VmuId)!;
    }

    public (byte[] Bytes, string ContentType)? GetAvatar(string idOrName)
    {
        var monitor = _store.Get(idOrName);
        if (monitor is null || !monitor.AvatarKind.Equals("custom", StringComparison.OrdinalIgnoreCase)) return null;
        var bytes = MonitorAvatarService.ReadCustom(_dataRoot, monitor.AvatarValue);
        if (bytes is null) return null;
        var ext = Path.GetExtension(monitor.AvatarValue).ToLowerInvariant();
        return (bytes, ext == ".gif" ? "image/gif" : ext == ".ico" ? "image/x-icon" : "image/png");
    }

    public IReadOnlyList<MonitorAccessRule> ListAccessRules(string idOrName) => _store.ListAccessRules(idOrName);
    public MonitorAccessRule UpsertAccessRule(string idOrName, string clientId, string? ip, string? mac, string? computer, string? user, AccessPermission permission)
        => _store.UpsertAccessRule(idOrName, clientId, ip, mac, computer, user, permission);
    public void DeleteAccessRule(string idOrName, long ruleId) => _store.DeleteAccessRule(idOrName, ruleId);

    public MonitorSnapshot Connect(string idOrName)
    {
        var monitor = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        if (!monitor.Installed || string.IsNullOrWhiteSpace(monitor.DeviceName)) throw new InvalidOperationException("This VMU monitor is not currently bound to an installed Windows virtual display.");
        if (monitor.Connected) return monitor;
        if (!_topology.HasSavedTopology(monitor.DeviceName)) throw new InvalidOperationException("No saved CCD topology exists for this monitor. VMU will not invent a reconnect topology.");
        _topology.ReconnectSaved(monitor.DeviceName);
        _logStore.Write("INFO", "VMU", "MONITOR_CONNECT", $"{monitor.Configuration.Title} connected", monitor.Configuration.VmuId);
        return Get(monitor.Configuration.VmuId)!;
    }

    public MonitorSnapshot Disconnect(string idOrName)
    {
        var monitor = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        if (!monitor.Installed || string.IsNullOrWhiteSpace(monitor.DeviceName)) throw new InvalidOperationException("This VMU monitor is not currently bound to an installed Windows virtual display.");
        if (!monitor.Connected) return monitor;
        _topology.DisconnectExact(monitor.DeviceName);
        _logStore.Write("INFO", "VMU", "MONITOR_DISCONNECT", $"{monitor.Configuration.Title} disconnected", monitor.Configuration.VmuId);
        return Get(monitor.Configuration.VmuId)!;
    }

    public void Uninstall(string idOrName)
    {
        var monitor = Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        if (string.IsNullOrWhiteSpace(monitor.Configuration.InstanceId)) throw new InvalidOperationException("This monitor does not yet have a stable PnP instance identity.");
        _vddNodes.RemoveOne(monitor.Configuration.InstanceId);
        _store.Delete(monitor.Configuration.VmuId);
        _logStore.Write("INFO", "VMU", "MONITOR_UNINSTALL", $"{monitor.Configuration.Title} uninstalled", monitor.Configuration.VmuId);
    }

    private void SynchronizeDiscoveredMonitors()
    {
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instanceId in _vddNodes.GetInstanceIds())
            if (TryResolveActive(instanceId, out var identity) && identity is not null) identities[identity.GdiName] = instanceId;

        foreach (var display in ReadVirtualDisplays().Values)
        {
            identities.TryGetValue(display.DeviceName, out var instanceId);
            var mode = display.Mode;
            _store.EnsureForDevice(display.DeviceName, instanceId, checked((int)(mode?.Width ?? 1920u)), checked((int)(mode?.Height ?? 1080u)), checked((int)(mode?.RefreshRate ?? 60u)));
        }
    }

    private bool TryResolveActive(string instanceId, out WindowsVddIdentity? identity)
    {
        try { identity = _identity.ResolveActive(instanceId); return !string.IsNullOrWhiteSpace(identity.GdiName); }
        catch (InvalidOperationException) { identity = null; return false; }
    }

    private WindowsDisplayInfo ResolveDisplay(string deviceName) => _displayModes.GetDisplays().FirstOrDefault(display => string.Equals(display.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Windows display '{deviceName}' was not found.");

    private Dictionary<string, WindowsDisplayInfo> ReadVirtualDisplays() => _displayModes.GetDisplays().Where(display => display.IsVirtual).ToDictionary(display => display.DeviceName, StringComparer.OrdinalIgnoreCase);

    private static MonitorSnapshot ToSnapshot(MonitorRecord record, IReadOnlyDictionary<string, WindowsDisplayInfo> displays)
    {
        if (record.DeviceName is null || !displays.TryGetValue(record.DeviceName, out var display))
            return new MonitorSnapshot(record, !string.IsNullOrWhiteSpace(record.InstanceId), false, record.DeviceName, record.Width, record.Height, record.RefreshRate, ParseDisplayNumber(record.DeviceName), null, null);
        var mode = display.Mode;
        return new MonitorSnapshot(record, true, display.IsAttached, display.DeviceName,
            checked((int)(mode?.Width ?? (uint)record.Width)), checked((int)(mode?.Height ?? (uint)record.Height)), checked((int)(mode?.RefreshRate ?? (uint)record.RefreshRate)),
            ParseDisplayNumber(display.DeviceName), mode?.X, mode?.Y);
    }

    private static int? ParseDisplayNumber(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        var marker = "DISPLAY";
        var index = deviceName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && int.TryParse(deviceName[(index + marker.Length)..], out var number) ? number : null;
    }

    private static (int Width, int Height) NormalizeOrientation(int width, int height, bool portrait)
        => portrait && width > height ? (height, width) : (!portrait && height > width ? (height, width) : (width, height));

    private static void ValidateMode(int width, int height, int refreshRate)
    {
        if (width < 320 || height < 200) throw new ArgumentOutOfRangeException(nameof(width), "Monitor resolution is too small.");
        if (!SupportedRefreshRates.Contains(refreshRate)) throw new ArgumentOutOfRangeException(nameof(refreshRate), $"Refresh rate must be one of: {string.Join(", ", SupportedRefreshRates)} Hz.");
    }
}
