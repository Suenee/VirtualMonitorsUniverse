using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Provides lifecycle management for an in-process ASP.NET Core endpoint.
/// </summary>
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
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(BuildListenUrl(endpoint));
            ConfigureServices(builder.Services);
            var app = builder.Build();
            ConfigureApplication(app);
            await app.StartAsync().ConfigureAwait(true);
            _application = app;
            ActivePort = endpoint.Port;
            LogStore.Write("INFO", ServiceKey, "SERVICE_START", $"{Name} started on {endpoint.Interface}:{endpoint.Port}");
        }
        catch (Exception ex)
        {
            LogStore.Write("ERROR", ServiceKey, "SERVICE_START_FAILED", $"{Name} failed to start: {ex.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { endpoint.Interface, endpoint.Port, exception = ex.ToString() }));
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_application is null) return;
        var app = _application;
        _application = null;
        var port = ActivePort;
        ActivePort = null;
        try
        {
            await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await app.DisposeAsync().ConfigureAwait(true);
            LogStore.Write("INFO", ServiceKey, "SERVICE_STOP", $"{Name} stopped" + (port is null ? string.Empty : $" (port {port})"));
        }
        catch (Exception ex)
        {
            LogStore.Write("ERROR", ServiceKey, "SERVICE_STOP_FAILED", $"{Name} failed to stop cleanly: {ex.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { exception = ex.ToString() }));
            throw;
        }
    }

    public async Task RestartAsync(ServiceEndpointSettings endpoint)
    {
        try
        {
            if (_application is not null)
            {
                var app = _application;
                _application = null;
                ActivePort = null;
                await app.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                await app.DisposeAsync().ConfigureAwait(true);
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(BuildListenUrl(endpoint));
            ConfigureServices(builder.Services);
            var replacement = builder.Build();
            ConfigureApplication(replacement);
            await replacement.StartAsync().ConfigureAwait(true);
            _application = replacement;
            ActivePort = endpoint.Port;
            LogStore.Write("INFO", ServiceKey, "SERVICE_RESTART", $"{Name} restarted on {endpoint.Interface}:{endpoint.Port}");
        }
        catch (Exception ex)
        {
            LogStore.Write("ERROR", ServiceKey, "SERVICE_RESTART_FAILED", $"{Name} failed to restart: {ex.Message}", detailsJson: System.Text.Json.JsonSerializer.Serialize(new { endpoint.Interface, endpoint.Port, exception = ex.ToString() }));
            throw;
        }
    }

    protected virtual void ConfigureServices(IServiceCollection services) { }
    protected abstract void ConfigureApplication(WebApplication application);

    private static string BuildListenUrl(ServiceEndpointSettings endpoint) =>
        endpoint.Interface.Equals("any", StringComparison.OrdinalIgnoreCase)
            ? $"http://0.0.0.0:{endpoint.Port}"
            : $"http://127.0.0.1:{endpoint.Port}";

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;
        var app = _application;
        _application = null;
        ActivePort = null;
        await app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class WebServerService : NetworkService
{
    public WebServerService(LogStore logStore) : base("Web Server", "WEB", logStore) { }

    protected override void ConfigureApplication(WebApplication application)
    {
        application.MapGet("/", () => Results.Text("I am alive...!", "text/plain; charset=utf-8"));
    }
}

internal sealed class WebSocketServerService : NetworkService
{
    public WebSocketServerService(LogStore logStore) : base("Web Socket", "SOCKET", logStore) { }

    protected override void ConfigureApplication(WebApplication application)
    {
        application.UseWebSockets();
        application.Map("/", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                await context.Response.WriteAsync("WebSocket endpoint");
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
