using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VirtualMonitorsUniverse.Server;

internal abstract class NetworkService : IAsyncDisposable
{
    private WebApplication? _application;
    protected NetworkService(string name, string serviceKey, LogStore logStore) { Name = name; ServiceKey = serviceKey; LogStore = logStore; }
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
            await app.StopAsync(TimeSpan.FromSeconds(5));
            await app.DisposeAsync();
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
        if (_application is not null) await StopAsync();
        await StartAsync(endpoint);
    }

    private async Task<WebApplication> BuildAndStartAsync(ServiceEndpointSettings endpoint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(BuildListenUrl(endpoint));
        ConfigureServices(builder.Services);
        var app = builder.Build();
        ConfigureApplication(app);
        await app.StartAsync();
        return app;
    }

    protected virtual void ConfigureServices(IServiceCollection services) { }
    protected abstract void ConfigureApplication(WebApplication application);

    private static string BuildListenUrl(ServiceEndpointSettings endpoint) => endpoint.Interface.Equals("any", StringComparison.OrdinalIgnoreCase) ? $"http://0.0.0.0:{endpoint.Port}" : $"http://127.0.0.1:{endpoint.Port}";

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;
        var app = _application;
        _application = null;
        ActivePort = null;
        await app.StopAsync(TimeSpan.FromSeconds(2));
        await app.DisposeAsync();
    }
}

internal sealed class WebServerService : NetworkService
{
    private static readonly string[] KnownServices = ["VMU", "VMU_SERVER", "WEB", "SOCKET"];

    public WebServerService(LogStore logStore) : base("Web Server", "WEB", logStore) { }

    protected override void ConfigureApplication(WebApplication app)
    {
        app.MapGet("/", () => Page("Status", "This page will contain VMU system status and diagnostics."));
        app.MapGet("/settings", () => Page("Settings", "This page will contain the complete VMU configuration.", settingsNavigation: true));
        app.MapGet("/monitors", () => Page("Monitors", "This page will contain virtual monitor tiles and monitor management.", settingsNavigation: true));
        app.MapGet("/log", () => Results.Content(LogPage(), "text/html; charset=utf-8"));
        app.MapGet("/monitor/{id}", (string id) => Page($"Monitor {System.Net.WebUtility.HtmlEncode(id)}", "This page will contain the remote desktop terminal for this monitor."));

        app.MapGet("/api/status", () => Results.Json(new { status = "ok", application = "Virtual Monitors Universe" }));
        app.MapGet("/api/log", (HttpRequest request) =>
        {
            var search = Convert.ToString(request.Query["search"]);
            var after = long.TryParse(request.Query["after"], out var parsed) ? Math.Max(0, parsed) : 0;
            var requested = Convert.ToString(request.Query["services"]);
            var services = string.IsNullOrWhiteSpace(requested)
                ? KnownServices
                : requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => KnownServices.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
            return Results.Json(LogStore.Read(search, services, after));
        });
        app.MapGet("/api/log/{id:long}", (long id) => LogStore.ReadById(id) is { } entry ? Results.Json(entry) : Results.NotFound());
        app.MapDelete("/api/log", () => { LogStore.Clear(); return Results.NoContent(); });
    }

    private static IResult Page(string title, string message, bool settingsNavigation = false)
    {
        var subnav = settingsNavigation ? "<aside><a href=\"/monitors\">Monitors</a><a href=\"/\">Status</a><a href=\"/log\">View Log</a></aside>" : string.Empty;
        var body = Document(title, subnav + "<main><h1>" + title + "</h1><p>" + message + "</p></main>");
        return Results.Content(body, "text/html; charset=utf-8");
    }

    private static string Document(string title, string body)
    {
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>"
            + title
            + " - VMU</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:0;color:#202124;background:#f6f7f9}nav{display:flex;align-items:center;gap:16px;background:#202124;padding:10px 18px;color:white}nav strong{margin-right:14px}nav a{color:white;text-decoration:none;font-size:20px}main{max-width:1180px;margin:28px auto;padding:0 20px}aside{max-width:1180px;margin:18px auto 0;padding:0 20px;display:flex;gap:18px}aside a{color:#1659a7;text-decoration:none}.card{background:white;border:1px solid #d7dce2;border-radius:6px;padding:18px}</style></head><body><nav><strong>VMU Client</strong><a href=\"/settings\" title=\"Settings\">⚙</a></nav>"
            + body
            + "</body></html>";
    }

    private static string LogPage()
    {
        const string logBody = """
<aside><a href="/monitors">Monitors</a><a href="/">Status</a><a href="/log"><strong>View Log</strong></a></aside>
<main><h1>View Log</h1><div class="log-layout"><section class="filters card"><strong>Filters</strong><label><input type="checkbox" value="VMU" checked> VMU</label><label><input type="checkbox" value="VMU_SERVER" checked> VMU Server</label><label><input type="checkbox" value="WEB" checked> Web Server</label><label><input type="checkbox" value="SOCKET" checked> Socket Server</label></section><section class="card log-main"><div class="toolbar"><input id="search" placeholder="Search..."><button id="clear">Clear</button></div><div class="table-wrap"><table><thead><tr><th>Timecode</th><th>Level</th><th>Service</th><th>Monitor</th><th>Event</th><th>Message</th></tr></thead><tbody id="rows"></tbody></table></div><label class="tail"><input id="tail" type="checkbox" checked> Always at end</label></section></div></main>
<dialog id="detail"><pre id="detailText"></pre><button onclick="detail.close()">Close</button></dialog>
<style>.log-layout{display:grid;grid-template-columns:210px 1fr;gap:8px}.filters{display:flex;flex-direction:column;gap:10px;align-self:start}.filters strong{margin-bottom:4px}.log-main{padding:0}.toolbar{display:flex;justify-content:flex-end;gap:8px;padding:10px}.toolbar input{width:260px;padding:6px}.table-wrap{height:560px;overflow:auto;border-top:1px solid #ccd2d8;border-bottom:1px solid #ccd2d8}table{border-collapse:collapse;width:100%;font:13px Consolas,monospace}th{position:sticky;top:0;background:#e1e6ec;text-align:left;border:1px solid #b8c0c8;padding:6px}td{border:1px solid #d4d8dd;padding:5px;white-space:nowrap}tr.selected{background:#1479d0;color:white}.tail{display:block;padding:10px}dialog{width:min(760px,90vw)}dialog pre{white-space:pre-wrap}</style>
<script>
const rows=document.getElementById('rows'),search=document.getElementById('search'),tail=document.getElementById('tail'),wrap=document.querySelector('.table-wrap'),detail=document.getElementById('detail'),detailText=document.getElementById('detailText');let selectedId=0,lastSignature='';
function services(){return [...document.querySelectorAll('.filters input:checked')].map(x=>x.value).join(',')}
function stamp(v){return new Date(v).toLocaleString('en-GB',{year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',fractionalSecondDigits:3}).replace(',','')}
async function load(){const s=services();if(!s){rows.innerHTML='';return}const data=await fetch('/api/log?services='+encodeURIComponent(s)+'&search='+encodeURIComponent(search.value)).then(r=>r.json());const sig=data.map(x=>x.id).join(',');if(sig===lastSignature)return;lastSignature=sig;rows.innerHTML='';for(const x of data){const tr=document.createElement('tr');tr.dataset.id=x.id;tr.innerHTML=`<td>${stamp(x.timestamp)}</td><td>${x.level}</td><td>${x.service}</td><td>${x.monitorId??''}</td><td>${x.event}</td><td>${x.message}</td>`;tr.ondblclick=()=>showDetail(x.id);tr.onclick=()=>select(tr,x.id);rows.appendChild(tr)}if(tail.checked&&rows.lastElementChild){select(rows.lastElementChild,+rows.lastElementChild.dataset.id);wrap.scrollTop=wrap.scrollHeight}else if(selectedId){const old=[...rows.children].find(x=>+x.dataset.id===selectedId);if(old)select(old,selectedId)}}
function select(tr,id){[...rows.children].forEach(x=>x.classList.remove('selected'));tr.classList.add('selected');selectedId=id}
async function showDetail(id){const x=await fetch('/api/log/'+id).then(r=>r.json());detailText.textContent=JSON.stringify(x,null,2);detail.showModal()}
document.querySelectorAll('.filters input').forEach(x=>x.onchange=()=>{lastSignature='';load()});search.oninput=()=>{lastSignature='';load()};tail.onchange=()=>{if(tail.checked){lastSignature='';load()}};document.getElementById('clear').onclick=async()=>{if(confirm('Clear the log?')){await fetch('/api/log',{method:'DELETE'});lastSignature='';load()}};setInterval(load,750);load();
</script>
""";
        return Document("View Log", logBody);
    }
}

internal sealed class WebSocketServerService : NetworkService
{
    public WebSocketServerService(LogStore logStore) : base("Socket Server", "SOCKET", logStore) { }

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
