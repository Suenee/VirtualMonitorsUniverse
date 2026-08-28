using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VirtualMonitorsUniverse.Server;

internal sealed record WebSettingsSaveResult(string TargetUrl, bool RestartRequired, int WaitMilliseconds);

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
            LogStore.Write("ERROR", ServiceKey, "SERVICE_START_FAILED", $"{Name} failed to start: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { endpoint.Interface, endpoint.Port, exception = ex.ToString() }));
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
            LogStore.Write("ERROR", ServiceKey, "SERVICE_STOP_FAILED", $"{Name} failed to stop cleanly: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { exception = ex.ToString() }));
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
        await app.StopAsync(TimeSpan.FromSeconds(2));
        await app.DisposeAsync();
    }
}

internal sealed class WebServerService : NetworkService
{
    private static readonly string[] ServiceKeys = ["VMU", "VMU_SERVER", "WEB", "SOCKET"];
    private readonly Func<IReadOnlyDictionary<string, bool>> _statusProvider;
    private readonly Func<ServerSettings> _settingsProvider;
    private readonly Func<ServerSettings, Task<WebSettingsSaveResult>> _settingsSaver;
    private readonly Func<int, bool> _isOwnedListener;

    public WebServerService(
        LogStore logStore,
        Func<IReadOnlyDictionary<string, bool>> statusProvider,
        Func<ServerSettings> settingsProvider,
        Func<ServerSettings, Task<WebSettingsSaveResult>> settingsSaver,
        Func<int, bool> isOwnedListener)
        : base("Web Server", "WEB", logStore)
    {
        _statusProvider = statusProvider;
        _settingsProvider = settingsProvider;
        _settingsSaver = settingsSaver;
        _isOwnedListener = isOwnedListener;
    }

    protected override void ConfigureApplication(WebApplication app)
    {
        app.MapGet("/", StatusPage);
        app.MapGet("/settings", SettingsPage);
        app.MapGet("/monitors", () => HtmlShell("Monitors", "<div class=\"page\"><h1>Monitors</h1><p>This page will contain virtual monitor management.</p></div>"));
        app.MapGet("/log", LogPage);
        app.MapGet("/monitor/{id}", (string id) => HtmlShell($"Monitor {System.Net.WebUtility.HtmlEncode(id)}", $"<div class=\"page\"><h1>Monitor {System.Net.WebUtility.HtmlEncode(id)}</h1><p>This page will contain the remote desktop terminal for this monitor.</p></div>"));
        app.MapGet("/docs", () => HtmlShell("Documentation", $"<div class=\"page\"><h1>Documentation</h1><p><a href=\"{ProjectInfo.DocumentationUrl}\">Open project documentation</a></p></div>"));
        app.MapGet("/guide", () => HtmlShell("User Guide", $"<div class=\"page\"><h1>User Guide</h1><p><a href=\"{ProjectInfo.GuideUrl}\">Open the user guide nobody will read</a></p></div>"));
        app.MapGet("/api/health", () => Results.Json(new { status = "ok", version = ProjectInfo.Version }));
        app.MapGet("/api/status", () => Results.Json(CreateStatusModel()));
        app.MapGet("/api/settings", () => Results.Json(_settingsProvider()));
        app.MapPost("/api/settings", SaveSettingsAsync);
        app.MapGet("/api/log", (HttpRequest request) => Results.Json(ReadLog(request)));
        app.MapGet("/api/log/{id:long}", (long id) => LogStore.ReadById(id) is { } entry ? Results.Json(entry) : Results.NotFound());
        app.MapDelete("/api/log", () => { LogStore.Clear(); return Results.NoContent(); });
        app.MapGet("/api/log/export/{format}", ExportLog);
    }

    private object CreateStatusModel()
    {
        var states = _statusProvider();
        return new
        {
            application = ProjectInfo.ProductName,
            version = ProjectInfo.Version,
            services = new[]
            {
                new { key = "VMU", name = "VMU", running = states.GetValueOrDefault("VMU") },
                new { key = "VMU_SERVER", name = "VMU Server", running = states.GetValueOrDefault("VMU_SERVER") },
                new { key = "WEB", name = "Web Server", running = states.GetValueOrDefault("WEB") },
                new { key = "SOCKET", name = "Socket Server", running = states.GetValueOrDefault("SOCKET") },
            },
            monitors = new { installed = 0, connected = 0 },
            remote = new { clients = 0, enabled = false },
            runtime = new { framework = ".NET 10", os = Environment.OSVersion.VersionString },
            links = new { github = ProjectInfo.RepositoryUrl, documentation = "/docs", guide = "/guide" },
        };
    }

    private IResult StatusPage()
    {
        var body = """
<div class="page"><h1>Virtual Monitors Universe</h1><div class="muted" id="version"></div>
<h2>Services</h2><div id="services" class="cards"></div>
<h2>Overview</h2><div class="stats"><div><strong id="installed">0</strong><span>Installed monitors</span></div><div><strong id="connected">0</strong><span>Connected monitors</span></div><div><strong id="clients">0</strong><span>Remote clients</span></div><div><strong id="remote">Disabled</strong><span>Remote access</span></div></div>
<h2>Project</h2><p><a id="github" href="#">GitHub</a> · <a href="/docs">Documentation</a> · <a href="/guide">User Guide</a></p></div>
<script>async function refresh(){const s=await fetch('/api/status',{cache:'no-store'}).then(r=>r.json());document.getElementById('version').textContent='Version '+s.version;document.getElementById('services').innerHTML=s.services.map(x=>`<div class="service"><span class="dot ${x.running?'on':'off'}"></span><span>${x.name}</span><b>${x.running?'Running':'Stopped'}</b></div>`).join('');document.getElementById('installed').textContent=s.monitors.installed;document.getElementById('connected').textContent=s.monitors.connected;document.getElementById('clients').textContent=s.remote.clients;document.getElementById('remote').textContent=s.remote.enabled?'Enabled':'Disabled';document.getElementById('github').href=s.links.github;}refresh();setInterval(refresh,1500);</script>
""";
        return HtmlShell("Status", body);
    }

    private IResult SettingsPage()
    {
        var body = """
<div class="page"><h1>Settings</h1><div class="subnav"><a href="/monitors">Monitors</a><a href="/">Status</a><a href="/log">View Log</a></div><div id="error" class="error"></div>
<form id="form"><table class="settings"><thead><tr><th>Service</th><th>Interface</th><th>Port</th></tr></thead><tbody>
<tr><td>VMU Server</td><td><select id="vmuInterface"><option>localhost</option><option>any</option></select></td><td><input id="vmuPort" type="number" min="1" max="65535"></td></tr>
<tr><td>Web Server</td><td><select id="webInterface"><option>localhost</option><option>any</option></select></td><td><input id="webPort" type="number" min="1" max="65535"></td></tr>
<tr><td>Web Socket</td><td><select id="socketInterface"><option>localhost</option><option>any</option></select></td><td><input id="socketPort" type="number" min="1" max="65535"></td></tr></tbody></table>
<label class="row">Log retention <input id="retention" type="number" min="1" max="3650"> days</label>
<fieldset><legend>On exit</legend><label class="row">Monitors <select id="monitorAction"><option>Disconnect</option><option>Keep</option><option>Uninstall</option></select></label><label class="row">Restore services <input id="restore" type="checkbox"></label></fieldset>
<div class="actions"><button type="submit">Save</button><button type="button" onclick="location.reload()">Cancel</button></div></form></div>
<div id="restart" class="restart hidden"><h1>Restarting Web Server...</h1><div class="progress"><div id="bar"></div></div><p id="restartText">Waiting for the new endpoint.</p></div>
<script>
let original;
async function load(){original=await fetch('/api/settings').then(r=>r.json());document.getElementById('vmuInterface').value=original.vmu.interface;document.getElementById('vmuPort').value=original.vmu.port;document.getElementById('webInterface').value=original.web.interface;document.getElementById('webPort').value=original.web.port;document.getElementById('socketInterface').value=original.socket.interface;document.getElementById('socketPort').value=original.socket.port;document.getElementById('retention').value=Math.ceil(original.logging.retentionMinutes/1440);document.getElementById('monitorAction').value=original.exit.monitorAction;document.getElementById('restore').checked=original.exit.restoreServices;}
document.getElementById('form').onsubmit=async e=>{e.preventDefault();const error=document.getElementById('error');error.textContent='';const data={vmu:{interface:document.getElementById('vmuInterface').value,port:+document.getElementById('vmuPort').value},web:{interface:document.getElementById('webInterface').value,port:+document.getElementById('webPort').value},socket:{interface:document.getElementById('socketInterface').value,port:+document.getElementById('socketPort').value},logging:{retentionMinutes:+document.getElementById('retention').value*1440},exit:{monitorAction:document.getElementById('monitorAction').value,restoreServices:document.getElementById('restore').checked},serviceState:original.serviceState};const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});if(!r.ok){const x=await r.json();error.textContent=x.error||'Settings could not be saved.';return;}const result=await r.json();if(result.restartRequired)waitForRestart(result.targetUrl,result.waitMilliseconds);else location.reload();};
async function waitForRestart(target,waitMs){document.getElementById('form').closest('.page').classList.add('hidden');const restart=document.getElementById('restart');restart.classList.remove('hidden');const bar=document.getElementById('bar');const text=document.getElementById('restartText');const start=Date.now();const timer=setInterval(()=>{const p=Math.min(100,Math.round((Date.now()-start)/waitMs*100));bar.style.width=p+'%';},100);while(Date.now()-start<waitMs){try{await fetch(target+'api/health',{mode:'no-cors',cache:'no-store'});clearInterval(timer);bar.style.width='100%';text.textContent='Web Server is ready.';setTimeout(()=>location.href=target+'settings',250);return;}catch{}await new Promise(r=>setTimeout(r,350));}clearInterval(timer);bar.style.width='100%';restart.innerHTML=`<div class="broken">▱<span>×</span></div><h1>Web Server did not restart</h1><p>The new endpoint did not answer before the timeout.</p><p><button onclick="location.href='${target}settings'">Retry</button></p>`;}
load();
</script>
""";
        return HtmlShell("Settings", body);
    }

    private async Task<IResult> SaveSettingsAsync(HttpRequest request)
    {
        var proposed = await request.ReadFromJsonAsync<ServerSettings>();
        if (proposed is null) return Results.BadRequest(new { error = "Invalid settings payload." });
        var ports = new[] { proposed.Vmu.Port, proposed.Web.Port, proposed.Socket.Port };
        if (ports.Any(x => x is < 1 or > 65535) || ports.Distinct().Count() != ports.Length) return Results.Conflict(new { error = "Service ports must be valid and unique." });
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(x => x.Port).ToHashSet();
        var blocked = ports.FirstOrDefault(port => active.Contains(port) && !_isOwnedListener(port));
        if (blocked != 0) return Results.Conflict(new { error = $"Port {blocked} is already used by another TCP listener." });
        var result = await _settingsSaver(proposed);
        return Results.Json(result);
    }

    private IResult LogPage()
    {
        var body = """
<div class="logpage"><div class="filters"><h3>Filters</h3><label><input type="checkbox" data-service="VMU" checked> VMU <span class="dot" id="s-VMU"></span></label><label><input type="checkbox" data-service="VMU_SERVER" checked> VMU Server <span class="dot" id="s-VMU_SERVER"></span></label><label><input type="checkbox" data-service="WEB" checked> Web Server <span class="dot" id="s-WEB"></span></label><label><input type="checkbox" data-service="SOCKET" checked> Socket Server <span class="dot" id="s-SOCKET"></span></label></div>
<div class="logmain"><div class="toolbar"><input id="search" placeholder="Search..."></div><div class="tablewrap"><table id="log"><thead><tr><th data-col="timestamp">Timecode</th><th data-col="level">Level</th><th data-col="service">Service</th><th data-col="monitorId">Monitor</th><th data-col="event">Event</th><th data-col="message">Message</th></tr></thead><tbody></tbody></table></div><div class="logfooter"><label><input id="tail" type="checkbox" checked> Always at end</label><div><button id="exportXlsx">Export XLSX</button><button id="exportCsv">Export CSV</button><button id="exportTxt">Export TXT</button><button id="clear">Clear</button></div></div></div></div>
<dialog id="detail"><pre id="detailText"></pre><button onclick="document.getElementById('detail').close()">Close</button></dialog>
<script>
let rows=[],sortCol=null,sortDir=1,lastSelected=null;const tbody=document.querySelector('#log tbody'),search=document.getElementById('search'),tail=document.getElementById('tail');
function selected(){return [...document.querySelectorAll('[data-service]:checked')].map(x=>x.dataset.service)}
function query(){const p=new URLSearchParams();selected().forEach(x=>p.append('service',x));if(search.value)p.set('q',search.value);return p}
async function refresh(){const [data,status]=await Promise.all([fetch('/api/log?'+query()).then(r=>r.json()),fetch('/api/status',{cache:'no-store'}).then(r=>r.json())]);rows=data;status.services.forEach(x=>{const d=document.getElementById('s-'+x.key);if(d)d.className='dot '+(x.running?'on':'off')});render();}
function render(){let data=[...rows];if(!tail.checked&&sortCol)data.sort((a,b)=>String(a[sortCol]??'').localeCompare(String(b[sortCol]??''))*sortDir);tbody.innerHTML=data.map(x=>`<tr data-id="${x.id}"><td>${new Date(x.timestamp).toLocaleString()}</td><td>${x.level}</td><td>${x.service}</td><td>${x.monitorId??''}</td><td>${x.event}</td><td>${escapeHtml(x.message)}</td></tr>`).join('');const target=tail.checked?tbody.lastElementChild:(lastSelected&&tbody.querySelector(`[data-id="${lastSelected}"]`));if(target){target.classList.add('selected');target.scrollIntoView({block:'nearest'});lastSelected=target.dataset.id;}}
function escapeHtml(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML}
tbody.onclick=e=>{const tr=e.target.closest('tr');if(!tr)return;lastSelected=tr.dataset.id;tbody.querySelectorAll('tr').forEach(x=>x.classList.remove('selected'));tr.classList.add('selected')};tbody.ondblclick=async e=>{const tr=e.target.closest('tr');if(!tr)return;const x=await fetch('/api/log/'+tr.dataset.id).then(r=>r.json());document.getElementById('detailText').textContent=JSON.stringify(x,null,2);document.getElementById('detail').showModal()};
document.querySelectorAll('[data-service]').forEach(x=>x.onchange=refresh);search.oninput=refresh;tail.onchange=()=>{sortCol=null;render()};document.querySelectorAll('th[data-col]').forEach(th=>th.onclick=()=>{if(tail.checked)return;if(sortCol===th.dataset.col)sortDir*=-1;else{sortCol=th.dataset.col;sortDir=1}render()});
document.getElementById('clear').onclick=async()=>{if(confirm('Clear the log?')){await fetch('/api/log',{method:'DELETE'});refresh()}};function exp(f){location.href='/api/log/export/'+f+'?'+query()}document.getElementById('exportXlsx').onclick=()=>exp('xlsx');document.getElementById('exportCsv').onclick=()=>exp('csv');document.getElementById('exportTxt').onclick=()=>exp('txt');refresh();setInterval(refresh,1000);
</script>
""";
        return HtmlShell("View Log", body);
    }

    private IReadOnlyList<LogEntry> ReadLog(HttpRequest request)
    {
        var services = request.Query["service"].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
        return LogStore.Read(request.Query["q"].FirstOrDefault(), services.Length == 0 ? ServiceKeys : services);
    }

    private IResult ExportLog(HttpRequest request, string format)
    {
        if (format is not ("xlsx" or "csv" or "txt")) return Results.BadRequest();
        var entries = ReadLog(request);
        var bytes = LogExportService.ExportBytes(format, entries);
        var contentType = format == "xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : format == "csv" ? "text/csv" : "text/plain";
        return Results.File(bytes, contentType, $"vmu-log-{DateTime.Now:yyyyMMdd-HHmmss}.{format}");
    }

    private static IResult HtmlShell(string title, string body)
    {
        var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + title + " - VMU</title><style>"
            + "*{box-sizing:border-box}body{font-family:Segoe UI,Arial,sans-serif;margin:0;color:#202124;background:#f5f6f8}nav{height:52px;background:#202124;padding:8px 18px;display:flex;align-items:center;gap:10px}nav a{color:white;text-decoration:none;padding:8px 10px;border-radius:5px}nav a:hover{background:#34373b}.page{max-width:1000px;margin:30px auto;padding:0 20px}.subnav{display:flex;gap:8px;margin:8px 0 20px}.subnav a{background:white;border:1px solid #ccd1d8;border-radius:5px;padding:7px 12px;text-decoration:none;color:#202124}.muted{color:#687078}.cards,.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}.service,.stats>div{background:white;border:1px solid #d9dde3;border-radius:7px;padding:14px;display:flex;gap:10px;align-items:center}.service b{margin-left:auto}.stats>div{flex-direction:column;align-items:flex-start}.stats strong{font-size:24px}.dot{display:inline-block;width:11px;height:11px;border-radius:50%;background:#aaa;margin-left:auto}.dot.on{background:#25a746}.dot.off{background:#aaa}.settings{border-collapse:collapse;background:white}.settings th,.settings td{padding:7px 10px;text-align:left}.settings input,.settings select,.row input,.row select{padding:5px}.row{display:flex;gap:10px;align-items:center;margin:14px 0}fieldset{border:1px solid #b8bec7;border-radius:4px;padding:12px;margin-top:18px}.actions{display:flex;gap:8px;margin-top:18px}.actions button,.logfooter button,dialog button,.restart button{padding:7px 14px}.error{color:#b3261e;margin:10px 0}.hidden{display:none!important}.restart{max-width:650px;margin:80px auto;text-align:center}.progress{height:18px;background:#ddd;border-radius:9px;overflow:hidden}.progress div{height:100%;width:0;background:#2d7dd2;transition:width .1s}.broken{font-size:90px;color:#777;position:relative}.broken span{color:#d22;position:absolute;margin-left:-46px;margin-top:8px}.logpage{display:grid;grid-template-columns:190px 1fr;height:calc(100vh - 52px);padding:10px;gap:8px}.filters{background:#eee;border:1px solid #ccc;padding:8px}.filters label{display:flex;align-items:center;gap:5px;padding:4px}.logmain{display:flex;flex-direction:column;min-width:0}.toolbar{display:flex;justify-content:flex-end;margin-bottom:6px}.toolbar input{width:260px;padding:6px}.tablewrap{background:white;border:1px solid #bbb;overflow:auto;flex:1}#log{border-collapse:collapse;width:100%;font:13px Consolas,monospace}#log th{position:sticky;top:0;background:#e1e6ec;border-bottom:1px solid #aaa;padding:6px;text-align:left;cursor:pointer}#log td{padding:5px;border-bottom:1px solid #eee;white-space:nowrap}#log tr.selected{background:#cfe4ff}.logfooter{display:flex;justify-content:space-between;align-items:center;padding-top:8px}.logfooter>div{display:flex;gap:6px}dialog{max-width:800px;width:70%;border:1px solid #888;border-radius:6px}dialog pre{white-space:pre-wrap}"
            + "</style></head><body><nav><a href=\"/settings\" title=\"Settings\">⚙</a></nav>" + body + "</body></html>";
        return Results.Content(html, "text/html; charset=utf-8");
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
