using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VirtualMonitorsUniverse.Server;

internal sealed record WebSettingsSaveResult(string TargetUrl, bool RestartRequired, int WaitMilliseconds);
internal sealed record MonitorUpdateRequest(string FriendlyName, int Width, int Height, int RefreshRate, bool Portrait, string RemoteAccess, bool PasswordEnabled, string? Password, bool ApiKeyEnabled, bool RegenerateApiKey, bool ApprovalEnabled);

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
    private readonly MonitorApplicationService _monitorService;
    private readonly Func<IReadOnlyDictionary<string, bool>> _statusProvider;
    private readonly Func<ServerSettings> _settingsProvider;
    private readonly Func<ServerSettings, Task<WebSettingsSaveResult>> _settingsSaver;
    private readonly Func<int, bool> _isOwnedListener;

    public WebServerService(
        LogStore logStore,
        MonitorApplicationService monitorService,
        Func<IReadOnlyDictionary<string, bool>> statusProvider,
        Func<ServerSettings> settingsProvider,
        Func<ServerSettings, Task<WebSettingsSaveResult>> settingsSaver,
        Func<int, bool> isOwnedListener)
        : base("Web Server", "WEB", logStore)
    {
        _monitorService = monitorService;
        _statusProvider = statusProvider;
        _settingsProvider = settingsProvider;
        _settingsSaver = settingsSaver;
        _isOwnedListener = isOwnedListener;
    }

    protected override void ConfigureApplication(WebApplication app)
    {
        app.MapGet("/", StatusPage);
        app.MapGet("/settings", SettingsPage);
        app.MapGet("/monitors", MonitorsPage);
        app.MapGet("/monitors/new", NewMonitorPage);
        app.MapGet("/monitors/{id}", MonitorPropertiesPage);
        app.MapGet("/log", LogPage);
        app.MapGet("/monitor/{id}", (string id) => HtmlShell($"Monitor {System.Net.WebUtility.HtmlEncode(id)}", $"<div class=\"page\"><h1>Monitor Terminal</h1><p>Remote desktop streaming is not implemented yet.</p></div>"));
        app.MapGet("/api/health", () => Results.Json(new { status = "ok", version = ProjectInfo.Version }));
        app.MapGet("/api/status", () => Results.Json(CreateStatusModel()));
        app.MapGet("/api/settings", () => Results.Json(_settingsProvider()));
        app.MapPost("/api/settings", SaveSettingsAsync);
        app.MapGet("/api/log", (HttpRequest request) => Results.Json(ReadLog(request)));
        app.MapGet("/api/log/{id:long}", (long id) => LogStore.ReadById(id) is { } entry ? Results.Json(entry) : Results.NotFound());
        app.MapDelete("/api/log", () => { LogStore.Clear(); return Results.NoContent(); });
        app.MapGet("/api/log/export/{format}", ExportLog);
        app.MapGet("/api/monitors", () => Results.Json(_monitorService.List()));
        app.MapGet("/api/monitors/{id}", (string id) => _monitorService.Get(id) is { } monitor ? Results.Json(monitor) : Results.NotFound());
        app.MapPut("/api/monitors/{id}", UpdateMonitorAsync);
        app.MapPost("/api/monitors/{id}/connect", (string id) => RunMonitorAction(() => _monitorService.Connect(id)));
        app.MapPost("/api/monitors/{id}/disconnect", (string id) => RunMonitorAction(() => _monitorService.Disconnect(id)));
        app.MapPost("/api/monitors/{id}/uninstall", (string id) => RunMonitorUninstall(id));
    }

    private object CreateStatusModel()
    {
        var states = _statusProvider();
        IReadOnlyList<MonitorSnapshot> monitors;
        try { monitors = _monitorService.List(); } catch { monitors = []; }
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
            monitors = new { installed = monitors.Count(x => x.Installed), connected = monitors.Count(x => x.Connected) },
            remote = new { clients = 0, enabled = monitors.Any(x => x.Configuration.RemoteAccess != RemoteAccessMode.Disabled) },
            runtime = new { framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription },
            links = new { github = ProjectInfo.RepositoryUrl, documentation = ProjectInfo.DocumentationUrl, guide = ProjectInfo.GuideUrl },
        };
    }

    private IResult StatusPage()
    {
        var body = """
<div class="page"><h1>Virtual Monitors Universe</h1><div class="muted" id="version"></div>
<h2>Services</h2><div id="services" class="cards"></div>
<h2>Overview</h2><div class="stats"><div><strong id="installed">0</strong><span>Installed Monitors</span></div><div><strong id="connected">0</strong><span>Connected Monitors</span></div><div><strong id="clients">0</strong><span>Remote Clients</span></div><div><strong id="remote">Disabled</strong><span>Remote Access</span></div></div>
<h2>Project</h2><p><a id="github" target="_blank" rel="noreferrer">GitHub</a> · <a id="documentation" target="_blank" rel="noreferrer">Documentation</a> · <a id="guide" target="_blank" rel="noreferrer">User Guide</a></p></div>
<script>async function refreshStatus(){const s=await fetch('/api/status',{cache:'no-store'}).then(r=>r.json());document.getElementById('version').textContent='Version '+s.version;document.getElementById('services').innerHTML=s.services.map(x=>`<div class="service"><span class="dot ${x.running?'on':'off'}"></span><span>${x.name}</span><b>${x.running?'Running':'Stopped'}</b></div>`).join('');document.getElementById('installed').textContent=s.monitors.installed;document.getElementById('connected').textContent=s.monitors.connected;document.getElementById('clients').textContent=s.remote.clients;document.getElementById('remote').textContent=s.remote.enabled?'Enabled':'Disabled';document.getElementById('github').href=s.links.github;document.getElementById('documentation').href=s.links.documentation;document.getElementById('guide').href=s.links.guide;}refreshStatus();setInterval(refreshStatus,1500);</script>
""";
        return HtmlShell("Status", body);
    }

    private IResult SettingsPage()
    {
        var body = """
<div class="page"><h1>Settings</h1><div id="error" class="error"></div>
<form id="form"><table class="settings"><thead><tr><th>Service</th><th>Interface</th><th>Port</th></tr></thead><tbody>
<tr><td>VMU Server</td><td><select id="vmuInterface"><option>localhost</option><option>any</option></select></td><td><input id="vmuPort" type="number" min="1" max="65535"></td></tr>
<tr><td>Web Server</td><td><select id="webInterface"><option>localhost</option><option>any</option></select></td><td><input id="webPort" type="number" min="1" max="65535"></td></tr>
<tr><td>Web Socket</td><td><select id="socketInterface"><option>localhost</option><option>any</option></select></td><td><input id="socketPort" type="number" min="1" max="65535"></td></tr></tbody></table>
<label class="row">Log Retention <input id="retention" type="number" min="1" max="3650"> days</label>
<fieldset><legend>On Exit</legend><div class="formgrid"><label for="monitorAction">Monitors</label><select id="monitorAction"><option>Disconnect</option><option>Keep</option><option>Uninstall</option></select><label for="restore">Restore Services</label><input id="restore" type="checkbox"></div></fieldset>
<div class="actions"><button type="submit">Save</button><button type="button" onclick="location.reload()">Cancel</button></div></form></div>
<script>
let original;async function load(){original=await fetch('/api/settings').then(r=>r.json());vmuInterface.value=original.vmu.interface;vmuPort.value=original.vmu.port;webInterface.value=original.web.interface;webPort.value=original.web.port;socketInterface.value=original.socket.interface;socketPort.value=original.socket.port;retention.value=Math.ceil(original.logging.retentionMinutes/1440);monitorAction.value=original.exit.monitorAction;restore.checked=original.exit.restoreServices;}
form.onsubmit=async e=>{e.preventDefault();error.textContent='';const data={vmu:{interface:vmuInterface.value,port:+vmuPort.value},web:{interface:webInterface.value,port:+webPort.value},socket:{interface:socketInterface.value,port:+socketPort.value},logging:{retentionMinutes:+retention.value*1440},exit:{monitorAction:monitorAction.value,restoreServices:restore.checked},serviceState:original.serviceState};const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});if(!r.ok){const x=await r.json();error.textContent=x.error||'Settings could not be saved.';return;}const result=await r.json();if(result.restartRequired)window.vmuWaitForEndpoint(result.targetUrl,result.waitMilliseconds,'settings');else location.reload();};load();
</script>
""";
        return HtmlShell("Settings", body);
    }

    private IResult MonitorsPage()
    {
        var body = """
<div class="page"><h1>Monitors</h1><div id="monitorGrid" class="monitorgrid"></div></div>
<script>
function esc(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML}
async function loadMonitors(){const list=await fetch('/api/monitors',{cache:'no-store'}).then(r=>r.json());monitorGrid.innerHTML=list.map(m=>`<a class="monitorcard" href="/monitors/${encodeURIComponent(m.configuration.vmuId)}"><div class="monitorpic"><div class="screen"></div><div class="stand"></div></div><h3>${esc(m.configuration.friendlyName)}</h3><div>${m.width} × ${m.height}</div><div><span class="dot ${m.connected?'on':'off'}"></span> ${m.connected?'On':'Off'}</div></a>`).join('')+`<a class="monitorcard addmonitor" href="/monitors/new"><div class="plus">+</div><h3>Add Monitor</h3></a>`;}loadMonitors();
</script>
""";
        return HtmlShell("Monitors", body);
    }

    private IResult NewMonitorPage()
    {
        var body = """
<div class="page"><h1>Add Monitor</h1><div class="notice"><strong>Provisioning is not enabled yet.</strong><p>The current validated VMU Core can control an existing VDD target, but it cannot safely create and bind one persistent <code>vmu_id</code> without relying on unstable Windows display numbering. VMU will not guess here. Existing virtual displays are discovered automatically and appear on the Monitors page.</p></div><p><a href="/monitors">Back to Monitors</a></p></div>
""";
        return HtmlShell("Add Monitor", body);
    }

    private IResult MonitorPropertiesPage(string id)
    {
        var safeId = System.Net.WebUtility.HtmlEncode(id);
        var body = $$"""
<div class="page"><h1>Monitor Properties</h1><div id="error" class="error"></div><form id="props" class="properties">
<label>Name <input id="friendlyName"></label><label>Resolution <select id="resolution"><option value="1280x720">1280 × 720</option><option value="1920x1080">1920 × 1080</option><option value="2560x1440">2560 × 1440</option><option value="3840x2160">3840 × 2160</option></select></label><label>Refresh Rate <input id="refreshRate" type="number" min="1" max="1000"></label><label>Orientation <select id="portrait"><option value="false">Landscape</option><option value="true">Portrait</option></select></label>
<fieldset><legend>Remote Access</legend><label>Mode <select id="remoteAccess"><option>Disabled</option><option>Presentation</option><option>Collaboration</option></select></label><p class="hint">Presentation is view-only. Collaboration also permits mouse, keyboard and shared clipboard control.</p></fieldset>
<fieldset><legend>Remote Access Security</legend><label><input id="passwordEnabled" type="checkbox"> Password</label><input id="password" type="password" placeholder="New password (leave blank to keep current)"><label><input id="apiKeyEnabled" type="checkbox"> API Key</label><div class="keyrow"><input id="apiKey" readonly><button type="button" id="regen">Generate New Key</button></div><label><input id="approvalEnabled" type="checkbox"> White/Black List Approval</label><p class="hint">Unknown network clients will require Allow, Defer or Block approval when the remote-access server is implemented.</p></fieldset>
<div class="actions"><button type="submit">Save</button><button type="button" id="connect">Connect</button><button type="button" id="disconnect">Disconnect</button><button type="button" id="uninstall" class="danger">Uninstall</button><a class="buttonlink" id="terminal">Open Terminal</a></div></form></div>
<script>
const id={{JsonSerializer.Serialize(id)}};let current,regenKey=false;
async function load(){current=await fetch('/api/monitors/'+encodeURIComponent(id),{cache:'no-store'}).then(r=>{if(!r.ok)throw new Error('Monitor not found');return r.json()});friendlyName.value=current.configuration.friendlyName;const w=current.configuration.width,h=current.configuration.height;resolution.value=`${w}x${h}`;if(!resolution.value){const o=document.createElement('option');o.value=`${w}x${h}`;o.textContent=`${w} × ${h}`;resolution.append(o);resolution.value=o.value;}refreshRate.value=current.configuration.refreshRate;portrait.value=String(current.configuration.portrait);remoteAccess.value=current.configuration.remoteAccess;passwordEnabled.checked=current.configuration.passwordEnabled;apiKeyEnabled.checked=current.configuration.apiKeyEnabled;apiKey.value=current.configuration.apiKey||'';approvalEnabled.checked=current.configuration.approvalEnabled;connect.disabled=!current.installed||current.connected;disconnect.disabled=!current.installed||!current.connected;uninstall.disabled=!current.installed;terminal.href='/monitor/'+encodeURIComponent(id);}
props.onsubmit=async e=>{e.preventDefault();error.textContent='';const [width,height]=resolution.value.split('x').map(Number);const data={friendlyName:friendlyName.value,width,height,refreshRate:+refreshRate.value,portrait:portrait.value==='true',remoteAccess:remoteAccess.value,passwordEnabled:passwordEnabled.checked,password:password.value||null,apiKeyEnabled:apiKeyEnabled.checked,regenerateApiKey:regenKey,approvalEnabled:approvalEnabled.checked};const r=await fetch('/api/monitors/'+encodeURIComponent(id),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});if(!r.ok){const x=await r.json();error.textContent=x.error||'Could not save monitor properties.';return;}regenKey=false;password.value='';await load();};
regen.onclick=()=>{apiKeyEnabled.checked=true;regenKey=true;apiKey.value='New unique key will be generated on Save';};
async function action(name,question){if(question&&!confirm(question))return;const r=await fetch('/api/monitors/'+encodeURIComponent(id)+'/'+name,{method:'POST'});if(!r.ok){const x=await r.json();alert(x.error||'Monitor operation failed.');return;}await load();}
connect.onclick=()=>action('connect');disconnect.onclick=()=>action('disconnect',`Are you sure you want to disconnect monitor '${current.configuration.friendlyName}'?`);uninstall.onclick=()=>action('uninstall',`Are you sure you want to uninstall monitor '${current.configuration.friendlyName}'?\n\nThe virtual monitor will be removed from Windows.`);load().catch(e=>error.textContent=e.message);
</script>
""";
        return HtmlShell($"Monitor {safeId}", body);
    }

    private async Task<IResult> UpdateMonitorAsync(HttpRequest request, string id)
    {
        try
        {
            var input = await request.ReadFromJsonAsync<MonitorUpdateRequest>();
            if (input is null) return Results.BadRequest(new { error = "Invalid monitor properties payload." });
            if (!Enum.TryParse<RemoteAccessMode>(input.RemoteAccess, true, out var remote)) return Results.BadRequest(new { error = "Invalid remote access mode." });
            return Results.Json(_monitorService.UpdateProperties(id, input.FriendlyName, input.Width, input.Height, input.RefreshRate, input.Portrait, remote, input.PasswordEnabled, input.Password, input.ApiKeyEnabled, input.RegenerateApiKey, input.ApprovalEnabled));
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static IResult RunMonitorAction(Func<MonitorSnapshot> action)
    {
        try { return Results.Json(action()); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private IResult RunMonitorUninstall(string id)
    {
        try { _monitorService.Uninstall(id); return Results.NoContent(); }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private async Task<IResult> SaveSettingsAsync(HttpRequest request)
    {
        var proposed = await request.ReadFromJsonAsync<ServerSettings>();
        if (proposed is null) return Results.BadRequest(new { error = "Invalid settings payload." });
        var endpoints = new[] { ("VMU Server", proposed.Vmu.Port), ("Web Server", proposed.Web.Port), ("Web Socket", proposed.Socket.Port) };
        if (endpoints.Any(x => x.Port is < 1 or > 65535)) return Results.Conflict(new { error = "Service ports must be between 1 and 65535." });
        var duplicate = endpoints.GroupBy(x => x.Port).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) return Results.Conflict(new { error = $"Port {duplicate.Key} is configured for more than one VMU service." });
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(x => x.Port).ToHashSet();
        var blocked = endpoints.FirstOrDefault(x => active.Contains(x.Port) && !_isOwnedListener(x.Port));
        if (blocked != default) return Results.Conflict(new { error = $"{blocked.Item1} port {blocked.Port} is already used by another TCP listener on this computer." });
        var result = await _settingsSaver(proposed);
        return Results.Json(result);
    }

    private IResult LogPage()
    {
        var body = """
<div class="logpage"><div class="filters"><h3>Filters</h3><label><input type="checkbox" data-service="VMU" checked> VMU <span class="dot" id="s-VMU"></span></label><label><input type="checkbox" data-service="VMU_SERVER" checked> VMU Server <span class="dot" id="s-VMU_SERVER"></span></label><label><input type="checkbox" data-service="WEB" checked> Web Server <span class="dot" id="s-WEB"></span></label><label><input type="checkbox" data-service="SOCKET" checked> Socket Server <span class="dot" id="s-SOCKET"></span></label></div>
<div class="logmain"><div class="toolbar"><div class="searchbox"><input id="search" placeholder="Search..."><button id="clearSearch" title="Clear Search">×</button></div></div><div class="tablewrap"><table id="log"><thead><tr><th data-col="timestamp">Timecode <span class="sort"></span></th><th data-col="level">Level <span class="sort"></span></th><th data-col="service">Service <span class="sort"></span></th><th data-col="monitorId">Monitor <span class="sort"></span></th><th data-col="event">Event <span class="sort"></span></th><th data-col="message">Message <span class="sort"></span></th></tr></thead><tbody></tbody></table></div><div class="logfooter"><label><input id="tail" type="checkbox" checked> Always at end</label><div><button id="exportXlsx">Export XLSX</button><button id="exportCsv">Export CSV</button><button id="exportTxt">Export TXT</button><button id="clear">Clear</button></div></div></div></div>
<dialog id="detail"><pre id="detailText"></pre><button onclick="detail.close()">Close</button></dialog>
<script>
let rows=[],sortCol=null,sortDir=1,lastSelected=null;const tbody=document.querySelector('#log tbody'),search=document.getElementById('search'),tail=document.getElementById('tail');
function selected(){return [...document.querySelectorAll('[data-service]:checked')].map(x=>x.dataset.service)}function query(){const p=new URLSearchParams();selected().forEach(x=>p.append('service',x));if(search.value)p.set('q',search.value);return p}
async function refresh(){const [data,status]=await Promise.all([fetch('/api/log?'+query()).then(r=>r.json()),fetch('/api/status',{cache:'no-store'}).then(r=>r.json())]);rows=data;status.services.forEach(x=>{const d=document.getElementById('s-'+x.key);if(d)d.className='dot '+(x.running?'on':'off')});render();}
function value(x,k){if(k==='timestamp')return new Date(x[k]).getTime();return String(x[k]??'').toLocaleLowerCase()}function render(){let data=[...rows];if(!tail.checked&&sortCol)data.sort((a,b)=>{const av=value(a,sortCol),bv=value(b,sortCol);return (av<bv?-1:av>bv?1:0)*sortDir});document.querySelectorAll('#log th .sort').forEach(x=>x.textContent='');if(!tail.checked&&sortCol){const th=document.querySelector(`#log th[data-col="${sortCol}"] .sort`);if(th)th.textContent=sortDir===1?'▲':'▼';}tbody.innerHTML=data.map(x=>`<tr data-id="${x.id}"><td>${new Date(x.timestamp).toLocaleString()}</td><td>${x.level}</td><td>${x.service}</td><td>${x.monitorId??''}</td><td>${x.event}</td><td>${escapeHtml(x.message)}</td></tr>`).join('');const target=tail.checked?tbody.lastElementChild:(lastSelected&&tbody.querySelector(`[data-id="${lastSelected}"]`));if(target){target.classList.add('selected');if(tail.checked)target.scrollIntoView({block:'nearest'});lastSelected=target.dataset.id;}}
function escapeHtml(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML}tbody.onclick=e=>{const tr=e.target.closest('tr');if(!tr)return;lastSelected=tr.dataset.id;tbody.querySelectorAll('tr').forEach(x=>x.classList.remove('selected'));tr.classList.add('selected')};tbody.ondblclick=async e=>{const tr=e.target.closest('tr');if(!tr)return;const x=await fetch('/api/log/'+tr.dataset.id).then(r=>r.json());detailText.textContent=JSON.stringify(x,null,2);detail.showModal()};document.querySelectorAll('[data-service]').forEach(x=>x.onchange=refresh);search.oninput=refresh;clearSearch.onclick=()=>{search.value='';refresh()};tail.onchange=()=>{if(!tail.checked){sortCol='timestamp';sortDir=1}else sortCol=null;render()};document.querySelectorAll('th[data-col]').forEach(th=>th.onclick=()=>{if(tail.checked)return;if(sortCol===th.dataset.col)sortDir*=-1;else{sortCol=th.dataset.col;sortDir=1}render()});clear.onclick=async()=>{if(confirm('Clear the log?')){await fetch('/api/log',{method:'DELETE'});refresh()}};function exp(f){location.href='/api/log/export/'+f+'?'+query()}exportXlsx.onclick=()=>exp('xlsx');exportCsv.onclick=()=>exp('csv');exportTxt.onclick=()=>exp('txt');refresh();setInterval(refresh,1000);
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
        var commonScript = """
<script>
const navButton=document.getElementById('navButton'),navMenu=document.getElementById('navMenu');navButton.onclick=e=>{e.stopPropagation();navMenu.classList.toggle('open')};document.addEventListener('click',()=>navMenu.classList.remove('open'));navMenu.onclick=e=>e.stopPropagation();
let vmuHealthFailures=0,vmuRecoveryRunning=false;async function vmuHealth(){try{const r=await fetch('/api/health',{cache:'no-store'});if(!r.ok)throw new Error();vmuHealthFailures=0;}catch{vmuHealthFailures++;if(vmuHealthFailures>=2&&!vmuRecoveryRunning)window.vmuWaitForEndpoint(location.origin+'/',10000,null);}}setInterval(vmuHealth,1000);
window.vmuWaitForEndpoint=async function(target,waitMs,path){if(vmuRecoveryRunning)return;vmuRecoveryRunning=true;const overlay=document.getElementById('connectionOverlay'),bar=document.getElementById('connectionBar'),headline=document.getElementById('connectionHeadline'),message=document.getElementById('connectionMessage'),retry=document.getElementById('connectionRetry');overlay.classList.add('show');headline.textContent='Restarting Web Server...';message.textContent='Waiting for VMU to become available.';retry.classList.add('hidden');const started=Date.now();const timer=setInterval(()=>bar.style.width=Math.min(99,Math.round((Date.now()-started)/waitMs*100))+'%',100);while(Date.now()-started<waitMs){try{await fetch(target+'api/health',{mode:'no-cors',cache:'no-store'});clearInterval(timer);bar.style.width='100%';message.textContent='Web Server is ready.';setTimeout(()=>{location.href=target+(path||location.pathname.replace(/^\//,''));},250);return;}catch{}await new Promise(r=>setTimeout(r,350));}clearInterval(timer);bar.style.width='100%';headline.textContent='Connection to VMU was lost';message.textContent='The Web Server did not answer before the timeout. Target: '+target;retry.classList.remove('hidden');retry.onclick=()=>{vmuRecoveryRunning=false;bar.style.width='0';window.vmuWaitForEndpoint(target,waitMs,path);};};
</script>
""";
        var css = """
*{box-sizing:border-box}body{font-family:Segoe UI,Arial,sans-serif;margin:0;color:#202124;background:#f5f6f8}nav{height:60px;background:#202124;padding:8px 18px;display:flex;align-items:center}.navwrap{position:relative}.gear{width:38px;height:38px;border:0;background:transparent;color:white;font-size:20px;border-radius:5px;cursor:pointer}.gear:hover{background:#34373b}.navmenu{display:none;position:absolute;top:42px;left:0;min-width:150px;background:white;border:1px solid #bbb;border-radius:6px;box-shadow:0 5px 18px #0004;padding:5px;z-index:1000}.navmenu.open{display:block}.navmenu a{display:block;color:#202124;text-decoration:none;padding:8px 10px;border-radius:4px}.navmenu a:hover{background:#eef1f5}.page{max-width:1000px;margin:30px auto;padding:0 20px}.muted{color:#687078}.cards,.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}.service,.stats>div{background:white;border:1px solid #d9dde3;border-radius:7px;padding:14px;display:flex;gap:10px;align-items:center;text-align:center;justify-content:center}.service b{margin-left:8px}.stats>div{flex-direction:column;align-items:center}.stats strong{font-size:24px}.dot{display:inline-block;width:11px;height:11px;border-radius:50%;background:#aaa}.dot.on{background:#25a746}.dot.off{background:#aaa}.settings{border-collapse:collapse;background:white}.settings th,.settings td{padding:7px 10px;text-align:left}.settings input,.settings select,.row input,.row select,.properties input,.properties select{padding:6px}.row{display:flex;gap:10px;align-items:center;margin:14px 0}fieldset{border:1px solid #b8bec7;border-radius:4px;padding:12px;margin-top:18px}.formgrid{display:grid;grid-template-columns:150px 180px;gap:10px;align-items:center}.actions{display:flex;gap:8px;margin-top:18px;align-items:center}.actions button,.logfooter button,dialog button,.buttonlink,.keyrow button{padding:7px 14px}.buttonlink{border:1px solid #aaa;border-radius:3px;text-decoration:none;color:#202124;background:#f4f4f4}.danger{color:#9b1c1c}.error{color:#b3261e;margin:10px 0}.hidden{display:none!important}.notice{background:white;border:1px solid #d9dde3;border-radius:7px;padding:16px}.monitorgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:16px}.monitorcard{background:white;border:1px solid #d9dde3;border-radius:8px;padding:18px;text-align:center;color:#202124;text-decoration:none;min-height:210px}.monitorcard:hover{box-shadow:0 3px 10px #0002}.monitorpic{height:105px;position:relative}.screen{width:125px;height:78px;border:7px solid #4b5563;border-radius:5px;margin:auto;background:#dceefa}.stand{width:45px;height:8px;background:#4b5563;margin:8px auto}.plus{font-size:92px;line-height:120px}.properties>label{display:grid;grid-template-columns:160px 1fr;gap:12px;align-items:center;margin:10px 0}.properties fieldset label{display:flex;gap:8px;align-items:center;margin:10px 0}.hint{color:#687078;font-size:13px}.keyrow{display:flex;gap:8px}.keyrow input{flex:1}.logpage{display:grid;grid-template-columns:190px 1fr;height:calc(100vh - 60px);padding:10px;gap:8px}.filters{background:#eee;border:1px solid #ccc;padding:8px}.filters label{display:flex;align-items:center;gap:5px;padding:4px}.filters .dot{margin-left:auto}.logmain{display:flex;flex-direction:column;min-width:0}.toolbar{display:flex;justify-content:flex-end;margin-bottom:6px}.searchbox{display:flex}.searchbox input{width:260px;padding:6px}.searchbox button{width:30px}.tablewrap{background:white;border:1px solid #bbb;overflow:auto;flex:1}#log{border-collapse:collapse;width:100%;font:13px Consolas,monospace}#log th{position:sticky;top:0;background:#e1e6ec;border-bottom:1px solid #aaa;padding:6px;text-align:left;cursor:pointer}#log td{padding:5px;border-bottom:1px solid #eee;white-space:nowrap}#log tr.selected{background:#cfe4ff}.sort{font-size:10px}.logfooter{display:flex;justify-content:space-between;align-items:center;padding-top:8px}.logfooter>div{display:flex;gap:6px}dialog{max-width:800px;width:70%;border:1px solid #888;border-radius:6px}dialog pre{white-space:pre-wrap}.connection{display:none;position:fixed;inset:0;background:#f5f6f8f5;z-index:5000;align-items:center;justify-content:center}.connection.show{display:flex}.connectionbox{width:min(600px,90vw);text-align:center;background:white;border:1px solid #ccd1d8;border-radius:9px;padding:28px}.connectionmonitor{width:95px;height:60px;border:8px solid #6b7280;border-radius:6px;margin:0 auto 18px;position:relative}.connectionmonitor:after{content:'×';position:absolute;font-size:48px;color:#b3261e;left:22px;top:-8px}.progress{height:16px;background:#ddd;border-radius:9px;overflow:hidden;margin:18px 0}.progress div{height:100%;width:0;background:#2d7dd2;transition:width .1s}
""";
        var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + title + " - VMU</title><style>" + css + "</style></head><body><nav><div class=\"navwrap\"><button id=\"navButton\" class=\"gear\" title=\"Navigation\">⚙</button><div id=\"navMenu\" class=\"navmenu\"><a href=\"/\">Status</a><a href=\"/monitors\">Monitors</a><a href=\"/settings\">Settings</a><a href=\"/log\">View Log</a></div></div></nav>" + body + "<div id=\"connectionOverlay\" class=\"connection\"><div class=\"connectionbox\"><div class=\"connectionmonitor\"></div><h1 id=\"connectionHeadline\">Restarting Web Server...</h1><div class=\"progress\"><div id=\"connectionBar\"></div></div><p id=\"connectionMessage\"></p><button id=\"connectionRetry\" class=\"hidden\">Retry</button></div></div>" + commonScript + "</body></html>";
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
