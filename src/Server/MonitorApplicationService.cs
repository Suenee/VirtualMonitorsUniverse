using System.Text.Json;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Server;

internal sealed record MonitorHealth(string State, string Message, DateTime? Timestamp, bool IsError);
internal sealed record MonitorSnapshot(MonitorRecord Configuration, bool Installed, bool Connected, string? DeviceName, int Width, int Height, int RefreshRate, int? WindowsDisplay, int? PositionX, int? PositionY, MonitorHealth Health);

internal sealed class MonitorApplicationService
{
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(2);

    public static readonly int[] SupportedRefreshRates = [60, 75, 90, 120, 144, 165, 240];
    public const int RecommendedRefreshRate = 60;

    private readonly MonitorStore _store;
    private readonly LogStore _logStore;
    private readonly string _dataRoot;
    private readonly MonitorOrderService _order;
    private readonly WindowsDisplayModeService _displayModes = new();
    private readonly WindowsDisplayConfigTopologyService _topology = new();
    private readonly WindowsAlphaReflowService _reflow = new();
    private readonly WindowsAlphaVddIdentityService _identity = new();
    private readonly WindowsVddNodeService _vddNodes = new();
    private readonly object _snapshotRefreshGate = new();

    private MonitorSnapshot[] _snapshot = [];
    private long _snapshotUtcTicks;
    private int _refreshScheduled;

    public MonitorApplicationService(MonitorStore store, LogStore logStore, string dataRoot)
    {
        _store = store;
        _logStore = logStore;
        _dataRoot = dataRoot;
        _order = new MonitorOrderService(dataRoot);

        // Warm the Windows/PnP state outside the request path. Until this finishes,
        // List/Get can still construct the first snapshot synchronously if needed.
        ScheduleSnapshotRefresh();
    }

    public string DataRoot => _dataRoot;

    public IReadOnlyList<MonitorSnapshot> List()
    {
        var current = _snapshot;
        if (current.Length == 0)
            return RefreshSnapshot();

        var refreshedTicks = Interlocked.Read(ref _snapshotUtcTicks);
        if (refreshedTicks == 0 || DateTime.UtcNow - new DateTime(refreshedTicks, DateTimeKind.Utc) >= SnapshotLifetime)
            ScheduleSnapshotRefresh();

        return current;
    }

    public MonitorSnapshot? Get(string idOrName)
    {
        return List().FirstOrDefault(x =>
            x.Configuration.VmuId.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
            x.Configuration.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public bool NameAvailable(string name, string? except = null)
    {
        try
        {
            name = MonitorStore.NormalizeCanonical(name);
            var exceptId = string.IsNullOrWhiteSpace(except) ? null : _store.Get(except)?.VmuId;
            return !_store.NameExists(name, exceptId);
        }
        catch
        {
            return false;
        }
    }

    public (string Name, string Title) SuggestIdentity(string? name, string? title) => _store.NormalizeIdentity(name, title);

    public void Reorder(IReadOnlyList<string> ids)
    {
        _order.Save(ids, _store.List());
        ScheduleSnapshotRefresh();
    }

    public MonitorSnapshot Create(string? name, string? title, int width, int height, int refreshRate, bool portrait, string? avatarAnimal)
    {
        ValidateMode(width, height, refreshRate);
        var requested = _store.NormalizeIdentity(name, title);
        var before = _vddNodes.GetInstanceIds();
        string? instance = null;
        string? createdVmuId = null;

        void Stage(string evt, string message) => _logStore.Write("INFO", "VMU", evt, message, createdVmuId);

        Stage("MONITOR_INSTALL_START", $"Installing monitor '{requested.Title}' ({requested.Name}) at {width}x{height}@{refreshRate} Hz.");
        using var payload = _vddNodes.PreparePayload();
        try
        {
            Stage("MONITOR_INSTALL_PAYLOAD", "VDD payload prepared and verified.");
            _vddNodes.InstallOne(payload);
            Stage("MONITOR_INSTALL_NODE", "VDD device-node installation command completed.");
            if (!WindowsVddNodeService.WaitUntil(() => _vddNodes.GetInstanceIds().Length == before.Length + 1, TimeSpan.FromSeconds(20)))
                throw new TimeoutException("The new VDD device node did not appear in Windows.");

            var created = _vddNodes.GetInstanceIds().Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
            if (created.Length != 1)
                throw new InvalidOperationException($"Expected exactly one new VDD PnP identity, found {created.Length}.");

            instance = created[0];
            Stage("MONITOR_INSTALL_IDENTITY", $"New VDD identity: {instance}.");
            if (!WindowsVddNodeService.WaitUntil(() => TryResolveActive(instance, out _), TimeSpan.FromSeconds(20)))
                throw new TimeoutException($"The new VDD '{instance}' did not acquire an active display identity.");

            var identity = _identity.ResolveActive(instance);
            Stage("MONITOR_INSTALL_DISPLAY", $"Windows display resolved as {identity.GdiName}.");
            var (targetWidth, targetHeight) = NormalizeOrientation(width, height, portrait);
            _reflow.SetMode(identity.GdiName, checked((uint)targetWidth), checked((uint)targetHeight));
            _displayModes.SetMode(identity.GdiName, checked((uint)targetWidth), checked((uint)targetHeight), checked((uint)refreshRate));
            var display = ResolveDisplay(identity.GdiName);
            var actual = checked((int)(display.Mode?.RefreshRate ?? (uint)refreshRate));
            if (Math.Abs(actual - refreshRate) > 1)
                throw new InvalidOperationException($"Requested {refreshRate} Hz, but Windows reports {actual} Hz for the new monitor.");

            Stage("MONITOR_INSTALL_MODE", $"Mode applied: {targetWidth}x{targetHeight}@{actual} Hz.");
            var discovered = _store.EnsureForDevice(identity.GdiName, instance, targetWidth, targetHeight, actual);
            createdVmuId = discovered.VmuId;
            var configured = _store.ApplyCreationIdentity(discovered.VmuId, requested.Name, requested.Title, avatarAnimal);
            _store.Update(configured.VmuId, configured.Name, configured.Title, targetWidth, targetHeight, actual, portrait, RemoteAccessMode.Disabled, RemoteSecurityMode.Public, null, false, true, true, true);
            Stage("MONITOR_INSTALL_DATABASE", $"VMU identity committed as {configured.Name} ({configured.VmuId}).");
            DisconnectWithFallback(identity.GdiName, configured.VmuId, configured.Title);
            Stage("MONITOR_INSTALL_COMPLETE", $"{configured.Title} installed successfully and left disconnected.");
            return GetFresh(configured.VmuId)!;
        }
        catch (Exception ex)
        {
            _logStore.Write("ERROR", "VMU", "MONITOR_INSTALL_FAILED", $"Installation of '{requested.Title}' failed: {ex.Message}", createdVmuId, JsonSerializer.Serialize(new { requested.Name, requested.Title, instance, exception = ex.ToString() }));
            if (createdVmuId is not null)
            {
                try { _store.Delete(createdVmuId); }
                catch (Exception rollback) { _logStore.Write("ERROR", "VMU", "MONITOR_ROLLBACK_DB_FAILED", rollback.Message, createdVmuId); }
            }
            if (instance is not null)
            {
                try
                {
                    _vddNodes.RemoveOne(instance);
                    _logStore.Write("INFO", "VMU", "MONITOR_ROLLBACK_NODE", $"Rolled back VDD node {instance}.");
                }
                catch (Exception rollback)
                {
                    _logStore.Write("ERROR", "VMU", "MONITOR_ROLLBACK_NODE_FAILED", rollback.Message, detailsJson: JsonSerializer.Serialize(new { instance, exception = rollback.ToString() }));
                }
            }
            ScheduleSnapshotRefresh();
            throw;
        }
    }

    public MonitorSnapshot UpdateProperties(string idOrName, string? name, string? title, int width, int height, int refreshRate, bool portrait, RemoteAccessMode remoteAccess, RemoteSecurityMode securityMode, string? password, bool regenerateApiKey, bool collaborationClipboard, bool collaborationMouse, bool collaborationKeyboard)
    {
        ValidateMode(width, height, refreshRate);
        var current = GetFresh(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        var (targetWidth, targetHeight) = NormalizeOrientation(width, height, portrait);
        if (current.Connected && current.DeviceName is not null)
        {
            if (targetWidth != current.Width || targetHeight != current.Height)
                _reflow.SetMode(current.DeviceName, checked((uint)targetWidth), checked((uint)targetHeight));
            if (refreshRate != current.RefreshRate || targetWidth != current.Width || targetHeight != current.Height)
                _displayModes.SetMode(current.DeviceName, checked((uint)targetWidth), checked((uint)targetHeight), checked((uint)refreshRate));
        }

        var updated = _store.Update(current.Configuration.VmuId, name, title, targetWidth, targetHeight, refreshRate, portrait, remoteAccess, securityMode, password, regenerateApiKey, collaborationClipboard, collaborationMouse, collaborationKeyboard);
        _logStore.Write("INFO", "VMU", "MONITOR_PROPERTIES", $"Properties updated for {updated.Title}", updated.VmuId);
        return GetFresh(updated.VmuId)!;
    }

    public MonitorSnapshot SetAnimalAvatar(string idOrName, string animal)
    {
        var before = _store.Get(idOrName) ?? throw new KeyNotFoundException();
        var result = _store.SetAnimalAvatar(idOrName, animal);
        if (before.AvatarKind.Equals("custom", StringComparison.OrdinalIgnoreCase))
            MonitorAvatarService.DeleteCustom(_dataRoot, before.VmuId);
        return GetFresh(result.VmuId)!;
    }

    public MonitorSnapshot SetCustomAvatar(string idOrName, string fileName, Stream content)
    {
        var monitor = _store.Get(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        var stored = MonitorAvatarService.SaveCustom(_dataRoot, monitor.VmuId, fileName, content);
        return GetFresh(_store.SetCustomAvatar(monitor.VmuId, stored).VmuId)!;
    }

    public (byte[] Bytes, string ContentType)? GetAvatar(string idOrName)
    {
        var monitor = _store.Get(idOrName);
        if (monitor is null || !monitor.AvatarKind.Equals("custom", StringComparison.OrdinalIgnoreCase)) return null;
        var bytes = MonitorAvatarService.ReadCustom(_dataRoot, monitor.AvatarValue);
        if (bytes is null) return null;
        var extension = Path.GetExtension(monitor.AvatarValue).ToLowerInvariant();
        return (bytes, extension == ".gif" ? "image/gif" : extension == ".ico" ? "image/x-icon" : "image/png");
    }

    public IReadOnlyList<MonitorAccessRule> ListAccessRules(string idOrName) => _store.ListAccessRules(idOrName);
    public MonitorAccessRule UpsertAccessRule(string idOrName, string clientId, string? ip, string? mac, string? computer, string? user, AccessPermission permission) => _store.UpsertAccessRule(idOrName, clientId, ip, mac, computer, user, permission);
    public void DeleteAccessRule(string idOrName, long ruleId) => _store.DeleteAccessRule(idOrName, ruleId);

    public MonitorSnapshot Connect(string idOrName)
    {
        var monitor = GetFresh(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        if (!monitor.Installed || string.IsNullOrWhiteSpace(monitor.DeviceName))
            throw new InvalidOperationException("This VMU monitor is not currently bound to an installed Windows virtual display.");
        if (monitor.Connected) return monitor;

        try
        {
            if (_topology.HasSavedTopology(monitor.DeviceName)) _topology.ReconnectSaved(monitor.DeviceName);
            else _displayModes.Reconnect(monitor.DeviceName);
        }
        catch (Exception ex)
        {
            _logStore.Write("WARN", "VMU", "MONITOR_CONNECT_CCD_FALLBACK", $"CCD reconnect failed; trying validated DEVMODE fallback: {ex.Message}", monitor.Configuration.VmuId);
            _displayModes.Reconnect(monitor.DeviceName);
        }

        _logStore.Write("INFO", "VMU", "MONITOR_CONNECT", $"{monitor.Configuration.Title} connected", monitor.Configuration.VmuId);
        return GetFresh(monitor.Configuration.VmuId)!;
    }

    public MonitorSnapshot Disconnect(string idOrName)
    {
        var monitor = GetFresh(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        if (!monitor.Installed || string.IsNullOrWhiteSpace(monitor.DeviceName))
            throw new InvalidOperationException("This VMU monitor is not currently bound to an installed Windows virtual display.");
        if (!monitor.Connected) return monitor;

        DisconnectWithFallback(monitor.DeviceName, monitor.Configuration.VmuId, monitor.Configuration.Title);
        return GetFresh(monitor.Configuration.VmuId)!;
    }

    private void DisconnectWithFallback(string device, string vmuId, string title)
    {
        try { _topology.DisconnectExact(device); }
        catch (Exception ex)
        {
            _logStore.Write("WARN", "VMU", "MONITOR_DISCONNECT_CCD_FALLBACK", $"CCD disconnect failed; using validated DEVMODE fallback: {ex.Message}", vmuId);
            _displayModes.Disconnect(device);
        }
        _logStore.Write("INFO", "VMU", "MONITOR_DISCONNECT", $"{title} disconnected", vmuId);
    }

    public void Uninstall(string idOrName)
    {
        var monitor = GetFresh(idOrName) ?? throw new KeyNotFoundException($"Monitor '{idOrName}' was not found.");
        if (monitor.Connected) throw new InvalidOperationException("Disconnect the monitor before uninstalling it.");
        if (string.IsNullOrWhiteSpace(monitor.Configuration.InstanceId))
            throw new InvalidOperationException("This monitor does not yet have a stable PnP instance identity.");

        _vddNodes.RemoveOne(monitor.Configuration.InstanceId);
        _store.Delete(monitor.Configuration.VmuId);
        MonitorAvatarService.DeleteCustom(_dataRoot, monitor.Configuration.VmuId);
        _logStore.Write("INFO", "VMU", "MONITOR_UNINSTALL", $"{monitor.Configuration.Title} uninstalled", monitor.Configuration.VmuId);
        ScheduleSnapshotRefresh();
    }

    private MonitorSnapshot? GetFresh(string idOrName)
    {
        return RefreshSnapshot().FirstOrDefault(x =>
            x.Configuration.VmuId.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
            x.Configuration.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
    }

    private MonitorSnapshot[] RefreshSnapshot()
    {
        lock (_snapshotRefreshGate)
        {
            SynchronizeDiscoveredMonitors();
            var displays = ReadVirtualDisplays();
            var refreshed = _order.Apply(_store.List()).Select(record => ToSnapshot(record, displays)).ToArray();
            _snapshot = refreshed;
            Interlocked.Exchange(ref _snapshotUtcTicks, DateTime.UtcNow.Ticks);
            return refreshed;
        }
    }

    private void ScheduleSnapshotRefresh()
    {
        if (Interlocked.Exchange(ref _refreshScheduled, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { RefreshSnapshot(); }
            catch
            {
                // Keep the last known-good snapshot. Web requests must stay responsive
                // even when Windows/PnP discovery is temporarily unavailable.
            }
            finally
            {
                Interlocked.Exchange(ref _refreshScheduled, 0);
            }
        });
    }

    private void SynchronizeDiscoveredMonitors()
    {
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in _vddNodes.GetInstanceIds())
        {
            if (TryResolveActive(instance, out var identity) && identity is not null)
                identities[identity.GdiName] = instance;
        }

        foreach (var display in ReadVirtualDisplays().Values)
        {
            identities.TryGetValue(display.DeviceName, out var instance);
            var mode = display.Mode;
            _store.EnsureForDevice(
                display.DeviceName,
                instance,
                checked((int)(mode?.Width ?? 1920u)),
                checked((int)(mode?.Height ?? 1080u)),
                checked((int)(mode?.RefreshRate ?? 60u)));
        }
    }

    private bool TryResolveActive(string instance, out WindowsVddIdentity? identity)
    {
        try
        {
            identity = _identity.ResolveActive(instance);
            return !string.IsNullOrWhiteSpace(identity.GdiName);
        }
        catch (InvalidOperationException)
        {
            identity = null;
            return false;
        }
    }

    private WindowsDisplayInfo ResolveDisplay(string device) =>
        _displayModes.GetDisplays().FirstOrDefault(x => string.Equals(x.DeviceName, device, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Windows display '{device}' was not found.");

    private Dictionary<string, WindowsDisplayInfo> ReadVirtualDisplays() =>
        _displayModes.GetDisplays().Where(x => x.IsVirtual).ToDictionary(x => x.DeviceName, StringComparer.OrdinalIgnoreCase);

    private MonitorSnapshot ToSnapshot(MonitorRecord record, IReadOnlyDictionary<string, WindowsDisplayInfo> displays)
    {
        var latest = _logStore.ReadLatestForMonitor(record.VmuId);

        MonitorHealth Health(bool installed, bool connected)
        {
            if (latest?.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
                return new("Error", latest.Message, latest.Timestamp, true);
            if (!installed)
                return new("Not Installed", latest?.Message ?? "Windows virtual display is not installed.", latest?.Timestamp, false);
            if (!connected)
                return new("Disconnected", latest?.Message ?? "Monitor is installed but disconnected.", latest?.Timestamp, false);
            return new("Healthy", latest?.Message ?? "Monitor is connected and healthy.", latest?.Timestamp, false);
        }

        if (record.DeviceName is null || !displays.TryGetValue(record.DeviceName, out var display))
        {
            var installed = !string.IsNullOrWhiteSpace(record.InstanceId);
            return new(record, installed, false, record.DeviceName, record.Width, record.Height, record.RefreshRate, WindowsArrangementService.GetWindowsNumber(record.DeviceName), null, null, Health(installed, false));
        }

        var mode = display.Mode;
        return new(
            record,
            true,
            display.IsAttached,
            display.DeviceName,
            checked((int)(mode?.Width ?? (uint)record.Width)),
            checked((int)(mode?.Height ?? (uint)record.Height)),
            checked((int)(mode?.RefreshRate ?? (uint)record.RefreshRate)),
            WindowsArrangementService.GetWindowsNumber(display.DeviceName),
            mode?.X,
            mode?.Y,
            Health(true, display.IsAttached));
    }

    private static (int Width, int Height) NormalizeOrientation(int width, int height, bool portrait) =>
        portrait && width > height ? (height, width) : (!portrait && height > width ? (height, width) : (width, height));

    private static void ValidateMode(int width, int height, int refreshRate)
    {
        if (width < 320 || height < 200)
            throw new ArgumentOutOfRangeException(nameof(width), "Monitor resolution is too small.");
        if (!SupportedRefreshRates.Contains(refreshRate))
            throw new ArgumentOutOfRangeException(nameof(refreshRate), $"Refresh rate must be one of: {string.Join(", ", SupportedRefreshRates)} Hz.");
    }
}
