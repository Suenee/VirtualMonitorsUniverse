using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VirtualMonitorsUniverse.Server;

internal abstract class NetworkService : IAsyncDisposable
{
    private WebApplication? _application;
    protected NetworkService(string name, string serviceKey, LogStore logStore) { Name = name; ServiceKey = serviceKey; LogStore = logStore; }
    public string Name { get; } public string ServiceKey { get; } protected LogStore LogStore { get; }
    public bool IsRunning => _application is not null; public int? ActivePort { get; private set; }

    public async Task StartAsync(ServiceEndpointSettings endpoint)
    {
        if (IsRunning) return;
        try { _application = await BuildAndStartAsync(endpoint); ActivePort = endpoint.Port; LogStore.Write("INFO", ServiceKey, "SERVICE_START", $"{Name} started on {endpoint.Interface}:{endpoint.Port}"); }
        catch (Exception ex) { LogStore.Write("ERROR", ServiceKey, "SERVICE_START_FAILED", $"{Name} failed to start: {ex.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { endpoint.Interface, endpoint.Port, exception = ex.ToString() })); throw; }
    }

    public async Task StopAsync()
    {
        if (_application is null) return; var app = _application; _application = null; var port = ActivePort; ActivePort = null;
        try { await app.StopAsync(TimeSpan.FromSeconds(5)); await app.DisposeAsync(); LogStore.Write("INFO", ServiceKey, "SERVICE_STOP", $"{Name} stopped" + (port is null ? string.Empty : $" (port {port})")); }
        catch (Exception ex) { LogStore.Write("ERROR", ServiceKey, "SERVICE_STOP_FAILED", $"{Name} failed to stop cleanly: {ex.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { exception = ex.ToString() })); throw; }
    }

    public async Task RestartAsync(ServiceEndpointSettings endpoint)
    {
        if (_application is not null) await StopAsync();
        await StartAsync(endpoint);
    }

    private async Task<WebApplication> BuildAndStartAsync(ServiceEndpointSettings endpoint)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.UseUrls(BuildListenUrl(endpoint)); ConfigureServices(builder.Services);
        var app = builder.Build(); ConfigureApplication(app); await app.StartAsync(); return app;
    }

    protected virtual void ConfigureServices(IServiceCollection services) { }
    protected abstract void ConfigureApplication(WebApplication application);
    private static string BuildListenUrl(ServiceEndpointSettings endpoint) => endpoint.Interface.Equals("any", StringComparison.OrdinalIgnoreCase) ? $"http://0.0.0.0:{endpoint.Port}" : $"http://127.0.0.1:{endpoint.Port}";
    public async ValueTask DisposeAsync() { if (_application is null) return; var app = _application; _application = null; ActivePort = null; await app.StopAsync(TimeSpan.FromSeconds(2)); await app.DisposeAsync(); }
}

internal sealed class WebServerService : NetworkService
{
    public WebServerService(LogStore logStore) : base("Web Server", "WEB", logStore) { }
    protected override void ConfigureApplication(WebApplication app)
    {
        app.MapGet("/", () => Html("Home", "This page will contain the VMU web client overview."));
        app.MapGet("/settings", () => Html("Settings", "This page will contain VMU configuration."));
        app.MapGet("/monitors", () => Html("Monitors", "This page will contain virtual monitor management."));
        app.MapGet("/status", () => Html("Status", "This page will contain VMU system status and diagnostics."));
        app.MapGet("/log", () => Html("View Log", "This page will contain the VMU operational log."));
        app.MapGet("/monitor/{id}", (string id) => Html($"Monitor {System.Net.WebUtility.HtmlEncode(id)}", "This page will contain the remote desktop terminal for this monitor."));
        app.MapGet("/api/status", () => Results.Json(new { status = "ok", application = "Virtual Monitors Universe" }));
    }

    private static IResult Html(string title, string message)
    {
        var body = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>"
            + title
            + " - VMU</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:0;color:#202124}nav{background:#202124;padding:12px 18px}nav a{color:white;text-decoration:none;margin-right:20px}main{max-width:1000px;margin:36px auto;padding:0 20px}h1{font-size:28px}</style></head><body><nav><a href=\"/settings\">⚙ Settings</a><a href=\"/monitors\">Monitors</a><a href=\"/status\">Status</a><a href=\"/log\">View Log</a></nav><main><h1>"
            + title
            + "</h1><p>"
            + message
            + "</p></main></body></html>";
        return Results.Content(body, "text/html; charset=utf-8");
    }
}

internal sealed class WebSocketServerService : NetworkService
{
    public WebSocketServerService(LogStore logStore) : base("Socket Server", "SOCKET", logStore) { }
    protected override void ConfigureApplication(WebApplication application)
    {
        application.UseWebSockets(); application.Map("/", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; await context.Response.WriteAsync("WebSocket endpoint"); return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync(); var buffer = new byte[4096];
            while (socket.State == System.Net.WebSockets.WebSocketState.Open) { var result = await socket.ReceiveAsync(buffer, context.RequestAborted); if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) { await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted); break; } }
        });
    }
}
