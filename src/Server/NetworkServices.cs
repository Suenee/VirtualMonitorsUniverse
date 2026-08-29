using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Server;

internal sealed record WebSettingsSaveResult(string TargetUrl, bool RestartRequired, int WaitMilliseconds);
internal sealed record MonitorCreateRequest(string? Name, string? Title, int Width, int Height, int RefreshRate, bool Portrait, string? AvatarAnimal);
internal sealed record MonitorUpdateRequest(string? Name, string? Title, int Width, int Height, int RefreshRate, bool Portrait, string RemoteAccess, string SecurityMode, string? Password, bool RegenerateApiKey, bool CollaborationClipboard, bool CollaborationMouse, bool CollaborationKeyboard);
internal sealed record AccessRuleRequest(string ClientId, string? IpAddress, string? MacAddress, string? ComputerName, string? UserName, string Permission);
internal sealed record MonitorOrderRequest(string[] Ids);
internal sealed record ArrangementDisplayRequest(string DeviceName, int X, int Y);
internal sealed record ArrangementApplyRequest(ArrangementDisplayRequest[] Displays);

internal abstract class NetworkService : IAsyncDisposable
{
    private WebApplication? _application;

    protected NetworkService(string name, string serviceKey, LogStore logStore)
    {
        Name = name;
        ServiceKey = serviceKey;
        LogStore = logStore;
    }

    public string Name { get; }
    public string ServiceKey { get; }
    protected LogStore LogStore { get; }
    public bool IsRunning => _application is not null;
    public int? ActivePort { get; private set; }

    public async Task StartAsync(ServiceEndpointSettings endpoint)
    {
        if (IsRunning) return;
        try
        {
            _application = await BuildAndStartAsync(endpoint);
            ActivePort = endpoint.Port;
            LogStore.Write("INFO", ServiceKey, "SERVICE_START", $"{Name} started on {endpoint.Interface}:{endpoint.Port}");
        }
        catch (Exception ex)
        {
            LogStore.Write("ERROR", ServiceKey, "SERVICE_START_FAILED", $"{Name} failed to start: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { exception = ex.ToString() }));
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_application is null) return;
        var application = _application;
        _application = null;
        ActivePort = null;
        await application.StopAsync(TimeSpan.FromSeconds(5));
        await application.DisposeAsync();
        LogStore.Write("INFO", ServiceKey, "SERVICE_STOP", $"{Name} stopped");
    }

    public async Task RestartAsync(ServiceEndpointSettings endpoint)
    {
        if (_application is not null) await StopAsync();
        await StartAsync(endpoint);
    }

    private async Task<WebApplication> BuildAndStartAsync(ServiceEndpointSettings endpoint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(endpoint.Interface.Equals("any", StringComparison.OrdinalIgnoreCase)
            ? $"http://0.0.0.0:{endpoint.Port}"
            : $"http://127.0.0.1:{endpoint.Port}");
        ConfigureServices(builder.Services);
        var application = builder.Build();
        ConfigureApplication(application);
        await application.StartAsync();
        return application;
    }

    protected virtual void ConfigureServices(IServiceCollection services) { }
    protected abstract void ConfigureApplication(WebApplication app);

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;
        var application = _application;
        _application = null;
        ActivePort = null;
        await application.StopAsync(TimeSpan.FromSeconds(2));
        await application.DisposeAsync();
    }
}

internal sealed class WebServerService : NetworkService
{
    private static readonly string[] ServiceKeys = ["VMU", "VMU_SERVER", "WEB", "SOCKET"];
    private readonly MonitorApplicationService _monitors;
    private readonly MonitorThumbnailService _capture = new();
    private readonly SystemResourceService _resources = new();
    private readonly DisplayArrangementCoordinator _arrangements;
    private readonly TerminalStreamSettingsStore _streamSettings;
    private readonly Func<IReadOnlyDictionary<string, bool>> _statusProvider;
    private readonly Func<ServerSettings> _settingsProvider;
    private readonly Func<ServerSettings, Task<WebSettingsSaveResult>> _settingsSaver;
    private readonly Func<int, bool> _isOwnedListener;

    public WebServerService(
        LogStore logs,
        MonitorApplicationService monitors,
        Func<IReadOnlyDictionary<string, bool>> status,
        Func<ServerSettings> settings,
        Func<ServerSettings, Task<WebSettingsSaveResult>> saver,
        Func<int, bool> owned) : base("Web Server", "WEB", logs)
    {
        _monitors = monitors;
        _statusProvider = status;
        _settingsProvider = settings;
        _settingsSaver = saver;
        _isOwnedListener = owned;
        _arrangements = new DisplayArrangementCoordinator(logs);
        _streamSettings = new TerminalStreamSettingsStore(monitors.DataRoot);
    }

    protected override void ConfigureApplication(WebApplication app)
    {
        app.MapGet("/", StatusPage);
        app.MapGet("/settings", SettingsPage);
        app.MapGet("/settings/arrangement", () => Results.Redirect("/arrangement"));
        app.MapGet("/arrangement", ArrangementPage);
        app.MapGet("/monitors", MonitorsPage);
        app.MapGet("/monitors/new", NewMonitorPage);
        app.MapGet("/monitors/{id}", MonitorPropertiesPage);
        app.MapGet("/monitor/{id}", TerminalPage);
        app.MapGet("/log", LogPage);

        app.MapGet("/api/health", () => Results.Json(new { status = "ok", version = ProjectInfo.Version }));
        app.MapGet("/api/status", () => Results.Json(StatusModel()));
        app.MapGet("/api/resources", () => Results.Json(_resources.Read()));
        app.MapGet("/api/arrangement", () => Results.Json(ArrangementModel()));
        app.MapPost("/api/arrangement/apply", ApplyArrangementAsync);
        app.MapPost("/api/arrangement/keep", () => Results.Json(new { kept = _arrangements.Keep() }));
        app.MapPost("/api/arrangement/revert", () => Results.Json(new { reverted = _arrangements.Revert() }));
        app.MapPost("/api/arrangement/open-windows-settings", OpenWindowsDisplaySettings);
        app.MapGet("/api/settings", () => Results.Json(_settingsProvider()));
        app.MapPost("/api/settings", SaveSettingsAsync);

        app.MapGet("/api/log", (HttpRequest request) => Results.Json(ReadLog(request)));
        app.MapGet("/api/log/count", (HttpRequest request) => Results.Json(ReadLogCount(request)));
        app.MapDelete("/api/log", () => { LogStore.Clear(); return Results.NoContent(); });
        app.MapGet("/api/log/export/{format}", ExportLog);

        app.MapGet("/api/monitors", () => Results.Json(_monitors.List()));
        app.MapPost("/api/monitors/order", ReorderAsync);
        app.MapGet("/api/monitors/name-available/{name}", (string name, string? except) => Results.Json(new { available = _monitors.NameAvailable(name, except) }));
        app.MapPost("/api/monitors", CreateAsync);
        app.MapGet("/api/monitors/{id}", (string id) => _monitors.Get(id) is { } monitor ? Results.Json(monitor) : Results.NotFound());
        app.MapPut("/api/monitors/{id}", UpdateAsync);
        app.MapPost("/api/monitors/{id}/connect", (string id) => Action(() => _monitors.Connect(id)));
        app.MapPost("/api/monitors/{id}/disconnect", (string id) => Action(() => _monitors.Disconnect(id)));
        app.MapPost("/api/monitors/{id}/uninstall", Uninstall);
        app.MapGet("/api/monitors/{id}/thumbnail", ThumbnailAsync);
        app.MapGet("/api/monitors/{id}/live", LiveAsync);
        app.MapGet("/api/monitors/{id}/stream-settings", StreamSettings);
        app.MapPut("/api/monitors/{id}/stream-settings", SaveStreamSettingsAsync);
        app.MapGet("/api/monitors/{id}/avatar", Avatar);
        app.MapPost("/api/monitors/{id}/avatar/animal/{animal}", (string id, string animal) => Action(() => _monitors.SetAnimalAvatar(id, animal)));
        app.MapPost("/api/monitors/{id}/avatar/upload", UploadAvatarAsync);
        app.MapGet("/api/monitors/{id}/access-rules", (string id) => Results.Json(_monitors.ListAccessRules(id)));
        app.MapPost("/api/monitors/{id}/access-rules", RuleAsync);
        app.MapDelete("/api/monitors/{id}/access-rules/{ruleId:long}", (string id, long ruleId) => { _monitors.DeleteAccessRule(id, ruleId); return Results.NoContent(); });
    }

    private object StatusModel()
    {
        var status = _statusProvider();
        var monitors = _monitors.List();
        return new
        {
            application = ProjectInfo.ProductName,
            version = ProjectInfo.Version,
            services = new[]
            {
                new { key = "VMU", name = "VMU", running = status.GetValueOrDefault("VMU") },
                new { key = "VMU_SERVER", name = "VMU Server", running = status.GetValueOrDefault("VMU_SERVER") },
                new { key = "WEB", name = "Web Server", running = status.GetValueOrDefault("WEB") },
                new { key = "SOCKET", name = "Socket Server", running = status.GetValueOrDefault("SOCKET") }
            },
            monitors = new { installed = monitors.Count(x => x.Installed), connected = monitors.Count(x => x.Connected) },
            remote = new { enabled = monitors.Any(x => x.Configuration.RemoteAccess != RemoteAccessMode.Disabled), clients = 0 },
            links = new
            {
                github = ProjectInfo.RepositoryUrl,
                documentation = ProjectInfo.DocumentationUrl,
                guide = ProjectInfo.GuideUrl,
                bugs = ProjectInfo.RepositoryUrl.TrimEnd('/') + "/issues"
            }
        };
    }

    private IResult StatusPage() => Shell("Status", WebUiRenderer.StatusBody());
    private IResult SettingsPage() => Shell("Settings", WebUiRenderer.SettingsBody() + WebPageEnhancements.Settings);
    private IResult ArrangementPage() => Shell("Arrangement", WebUiRenderer.ArrangementBody() + ArrangementWebEnhancement.Script, "arrangementbody");
    private IResult MonitorsPage() => Shell("Monitors", WebUiRenderer.MonitorsBody(_settingsProvider().WebUi.MonitorPreviewRefreshSeconds) + WebPageEnhancements.Monitors);
    private IResult LogPage() => Shell("Log", WebUiRenderer.LogBody() + WebPageEnhancements.Log, "logbody");

    private IResult NewMonitorPage()
    {
        var animal = MonitorAvatarService.RandomAnimal();
        var rates = BuildRefreshRateOptions(60);
        return Shell("Add Monitor", WebUiRenderer.NewMonitorBody(WebUiRenderer.AvatarPicker("animal", animal), rates));
    }

    private IResult MonitorPropertiesPage(string id)
    {
        var monitor = _monitors.Get(id);
        if (monitor is null) return Results.NotFound();
        if (!id.Equals(monitor.Configuration.Name, StringComparison.OrdinalIgnoreCase))
            return Results.Redirect("/monitors/" + Uri.EscapeDataString(monitor.Configuration.Name), permanent: true);

        var rates = BuildRefreshRateOptions(monitor.Configuration.RefreshRate);
        var picker = WebUiRenderer.AvatarPicker(monitor.Configuration.AvatarKind, monitor.Configuration.AvatarValue, monitor.Configuration.Name);
        return Shell("Monitor " + monitor.Configuration.Title, WebUiRenderer.MonitorPropertiesBody(monitor.Configuration.Name, picker, rates) + WebPageEnhancements.MonitorProperties);
    }

    private IResult TerminalPage(string id)
    {
        var monitor = _monitors.Get(id);
        if (monitor is null) return Results.NotFound();
        if (!id.Equals(monitor.Configuration.Name, StringComparison.OrdinalIgnoreCase))
            return Results.Redirect("/monitor/" + Uri.EscapeDataString(monitor.Configuration.Name), permanent: true);

        var vmuRunning = IsVmuServerRunning();
        var ready = monitor.Connected && !monitor.Health.IsError;
        return Shell("Terminal " + monitor.Configuration.Title, WebUiRenderer.TerminalBody(monitor.Configuration.Name, ready, vmuRunning) + WebPageEnhancements.Terminal, "terminalbody", WebUiRenderer.FullscreenNavButton);
    }

    private object ArrangementModel()
    {
        var virtualMonitors = _monitors.List()
            .Where(x => x.DeviceName is not null)
            .ToDictionary(x => x.DeviceName!, StringComparer.OrdinalIgnoreCase);

        return WindowsArrangementService.GetActive().Select(display =>
        {
            virtualMonitors.TryGetValue(display.DeviceName, out var monitor);
            return new
            {
                display.WindowsNumber,
                display.DeviceName,
                display.X,
                display.Y,
                display.Width,
                display.Height,
                display.Primary,
                title = monitor?.Configuration.Title,
                monitorName = monitor?.Configuration.Name,
                isVirtual = monitor is not null
            };
        });
    }

    private async Task<IResult> ApplyArrangementAsync(HttpRequest request)
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<ArrangementApplyRequest>();
            if (payload?.Displays is null || payload.Displays.Length == 0) return Results.BadRequest(new { error = "Display arrangement is empty." });

            var active = WindowsArrangementService.GetActive();
            var requestedNames = payload.Displays.Select(x => x.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (active.Count != payload.Displays.Length || active.Any(x => !requestedNames.Contains(x.DeviceName)))
                return Results.Conflict(new { error = "The active Windows display set changed. Reset the arrangement and try again." });

            var positions = payload.Displays.Select(x => new DisplayArrangementEntry(x.DeviceName, x.X, x.Y)).ToArray();
            return Results.Json(_arrangements.Apply(positions));
        }
        catch (Exception ex)
        {
            LogStore.Write("ERROR", "VMU", "ARRANGEMENT_APPLY_FAILED", $"Display arrangement failed: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { exception = ex.ToString() }));
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private IResult OpenWindowsDisplaySettings(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private async Task LiveAsync(string id, HttpContext context)
    {
        var monitor = _monitors.Get(id);
        if (!IsVmuServerRunning())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (monitor is null || !monitor.Connected || monitor.Health.IsError || monitor.DeviceName is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        var isLocalhost = remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
        var streamSettings = _streamSettings.Get(monitor.Configuration.VmuId);
        context.Response.ContentType = "multipart/x-mixed-replace; boundary=vmu";
        try
        {
            while (!context.RequestAborted.IsCancellationRequested && IsVmuServerRunning())
            {
                var frame = await _capture.GetLiveFrameAsync(monitor.Configuration.VmuId, monitor.DeviceName, streamSettings, isLocalhost, context.RequestAborted);
                var header = Encoding.ASCII.GetBytes($"--vmu\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                await context.Response.Body.WriteAsync(header, context.RequestAborted);
                await context.Response.Body.WriteAsync(frame, context.RequestAborted);
                await context.Response.Body.WriteAsync("\r\n"u8.ToArray(), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                _resources.AddVmuNetworkBytes(header.Length + frame.Length + 2);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogStore.Write("WARN", "WEB", "TERMINAL_STREAM_FAILED", ex.Message, monitor.Configuration.VmuId);
        }
    }

    private IResult StreamSettings(string id)
    {
        var monitor = _monitors.Get(id);
        return monitor is null ? Results.NotFound() : Results.Json(_streamSettings.Get(monitor.Configuration.VmuId));
    }

    private async Task<IResult> SaveStreamSettingsAsync(HttpRequest request, string id)
    {
        try
        {
            var monitor = _monitors.Get(id);
            if (monitor is null) return Results.NotFound();
            var proposed = await request.ReadFromJsonAsync<TerminalStreamSettings>();
            if (proposed is null) return Results.BadRequest();
            var saved = _streamSettings.Set(monitor.Configuration.VmuId, proposed);
            _capture.InvalidateLive(monitor.Configuration.VmuId);
            return Results.Json(saved);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IResult> ThumbnailAsync(string id, HttpContext context)
    {
        try
        {
            var monitor = _monitors.Get(id);
            if (monitor is null || !monitor.Connected || monitor.DeviceName is null) return Results.NotFound();
            if (context.Request.Query.TryGetValue("force", out var force) && force.Any(x => string.Equals(x, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "true", StringComparison.OrdinalIgnoreCase)))
                _capture.Invalidate(monitor.Configuration.VmuId);
            return Results.File(await _capture.GetThumbnailAsync(monitor.Configuration.VmuId, monitor.DeviceName, context.RequestAborted), "image/jpeg");
        }
        catch (Exception ex)
        {
            LogStore.Write("WARN", "WEB", "MONITOR_THUMBNAIL_FAILED", ex.Message, id);
            return Results.NotFound();
        }
    }

    private async Task<IResult> CreateAsync(HttpRequest request)
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<MonitorCreateRequest>();
            if (payload is null) return Results.BadRequest();
            return Results.Json(_monitors.Create(payload.Name, payload.Title, payload.Width, payload.Height, payload.RefreshRate, payload.Portrait, payload.AvatarAnimal));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IResult> UpdateAsync(HttpRequest request, string id)
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<MonitorUpdateRequest>();
            if (payload is null || !Enum.TryParse<RemoteAccessMode>(payload.RemoteAccess, true, out var remoteAccess) || !Enum.TryParse<RemoteSecurityMode>(payload.SecurityMode, true, out var securityMode))
                return Results.BadRequest();
            return Results.Json(_monitors.UpdateProperties(id, payload.Name, payload.Title, payload.Width, payload.Height, payload.RefreshRate, payload.Portrait, remoteAccess, securityMode, payload.Password, payload.RegenerateApiKey, payload.CollaborationClipboard, payload.CollaborationMouse, payload.CollaborationKeyboard));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IResult> ReorderAsync(HttpRequest request)
    {
        var payload = await request.ReadFromJsonAsync<MonitorOrderRequest>();
        if (payload is null) return Results.BadRequest();
        _monitors.Reorder(payload.Ids);
        return Results.NoContent();
    }

    private IResult Avatar(string id)
    {
        var avatar = _monitors.GetAvatar(id);
        return avatar is null ? Results.NotFound() : Results.File(avatar.Value.Bytes, avatar.Value.ContentType);
    }

    private async Task<IResult> UploadAvatarAsync(HttpRequest request, string id)
    {
        try
        {
            var file = (await request.ReadFormAsync()).Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest();
            if (file.Length > 2 * 1024 * 1024) return Results.BadRequest(new { error = "Avatar file must be at most 2 MB." });
            await using var stream = file.OpenReadStream();
            return Results.Json(_monitors.SetCustomAvatar(id, file.FileName, stream));
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IResult> RuleAsync(HttpRequest request, string id)
    {
        var payload = await request.ReadFromJsonAsync<AccessRuleRequest>();
        if (payload is null || !Enum.TryParse<AccessPermission>(payload.Permission, true, out var permission)) return Results.BadRequest();
        return Results.Json(_monitors.UpsertAccessRule(id, payload.ClientId, payload.IpAddress, payload.MacAddress, payload.ComputerName, payload.UserName, permission));
    }

    private static IResult Action(Func<MonitorSnapshot> action)
    {
        try { return Results.Json(action()); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private IResult Uninstall(string id)
    {
        try
        {
            var monitor = _monitors.Get(id);
            if (monitor is null) return Results.NotFound();
            _monitors.Uninstall(id);
            _streamSettings.Delete(monitor.Configuration.VmuId);
            _capture.InvalidateLive(monitor.Configuration.VmuId);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IResult> SaveSettingsAsync(HttpRequest request)
    {
        var proposed = await request.ReadFromJsonAsync<ServerSettings>();
        if (proposed is null) return Results.BadRequest();
        if (proposed.Vmu.Interface.Equals("any", StringComparison.OrdinalIgnoreCase) && !proposed.Web.Interface.Equals("any", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = "Web Server must use All Interfaces while VMU Server uses All Interfaces." });

        var endpoints = new[] { (Name: "VMU Server", Port: proposed.Vmu.Port), (Name: "Web Server", Port: proposed.Web.Port), (Name: "Web Socket", Port: proposed.Socket.Port) };
        if (endpoints.GroupBy(x => x.Port).Any(x => x.Count() > 1)) return Results.Conflict(new { error = "Service ports must be unique." });

        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(x => x.Port).ToHashSet();
        var blocked = endpoints.FirstOrDefault(x => active.Contains(x.Port) && !_isOwnedListener(x.Port));
        if (blocked != default) return Results.Conflict(new { error = $"{blocked.Name} port {blocked.Port} is already used." });
        return Results.Json(await _settingsSaver(proposed));
    }

    private IReadOnlyList<LogEntry> ReadLog(HttpRequest request)
    {
        var services = request.Query["service"].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
        return LogStore.Read(request.Query["q"].FirstOrDefault(), services.Length == 0 ? ServiceKeys : services);
    }

    private LogCount ReadLogCount(HttpRequest request)
    {
        var services = request.Query["service"].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
        return LogStore.Count(request.Query["q"].FirstOrDefault(), services.Length == 0 ? ServiceKeys : services);
    }

    private IResult ExportLog(HttpRequest request, string format)
    {
        if (format is not ("xlsx" or "csv" or "txt")) return Results.BadRequest();
        var bytes = LogExportService.ExportBytes(format, ReadLog(request));
        var contentType = format == "xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : format == "csv" ? "text/csv" : "text/plain";
        return Results.File(bytes, contentType, $"vmu-log-{DateTime.Now:yyyyMMdd-HHmmss}.{format}");
    }

    private string Navigation()
    {
        var status = _statusProvider();
        return WebUiRenderer.MonitorNavigation(_monitors.List(), status.GetValueOrDefault("VMU_SERVER"));
    }

    private IResult Shell(string title, string body, string bodyClass = "", string extraNav = "") =>
        Results.Content(WebUiRenderer.Shell(title, body, Navigation(), bodyClass, extraNav), "text/html; charset=utf-8");

    private bool IsVmuServerRunning() => _statusProvider().GetValueOrDefault("VMU_SERVER");

    private static string BuildRefreshRateOptions(int selected) => string.Join("", MonitorApplicationService.SupportedRefreshRates.Select(rate => $"<option value=\"{rate}\"{(rate == selected ? " selected" : string.Empty)}>{rate} Hz</option>"));
}

internal sealed class WebSocketServerService : NetworkService
{
    public WebSocketServerService(LogStore logs) : base("Socket Server", "SOCKET", logs) { }

    protected override void ConfigureApplication(WebApplication app)
    {
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 426;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);
                    break;
                }
            }
        });
    }
}
