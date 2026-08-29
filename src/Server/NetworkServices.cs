using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VirtualMonitorsUniverse.Server;

internal sealed record WebSettingsSaveResult(string TargetUrl, bool RestartRequired, int WaitMilliseconds);
internal sealed record MonitorCreateRequest(string? Name, string? Title, int Width, int Height, int RefreshRate, bool Portrait, string? AvatarAnimal);
internal sealed record MonitorUpdateRequest(string? Name, string? Title, int Width, int Height, int RefreshRate, bool Portrait, string RemoteAccess, string SecurityMode, string? Password, bool RegenerateApiKey, bool CollaborationClipboard, bool CollaborationMouse, bool CollaborationKeyboard);
internal sealed record AccessRuleRequest(string ClientId, string? IpAddress, string? MacAddress, string? ComputerName, string? UserName, string Permission);

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
            LogStore.Write("ERROR", ServiceKey, "SERVICE_START_FAILED", $"{Name} failed to start: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { endpoint.Interface, endpoint.Port, exception = ex.ToString() }));
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_application is null) return;
        var app = _application; _application = null; var port = ActivePort; ActivePort = null;
        try { await app.StopAsync(TimeSpan.FromSeconds(5)); await app.DisposeAsync(); LogStore.Write("INFO", ServiceKey, "SERVICE_STOP", $"{Name} stopped" + (port is null ? string.Empty : $" (port {port})")); }
        catch (Exception ex) { LogStore.Write("ERROR", ServiceKey, "SERVICE_STOP_FAILED", $"{Name} failed to stop cleanly: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { exception = ex.ToString() })); throw; }
    }

    public async Task RestartAsync(ServiceEndpointSettings endpoint) { if (_application is not null) await StopAsync(); await StartAsync(endpoint); }

    private async Task<WebApplication> BuildAndStartAsync(ServiceEndpointSettings endpoint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(endpoint.Interface.Equals("any", StringComparison.OrdinalIgnoreCase) ? $"http://0.0.0.0:{endpoint.Port}" : $"http://127.0.0.1:{endpoint.Port}");
        ConfigureServices(builder.Services);
        var app = builder.Build(); ConfigureApplication(app); await app.StartAsync(); return app;
    }

    protected virtual void ConfigureServices(IServiceCollection services) { }
    protected abstract void ConfigureApplication(WebApplication application);

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;
        var app = _application; _application = null; ActivePort = null;
        await app.StopAsync(TimeSpan.FromSeconds(2)); await app.DisposeAsync();
    }
}

internal sealed class WebServerService : NetworkService
{
    private static readonly string[] ServiceKeys = ["VMU", "VMU_SERVER", "WEB", "SOCKET"];
    private readonly MonitorApplicationService _monitorService;
    private readonly MonitorThumbnailService _thumbnails = new();
    private readonly Func<IReadOnlyDictionary<string, bool>> _statusProvider;
    private readonly Func<ServerSettings> _settingsProvider;
    private readonly Func<ServerSettings, Task<WebSettingsSaveResult>> _settingsSaver;
    private readonly Func<int, bool> _isOwnedListener;

    public WebServerService(LogStore logStore, MonitorApplicationService monitorService, Func<IReadOnlyDictionary<string, bool>> statusProvider,
        Func<ServerSettings> settingsProvider, Func<ServerSettings, Task<WebSettingsSaveResult>> settingsSaver, Func<int, bool> isOwnedListener)
        : base("Web Server", "WEB", logStore)
    { _monitorService = monitorService; _statusProvider = statusProvider; _settingsProvider = settingsProvider; _settingsSaver = settingsSaver; _isOwnedListener = isOwnedListener; }

    protected override void ConfigureApplication(WebApplication app)
    {
        app.MapGet("/", StatusPage);
        app.MapGet("/settings", SettingsPage);
        app.MapGet("/monitors", MonitorsPage);
        app.MapGet("/monitors/new", NewMonitorPage);
        app.MapGet("/monitors/{id}", MonitorPropertiesPage);
        app.MapGet("/log", LogPage);
        app.MapGet("/monitor/{id}", TerminalPage);

        app.MapGet("/api/health", () => Results.Json(new { status = "ok", version = ProjectInfo.Version }));
        app.MapGet("/api/status", () => Results.Json(CreateStatusModel()));
        app.MapGet("/api/settings", () => Results.Json(_settingsProvider()));
        app.MapPost("/api/settings", SaveSettingsAsync);
        app.MapGet("/api/log", (HttpRequest request) => Results.Json(ReadLog(request)));
        app.MapGet("/api/log/{id:long}", (long id) => LogStore.ReadById(id) is { } entry ? Results.Json(entry) : Results.NotFound());
        app.MapDelete("/api/log", () => { LogStore.Clear(); return Results.NoContent(); });
        app.MapGet("/api/log/export/{format}", ExportLog);

        app.MapGet("/api/monitors", () => Results.Json(_monitorService.List()));
        app.MapGet("/api/monitors/name-available/{name}", (string name, string? except) => Results.Json(new { available = _monitorService.NameAvailable(name, except) }));
        app.MapGet("/api/monitors/suggest", (string? name, string? title) => { try { var x = _monitorService.SuggestIdentity(name, title); return Results.Json(new { x.Name, x.Title }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
        app.MapPost("/api/monitors", CreateMonitorAsync);
        app.MapGet("/api/monitors/{id}", GetMonitorResult);
        app.MapPut("/api/monitors/{id}", UpdateMonitorAsync);
        app.MapPost("/api/monitors/{id}/connect", (string id) => RunMonitorAction(() => _monitorService.Connect(id)));
        app.MapPost("/api/monitors/{id}/disconnect", (string id) => RunMonitorAction(() => _monitorService.Disconnect(id)));
        app.MapPost("/api/monitors/{id}/uninstall", RunMonitorUninstall);
        app.MapGet("/api/monitors/{id}/thumbnail", GetThumbnailAsync);
        app.MapGet("/api/monitors/{id}/avatar", GetAvatar);
        app.MapPost("/api/monitors/{id}/avatar/animal/{animal}", (string id, string animal) => RunMonitorAction(() => _monitorService.SetAnimalAvatar(id, animal)));
        app.MapPost("/api/monitors/{id}/avatar/upload", UploadAvatarAsync);
        app.MapGet("/api/monitors/{id}/access-rules", (string id) => Results.Json(_monitorService.ListAccessRules(id)));
        app.MapPost("/api/monitors/{id}/access-rules", UpsertAccessRuleAsync);
        app.MapDelete("/api/monitors/{id}/access-rules/{ruleId:long}", (string id, long ruleId) => { try { _monitorService.DeleteAccessRule(id, ruleId); return Results.NoContent(); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
    }

    private object CreateStatusModel()
    {
        var states = _statusProvider();
        IReadOnlyList<MonitorSnapshot> monitors;
        try { monitors = _monitorService.List(); } catch { monitors = []; }
        return new
        {
            application = ProjectInfo.ProductName, version = ProjectInfo.Version,
            services = new[]
            {
                new { key="VMU", name="VMU", running=states.GetValueOrDefault("VMU") },
                new { key="VMU_SERVER", name="VMU Server", running=states.GetValueOrDefault("VMU_SERVER") },
                new { key="WEB", name="Web Server", running=states.GetValueOrDefault("WEB") },
                new { key="SOCKET", name="Socket Server", running=states.GetValueOrDefault("SOCKET") },
            },
            monitors = new { installed = monitors.Count(x => x.Installed), connected = monitors.Count(x => x.Connected) },
            remote = new { enabled = monitors.Any(x => x.Configuration.RemoteAccess != RemoteAccessMode.Disabled), clients = 0 },
            runtime = new { framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription },
            links = new { github = ProjectInfo.RepositoryUrl, documentation = ProjectInfo.DocumentationUrl, guide = ProjectInfo.GuideUrl, bugs = ProjectInfo.RepositoryUrl.TrimEnd('/') + "/issues" },
        };
    }

    private IResult StatusPage()
    {
        var body = """
<div class="page"><h1>Virtual Monitors Universe</h1><div class="muted" id="version"></div>
<h2>Services</h2><div id="services" class="cards"></div>
<h2>Overview</h2><div class="stats"><a href="/monitors"><strong id="installed">0</strong><span>Installed Monitors</span></a><a href="/monitors"><strong id="connected">0</strong><span>Connected Monitors</span></a><div><strong id="remote">Disabled</strong><span>Remote Access</span></div><div><strong id="clients">0</strong><span>Remote Clients</span></div></div>
<h2>Project</h2><div class="projecttiles"><a id="github" target="_blank" rel="noreferrer"><b>◆</b><span>GitHub</span></a><a id="documentation" target="_blank" rel="noreferrer"><b>📚</b><span>Documentation</span></a><a id="guide" target="_blank" rel="noreferrer"><b>📖</b><span>User Guide</span></a><a id="bugs" target="_blank" rel="noreferrer"><b>🐞</b><span>Report a Bug</span></a></div></div>
<script>async function refreshStatus(){const s=await fetch('/api/status',{cache:'no-store'}).then(r=>r.json());version.textContent='Version '+s.version;services.innerHTML=s.services.map(x=>`<div class="service"><span class="dot ${x.running?'on':'off'}"></span><span>${x.name}</span><b>${x.running?'Running':'Stopped'}</b></div>`).join('');installed.textContent=s.monitors.installed;connected.textContent=s.monitors.connected;remote.textContent=s.remote.enabled?'Enabled':'Disabled';clients.textContent=s.remote.clients;github.href=s.links.github;documentation.href=s.links.documentation;guide.href=s.links.guide;bugs.href=s.links.bugs;}refreshStatus();setInterval(refreshStatus,1500);</script>
""";
        return HtmlShell("Status", body);
    }

    private IResult SettingsPage()
    {
        var body = """
<div class="page"><h1>Settings</h1><div id="error" class="error"></div><form id="form" class="settingsform">
<fieldset><legend>Services</legend><table class="settings"><thead><tr><th>Service</th><th>Interface</th><th>Port</th></tr></thead><tbody>
<tr><td>VMU Server</td><td><select id="vmuInterface"><option>localhost</option><option>any</option></select></td><td><input id="vmuPort" type="number" min="1" max="65535"></td></tr>
<tr><td>Web Server</td><td><select id="webInterface"><option>localhost</option><option>any</option></select></td><td><input id="webPort" type="number" min="1" max="65535"></td></tr>
<tr><td>Web Socket</td><td><select id="socketInterface"><option>localhost</option><option>any</option></select></td><td><input id="socketPort" type="number" min="1" max="65535"></td></tr></tbody></table></fieldset>
<fieldset><legend>Web and Logging</legend><div class="formgrid"><label for="retention">Log Retention</label><div><input id="retention" type="number" min="1" max="3650"> days</div><label for="previewRefresh">Monitor Preview</label><select id="previewRefresh"><option value="0">Manual only</option><option value="15">15 seconds</option><option value="30">30 seconds</option><option value="60">1 minute</option><option value="120">2 minutes</option><option value="300">5 minutes</option><option value="600">10 minutes</option></select></div></fieldset>
<fieldset><legend>On Exit</legend><div class="formgrid"><label for="monitorAction">Monitors</label><select id="monitorAction"><option>Disconnect</option><option>Keep</option><option>Uninstall</option></select><label for="restore">Restore Services</label><div><input id="restore" type="checkbox"></div></div></fieldset>
<div class="actions"><button type="submit">Save</button><button type="button" onclick="location.reload()">Cancel</button></div></form></div>
<script>
let original;function dependency(){if(vmuInterface.value==='any')webInterface.value='any';}
async function load(){original=await fetch('/api/settings').then(r=>r.json());vmuInterface.value=original.vmu.interface;vmuPort.value=original.vmu.port;webInterface.value=original.web.interface;webPort.value=original.web.port;socketInterface.value=original.socket.interface;socketPort.value=original.socket.port;retention.value=Math.ceil(original.logging.retentionMinutes/1440);previewRefresh.value=String(original.webUi.monitorPreviewRefreshSeconds);monitorAction.value=original.exit.monitorAction;restore.checked=original.exit.restoreServices;dependency();}
vmuInterface.onchange=dependency;form.onsubmit=async e=>{e.preventDefault();dependency();error.textContent='';const data={vmu:{interface:vmuInterface.value,port:+vmuPort.value},web:{interface:webInterface.value,port:+webPort.value},socket:{interface:socketInterface.value,port:+socketPort.value},logging:{retentionMinutes:+retention.value*1440},webUi:{monitorPreviewRefreshSeconds:+previewRefresh.value},exit:{monitorAction:monitorAction.value,restoreServices:restore.checked},serviceState:original.serviceState};const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});if(!r.ok){const x=await r.json();error.textContent=x.error||'Settings could not be saved.';return;}const result=await r.json();if(result.restartRequired)window.vmuWaitForEndpoint(result.targetUrl,result.waitMilliseconds,'settings');else location.reload();};load();
</script>
""";
        return HtmlShell("Settings", body);
    }

    private IResult MonitorsPage()
    {
        var refresh = _settingsProvider().WebUi.MonitorPreviewRefreshSeconds;
        var body = $$"""
<div class="page"><h1>Monitors</h1><div id="monitorGrid" class="monitorgrid"></div></div>
<script>
const refreshSeconds={{refresh}};function esc(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML}
function avatar(m){return m.configuration.avatarKind==='custom'?`<img class="avatar" src="/api/monitors/${encodeURIComponent(m.configuration.name)}/avatar">`:`<span class="avatarEmoji">${esc(window.vmuAnimalEmoji(m.configuration.avatarValue))}</span>`;}
async function loadMonitors(){const list=await fetch('/api/monitors',{cache:'no-store'}).then(r=>r.json());monitorGrid.innerHTML=list.map(m=>`<a class="monitorcard" href="/monitors/${encodeURIComponent(m.configuration.name)}"><div class="monitorpic"><img class="preview ${m.connected?'':'hidden'}" src="${m.connected?'/api/monitors/'+encodeURIComponent(m.configuration.name)+'/thumbnail?t='+Date.now():''}"><div class="screen ${m.connected?'hidden':''}"></div><div class="stand"></div></div><h3>${avatar(m)} ${esc(m.configuration.title)}</h3><div>${m.width} × ${m.height}</div><div><span class="dot ${m.connected?'on':'off'}"></span> ${m.connected?'On':'Off'}</div></a>`).join('')+`<a class="monitorcard addmonitor" href="/monitors/new"><div class="plus">+</div><h3>Add Monitor</h3></a>`;}
async function refreshPreviews(){if(document.hidden||refreshSeconds===0)return;document.querySelectorAll('.preview').forEach(img=>{const u=new URL(img.src);u.searchParams.set('t',Date.now());img.src=u.toString();});}
loadMonitors();if(refreshSeconds>0)setInterval(refreshPreviews,refreshSeconds*1000);
</script>
""";
        return HtmlShell("Monitors", body);
    }

    private IResult NewMonitorPage()
    {
        var rates = string.Join("", MonitorApplicationService.SupportedRefreshRates.Select(x => $"<option value=\"{x}\"{(x == MonitorApplicationService.RecommendedRefreshRate ? " selected" : "")}>{x} Hz</option>"));
        var animals = string.Join("", MonitorAvatarService.AnimalNames.Select(x => $"<option value=\"{x}\">{System.Net.WebUtility.HtmlEncode(x)}</option>"));
        var body = $$"""
<div class="page"><h1>Add Monitor</h1><div id="error" class="error"></div><form id="newMonitor" class="properties newmonitor">
<label>Name <input id="name" autocomplete="off" pattern="[a-z0-9][a-z0-9-]*" placeholder="canonical-name"></label><div id="nameHint" class="fieldhint"></div>
<label>Title <input id="title" placeholder="Display title"></label>
<label>Avatar <div class="avatarcontrols"><select id="avatarAnimal"><option value="">Random animal</option>{{animals}}</select><input id="avatarFile" type="file" accept=".png,.ico,.gif,image/png,image/gif,image/x-icon"></div></label>
<label>Resolution <select id="resolution"><option value="1280x720">1280 × 720</option><option value="1920x1080" selected>1920 × 1080</option><option value="2560x1440">2560 × 1440</option><option value="3840x2160">3840 × 2160</option></select></label>
<label>Refresh Rate <select id="refreshRate">{{rates}}</select></label><label>Orientation <select id="portrait"><option value="false">Landscape</option><option value="true">Portrait</option></select></label>
<div class="actions"><button id="install" type="submit">Install</button><a class="buttonlink" href="/monitors">Cancel</a></div></form>
<div id="operation" class="operation hidden"><h2>Installing Monitor...</h2><div class="progress"><div id="operationBar"></div></div><p>Windows may ask for administrator confirmation.</p></div></div>
<script>
let nameValid=true,checking=0;function canonicalLocal(v){return /^[a-z0-9][a-z0-9-]*$/.test(v)}
async function validateName(){const v=name.value.trim();name.classList.remove('invalid');nameHint.textContent='';if(!v){nameValid=true;install.disabled=false;return;}if(!canonicalLocal(v)){nameValid=false;name.classList.add('invalid');nameHint.textContent='Use only a-z, 0-9 and hyphen.';install.disabled=true;return;}const n=++checking;const x=await fetch('/api/monitors/name-available/'+encodeURIComponent(v)).then(r=>r.json());if(n!==checking)return;nameValid=x.available;name.classList.toggle('invalid',!nameValid);nameHint.textContent=nameValid?'':`Name '${v}' already exists.`;install.disabled=!nameValid;}
name.oninput=()=>{name.value=name.value.trimStart().toLowerCase().replace(/[^a-z0-9-]/g,'');validateName();};
newMonitor.onsubmit=async e=>{e.preventDefault();await validateName();if(!nameValid)return;error.textContent='';const [width,height]=resolution.value.split('x').map(Number);newMonitor.classList.add('hidden');operation.classList.remove('hidden');const started=Date.now(),timer=setInterval(()=>operationBar.style.width=Math.min(92,Math.round((Date.now()-started)/20000*92))+'%',120);try{const r=await fetch('/api/monitors',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:name.value.trim()||null,title:title.value.trim()||null,width,height,refreshRate:+refreshRate.value,portrait:portrait.value==='true',avatarAnimal:avatarAnimal.value||null})});const x=await r.json();if(!r.ok)throw new Error(x.error||'Monitor could not be installed.');if(avatarFile.files[0]){const fd=new FormData();fd.append('file',avatarFile.files[0]);const ar=await fetch('/api/monitors/'+encodeURIComponent(x.configuration.name)+'/avatar/upload',{method:'POST',body:fd});if(!ar.ok)throw new Error('Monitor was installed, but the custom avatar could not be saved.');}clearInterval(timer);operationBar.style.width='100%';setTimeout(()=>location.href='/monitors/'+encodeURIComponent(x.configuration.name),300);}catch(ex){clearInterval(timer);operation.classList.add('hidden');newMonitor.classList.remove('hidden');error.textContent=ex.message;}};
</script>
""";
        return HtmlShell("Add Monitor", body);
    }

    private IResult MonitorPropertiesPage(string id)
    {
        var monitor = _monitorService.Get(id);
        if (monitor is null) return Results.NotFound();
        if (!id.Equals(monitor.Configuration.Name, StringComparison.OrdinalIgnoreCase)) return Results.Redirect("/monitors/" + Uri.EscapeDataString(monitor.Configuration.Name), true);
        var rates = string.Join("", MonitorApplicationService.SupportedRefreshRates.Select(x => $"<option value=\"{x}\">{x} Hz</option>"));
        var animals = string.Join("", MonitorAvatarService.AnimalNames.Select(x => $"<option value=\"{x}\">{System.Net.WebUtility.HtmlEncode(x)}</option>"));
        var body = $$"""
<div class="page"><h1>Monitor Properties</h1><div id="error" class="error"></div><form id="props" class="properties">
<label>Name <input id="name" pattern="[a-z0-9][a-z0-9-]*"></label><div id="nameHint" class="fieldhint"></div><label>Title <input id="title"></label>
<label>Avatar <div class="avatarcontrols"><span id="avatarPreview"></span><select id="avatarAnimal"><option value="">Keep current</option>{{animals}}</select><input id="avatarFile" type="file" accept=".png,.ico,.gif,image/png,image/gif,image/x-icon"></div></label>
<label>Windows Display <input id="windowsDisplay" readonly></label><label>GDI <input id="gdi" readonly></label><label>Current Position <input id="position" readonly></label>
<label>Resolution <select id="resolution"><option value="1280x720">1280 × 720</option><option value="1920x1080">1920 × 1080</option><option value="2560x1440">2560 × 1440</option><option value="3840x2160">3840 × 2160</option></select></label><label>Refresh Rate <select id="refreshRate">{{rates}}</select></label><label>Orientation <select id="portrait"><option value="false">Landscape</option><option value="true">Portrait</option></select></label>
<fieldset id="remoteBox"><legend>Remote Access</legend><label>Mode <select id="remoteAccess"><option>Disabled</option><option>Presentation</option><option>Collaboration</option></select></label><p id="modeHint" class="hint"></p>
<div id="collaboration" class="inlinechecks"><label><input id="allowClipboard" type="checkbox"> Clipboard</label><label><input id="allowMouse" type="checkbox"> Mouse</label><label><input id="allowKeyboard" type="checkbox"> Keyboard</label></div>
<label>Access <select id="securityMode"><option>Public</option><option>Password</option><option value="ApiKey">API Key</option><option value="Approval">White/Black List Approval</option></select></label>
<div id="passwordRow"><label>Password <input id="password" type="password" placeholder="New password (blank keeps current)"></label></div>
<div id="apiRow"><label>API Key <div class="keyrow"><input id="apiKey" readonly><button type="button" id="regen">Generate New Key</button></div></label></div>
<div id="approvalRow"><table class="accessrules"><thead><tr><th>IP Address</th><th>MAC</th><th>Computer</th><th>User</th><th>Permission</th><th></th></tr></thead><tbody id="rules"></tbody></table><div class="ruleadd"><input id="ruleClient" placeholder="Client/User ID"><input id="ruleIp" placeholder="IP"><input id="ruleMac" placeholder="MAC"><input id="ruleComputer" placeholder="Computer"><input id="ruleUser" placeholder="User"><select id="rulePermission"><option>Deny</option><option>Deferred</option><option>Allow</option></select><button type="button" id="addRule">Add / Update</button></div></div>
</fieldset>
<div class="actions"><button id="save" type="submit" disabled>Save</button><a class="buttonlink" href="/monitors">Cancel</a><button type="button" id="connect">Connect</button><button type="button" id="disconnect">Disconnect</button><button type="button" id="uninstall" class="danger">Uninstall</button><a class="buttonlink" id="terminal">Open Terminal</a></div></form></div>
<script>
const id={{JsonSerializer.Serialize(monitor.Configuration.Name)}};let current,baseline='',regenKey=false,nameValid=true;
function modeHelp(){modeHint.textContent=remoteAccess.value==='Disabled'?'Remote access is disabled.':remoteAccess.value==='Presentation'?'View-only remote display.':'Remote display with the selected collaboration controls.';collaboration.classList.toggle('hidden',remoteAccess.value!=='Collaboration');securityMode.disabled=remoteAccess.value==='Disabled';updateSecurity();}
function updateSecurity(){const disabled=remoteAccess.value==='Disabled';passwordRow.classList.toggle('hidden',securityMode.value!=='Password');apiRow.classList.toggle('hidden',securityMode.value!=='ApiKey');approvalRow.classList.toggle('hidden',securityMode.value!=='Approval');password.disabled=disabled||securityMode.value!=='Password';regen.disabled=disabled||securityMode.value!=='ApiKey';}
function collaborationGuard(){if(remoteAccess.value==='Collaboration'&&!allowClipboard.checked&&!allowMouse.checked&&!allowKeyboard.checked){remoteAccess.value='Presentation';modeHelp();}}
function state(){return JSON.stringify({name:name.value.trim(),title:title.value.trim(),resolution:resolution.value,refreshRate:refreshRate.value,portrait:portrait.value,remoteAccess:remoteAccess.value,securityMode:securityMode.value,clipboard:allowClipboard.checked,mouse:allowMouse.checked,keyboard:allowKeyboard.checked,animal:avatarAnimal.value,file:avatarFile.value,regen:regenKey,password:password.value});}
function dirty(){save.disabled=!nameValid||state()===baseline;}
async function validateName(){const v=name.value.trim();name.classList.remove('invalid');nameHint.textContent='';if(!/^[a-z0-9][a-z0-9-]*$/.test(v)){nameValid=false;name.classList.add('invalid');nameHint.textContent='Use only a-z, 0-9 and hyphen.';dirty();return;}const x=await fetch('/api/monitors/name-available/'+encodeURIComponent(v)+'?except='+encodeURIComponent(id)).then(r=>r.json());nameValid=x.available;name.classList.toggle('invalid',!nameValid);nameHint.textContent=nameValid?'':`Name '${v}' already exists.`;dirty();}
async function loadRules(){if(securityMode.value!=='Approval')return;const data=await fetch('/api/monitors/'+encodeURIComponent(id)+'/access-rules').then(r=>r.json());rules.innerHTML=data.map(x=>`<tr><td>${x.ipAddress??'—'}</td><td>${x.macAddress??'—'}</td><td>${x.computerName??'—'}</td><td>${x.userName??'—'}</td><td><select data-rule="${x.id}"><option ${x.permission==='Deny'?'selected':''}>Deny</option><option ${x.permission==='Deferred'?'selected':''}>Deferred</option><option ${x.permission==='Allow'?'selected':''}>Allow</option></select></td><td><button type="button" data-delete="${x.id}">Remove</button></td></tr>`).join('');document.querySelectorAll('[data-rule]').forEach(s=>s.onchange=async()=>{const old=data.find(x=>x.id==s.dataset.rule);await fetch('/api/monitors/'+encodeURIComponent(id)+'/access-rules',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({clientId:old.clientId,ipAddress:old.ipAddress,macAddress:old.macAddress,computerName:old.computerName,userName:old.userName,permission:s.value})});loadRules();});document.querySelectorAll('[data-delete]').forEach(b=>b.onclick=async()=>{await fetch('/api/monitors/'+encodeURIComponent(id)+'/access-rules/'+b.dataset.delete,{method:'DELETE'});loadRules();});}
async function load(){current=await fetch('/api/monitors/'+encodeURIComponent(id),{cache:'no-store'}).then(r=>r.json());name.value=current.configuration.name;title.value=current.configuration.title;const w=current.configuration.width,h=current.configuration.height;resolution.value=`${w}x${h}`;if(!resolution.value){const o=document.createElement('option');o.value=`${w}x${h}`;o.textContent=`${w} × ${h}`;resolution.append(o);resolution.value=o.value;}refreshRate.value=current.configuration.refreshRate;portrait.value=String(current.configuration.portrait);windowsDisplay.value=current.windowsDisplay??'—';gdi.value=current.deviceName??'—';position.value=current.positionX==null?'—':`X: ${current.positionX}, Y: ${current.positionY}`;remoteAccess.value=current.configuration.remoteAccess;securityMode.value=current.configuration.securityMode;apiKey.value=current.configuration.apiKey||'';allowClipboard.checked=current.configuration.collaborationClipboard;allowMouse.checked=current.configuration.collaborationMouse;allowKeyboard.checked=current.configuration.collaborationKeyboard;avatarPreview.innerHTML=current.configuration.avatarKind==='custom'?`<img class="avatar" src="/api/monitors/${encodeURIComponent(id)}/avatar?t=${Date.now()}">`:`<span class="avatarEmoji">${window.vmuAnimalEmoji(current.configuration.avatarValue)}</span>`;avatarAnimal.value='';avatarFile.value='';password.value='';regenKey=false;connect.disabled=!current.installed||current.connected;disconnect.disabled=!current.installed||!current.connected;uninstall.disabled=!current.installed;terminal.href='/monitor/'+encodeURIComponent(current.configuration.name);modeHelp();await loadRules();baseline=state();dirty();}
[name,title,resolution,refreshRate,portrait,remoteAccess,securityMode,allowClipboard,allowMouse,allowKeyboard,avatarAnimal,avatarFile,password].forEach(x=>x.addEventListener('input',dirty));name.oninput=()=>{name.value=name.value.trimStart().toLowerCase().replace(/[^a-z0-9-]/g,'');validateName();};remoteAccess.onchange=()=>{modeHelp();collaborationGuard();dirty();};securityMode.onchange=()=>{updateSecurity();loadRules();dirty();};[allowClipboard,allowMouse,allowKeyboard].forEach(x=>x.onchange=()=>{collaborationGuard();dirty();});regen.onclick=()=>{regenKey=true;apiKey.value='New unique key will be generated on Save';dirty();};
props.onsubmit=async e=>{e.preventDefault();await validateName();if(!nameValid)return;const [width,height]=resolution.value.split('x').map(Number);const data={name:name.value.trim(),title:title.value.trim(),width,height,refreshRate:+refreshRate.value,portrait:portrait.value==='true',remoteAccess:remoteAccess.value,securityMode:securityMode.value,password:password.value||null,regenerateApiKey:regenKey,collaborationClipboard:allowClipboard.checked,collaborationMouse:allowMouse.checked,collaborationKeyboard:allowKeyboard.checked};const r=await fetch('/api/monitors/'+encodeURIComponent(id),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});if(!r.ok){const x=await r.json();error.textContent=x.error||'Could not save monitor properties.';return;}let updated=await r.json();if(avatarAnimal.value)await fetch('/api/monitors/'+encodeURIComponent(updated.configuration.name)+'/avatar/animal/'+encodeURIComponent(avatarAnimal.value),{method:'POST'});if(avatarFile.files[0]){const fd=new FormData();fd.append('file',avatarFile.files[0]);await fetch('/api/monitors/'+encodeURIComponent(updated.configuration.name)+'/avatar/upload',{method:'POST',body:fd});}if(updated.configuration.name!==id){location.href='/monitors/'+encodeURIComponent(updated.configuration.name);return;}await load();};
async function action(name_,question){if(question&&!confirm(question))return false;const r=await fetch('/api/monitors/'+encodeURIComponent(id)+'/'+name_,{method:'POST'});if(!r.ok){const x=await r.json();alert(x.error||'Monitor operation failed.');return false;}return true;}connect.onclick=async()=>{if(await action('connect'))load();};disconnect.onclick=async()=>{if(await action('disconnect',`Disconnect '${current.configuration.title}'?`))load();};uninstall.onclick=async()=>{if(await action('uninstall',`Uninstall '${current.configuration.title}' from Windows?`))location.href='/monitors';};
addRule.onclick=async()=>{if(!ruleClient.value.trim())return;await fetch('/api/monitors/'+encodeURIComponent(id)+'/access-rules',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({clientId:ruleClient.value,ipAddress:ruleIp.value||null,macAddress:ruleMac.value||null,computerName:ruleComputer.value||null,userName:ruleUser.value||null,permission:rulePermission.value})});ruleClient.value=ruleIp.value=ruleMac.value=ruleComputer.value=ruleUser.value='';loadRules();};load().catch(e=>error.textContent=e.message);
</script>
""";
        return HtmlShell("Monitor " + monitor.Configuration.Title, body);
    }

    private IResult TerminalPage(string id)
    {
        var monitor = _monitorService.Get(id);
        if (monitor is null) return Results.NotFound();
        if (!id.Equals(monitor.Configuration.Name, StringComparison.OrdinalIgnoreCase)) return Results.Redirect("/monitor/" + Uri.EscapeDataString(monitor.Configuration.Name), true);
        var body = $"<div class=\"page\"><h1>{System.Net.WebUtility.HtmlEncode(monitor.Configuration.Title)}</h1><div class=\"terminalPlaceholder\"><img src=\"/api/monitors/{Uri.EscapeDataString(monitor.Configuration.Name)}/thumbnail?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\"><p>Live Terminal streaming is the next capture phase.</p></div></div>";
        return HtmlShell("Terminal " + monitor.Configuration.Title, body);
    }

    private async Task<IResult> CreateMonitorAsync(HttpRequest request)
    {
        try
        {
            var input = await request.ReadFromJsonAsync<MonitorCreateRequest>();
            if (input is null) return Results.BadRequest(new { error = "Invalid monitor creation payload." });
            var monitor = _monitorService.Create(input.Name, input.Title, input.Width, input.Height, input.RefreshRate, input.Portrait, input.AvatarAnimal);
            return Results.Created($"/api/monitors/{monitor.Configuration.Name}", monitor);
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private IResult GetMonitorResult(string id) => _monitorService.Get(id) is { } monitor ? Results.Json(monitor) : Results.NotFound();

    private async Task<IResult> UpdateMonitorAsync(HttpRequest request, string id)
    {
        try
        {
            var input = await request.ReadFromJsonAsync<MonitorUpdateRequest>();
            if (input is null) return Results.BadRequest(new { error = "Invalid monitor properties payload." });
            if (!Enum.TryParse<RemoteAccessMode>(input.RemoteAccess, true, out var remote)) return Results.BadRequest(new { error = "Invalid remote access mode." });
            if (!Enum.TryParse<RemoteSecurityMode>(input.SecurityMode, true, out var security)) return Results.BadRequest(new { error = "Invalid security mode." });
            return Results.Json(_monitorService.UpdateProperties(id, input.Name, input.Title, input.Width, input.Height, input.RefreshRate, input.Portrait, remote, security, input.Password, input.RegenerateApiKey, input.CollaborationClipboard, input.CollaborationMouse, input.CollaborationKeyboard));
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private async Task<IResult> GetThumbnailAsync(string id, HttpContext context)
    {
        try
        {
            var monitor = _monitorService.Get(id);
            if (monitor is null) return Results.NotFound();
            if (!monitor.Connected || string.IsNullOrWhiteSpace(monitor.DeviceName)) return Results.NotFound();
            var bytes = await _thumbnails.GetThumbnailAsync(monitor.Configuration.VmuId, monitor.DeviceName, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, "image/jpeg");
        }
        catch (Exception ex) { LogStore.Write("WARN", "WEB", "THUMBNAIL_FAILED", ex.Message, id); return Results.NotFound(); }
    }

    private IResult GetAvatar(string id)
    {
        var avatar = _monitorService.GetAvatar(id);
        return avatar is null ? Results.NotFound() : Results.File(avatar.Value.Bytes, avatar.Value.ContentType);
    }

    private async Task<IResult> UploadAvatarAsync(HttpRequest request, string id)
    {
        try
        {
            var form = await request.ReadFormAsync(); var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Avatar file is required." });
            if (file.Length > 2 * 1024 * 1024) return Results.BadRequest(new { error = "Avatar file must be at most 2 MB." });
            await using var stream = file.OpenReadStream();
            return Results.Json(_monitorService.SetCustomAvatar(id, file.FileName, stream));
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private async Task<IResult> UpsertAccessRuleAsync(HttpRequest request, string id)
    {
        try
        {
            var input = await request.ReadFromJsonAsync<AccessRuleRequest>();
            if (input is null || !Enum.TryParse<AccessPermission>(input.Permission, true, out var permission)) return Results.BadRequest(new { error = "Invalid access rule." });
            return Results.Json(_monitorService.UpsertAccessRule(id, input.ClientId, input.IpAddress, input.MacAddress, input.ComputerName, input.UserName, permission));
        }
        catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    private static IResult RunMonitorAction(Func<MonitorSnapshot> action) { try { return Results.Json(action()); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } }
    private IResult RunMonitorUninstall(string id) { try { _monitorService.Uninstall(id); _thumbnails.Invalidate(id); return Results.NoContent(); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } }

    private async Task<IResult> SaveSettingsAsync(HttpRequest request)
    {
        var proposed = await request.ReadFromJsonAsync<ServerSettings>();
        if (proposed is null) return Results.BadRequest(new { error = "Invalid settings payload." });
        if (proposed.Vmu.Interface.Equals("any", StringComparison.OrdinalIgnoreCase) && !proposed.Web.Interface.Equals("any", StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = "Web Server interface must be 'any' while VMU Server interface is 'any'." });
        var endpoints = new[] { ("VMU Server", proposed.Vmu.Port), ("Web Server", proposed.Web.Port), ("Web Socket", proposed.Socket.Port) };
        if (endpoints.Any(x => x.Port is < 1 or > 65535)) return Results.Conflict(new { error = "Service ports must be between 1 and 65535." });
        var duplicate = endpoints.GroupBy(x => x.Port).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) return Results.Conflict(new { error = $"Port {duplicate.Key} is configured for more than one VMU service." });
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(x => x.Port).ToHashSet();
        var blocked = endpoints.FirstOrDefault(x => active.Contains(x.Port) && !_isOwnedListener(x.Port));
        if (blocked != default) return Results.Conflict(new { error = $"{blocked.Item1} port {blocked.Port} is already used by another TCP listener on this computer." });
        return Results.Json(await _settingsSaver(proposed));
    }

    private IResult LogPage()
    {
        var body = """
<div class="logpage"><div class="filters"><h3>Filters</h3><label><input type="checkbox" data-service="VMU" checked> VMU <span class="dot" id="s-VMU"></span></label><label><input type="checkbox" data-service="VMU_SERVER" checked> VMU Server <span class="dot" id="s-VMU_SERVER"></span></label><label><input type="checkbox" data-service="WEB" checked> Web Server <span class="dot" id="s-WEB"></span></label><label><input type="checkbox" data-service="SOCKET" checked> Socket Server <span class="dot" id="s-SOCKET"></span></label></div><div class="logmain"><div class="toolbar"><div class="searchbox"><input id="search" placeholder="Search..."><button id="clearSearch">×</button></div></div><div class="tablewrap"><table id="log"><thead><tr><th data-col="timestamp">Timecode <span class="sort"></span></th><th data-col="level">Level <span class="sort"></span></th><th data-col="service">Service <span class="sort"></span></th><th data-col="monitorId">Monitor <span class="sort"></span></th><th data-col="event">Event <span class="sort"></span></th><th data-col="message">Message <span class="sort"></span></th></tr></thead><tbody></tbody></table></div><div class="logfooter"><label><input id="tail" type="checkbox" checked> Always at end</label><div><button id="exportXlsx">Export XLSX</button><button id="exportCsv">Export CSV</button><button id="exportTxt">Export TXT</button><button id="clear">Clear</button></div></div></div></div><dialog id="detail"><pre id="detailText"></pre><button onclick="detail.close()">Close</button></dialog>
<script>let rows=[],sortCol=null,sortDir=1,lastSelected=null;const tbody=document.querySelector('#log tbody');function selected(){return [...document.querySelectorAll('[data-service]:checked')].map(x=>x.dataset.service)}function query(){const p=new URLSearchParams();selected().forEach(x=>p.append('service',x));if(search.value)p.set('q',search.value);return p}async function refresh(){const [data,status]=await Promise.all([fetch('/api/log?'+query()).then(r=>r.json()),fetch('/api/status',{cache:'no-store'}).then(r=>r.json())]);rows=data;status.services.forEach(x=>{const d=document.getElementById('s-'+x.key);if(d)d.className='dot '+(x.running?'on':'off')});render()}function value(x,k){if(k==='timestamp')return new Date(x[k]).getTime();return String(x[k]??'').toLocaleLowerCase()}function esc(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML}function render(){let data=[...rows];if(!tail.checked&&sortCol)data.sort((a,b)=>(value(a,sortCol)<value(b,sortCol)?-1:value(a,sortCol)>value(b,sortCol)?1:0)*sortDir);document.querySelectorAll('#log th .sort').forEach(x=>x.textContent='');if(!tail.checked&&sortCol)document.querySelector(`#log th[data-col="${sortCol}"] .sort`).textContent=sortDir===1?'▲':'▼';tbody.innerHTML=data.map(x=>`<tr data-id="${x.id}"><td>${new Date(x.timestamp).toLocaleString()}</td><td>${x.level}</td><td>${x.service}</td><td>${x.monitorId??''}</td><td>${x.event}</td><td>${esc(x.message)}</td></tr>`).join('');const target=tail.checked?tbody.lastElementChild:(lastSelected&&tbody.querySelector(`[data-id="${lastSelected}"]`));if(target){target.classList.add('selected');if(tail.checked)target.scrollIntoView({block:'nearest'});lastSelected=target.dataset.id;}}tbody.onclick=e=>{const tr=e.target.closest('tr');if(!tr)return;lastSelected=tr.dataset.id;tbody.querySelectorAll('tr').forEach(x=>x.classList.remove('selected'));tr.classList.add('selected')};tbody.ondblclick=async e=>{const tr=e.target.closest('tr');if(!tr)return;detailText.textContent=JSON.stringify(await fetch('/api/log/'+tr.dataset.id).then(r=>r.json()),null,2);detail.showModal()};document.querySelectorAll('[data-service]').forEach(x=>x.onchange=refresh);search.oninput=refresh;clearSearch.onclick=()=>{search.value='';refresh()};tail.onchange=()=>{if(!tail.checked){sortCol='timestamp';sortDir=1}else sortCol=null;render()};document.querySelectorAll('th[data-col]').forEach(th=>th.onclick=()=>{if(tail.checked)return;if(sortCol===th.dataset.col)sortDir*=-1;else{sortCol=th.dataset.col;sortDir=1}render()});clear.onclick=async()=>{if(confirm('Clear the log?')){await fetch('/api/log',{method:'DELETE'});refresh()}};function exp(f){location.href='/api/log/export/'+f+'?'+query()}exportXlsx.onclick=()=>exp('xlsx');exportCsv.onclick=()=>exp('csv');exportTxt.onclick=()=>exp('txt');refresh();setInterval(refresh,1000);</script>
""";
        return HtmlShell("View Log", body, "logbody");
    }

    private IReadOnlyList<LogEntry> ReadLog(HttpRequest request)
    {
        var services = request.Query["service"].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
        return LogStore.Read(request.Query["q"].FirstOrDefault(), services.Length == 0 ? ServiceKeys : services);
    }

    private IResult ExportLog(HttpRequest request, string format)
    {
        if (format is not ("xlsx" or "csv" or "txt")) return Results.BadRequest();
        var entries = ReadLog(request); var bytes = LogExportService.ExportBytes(format, entries);
        var contentType = format == "xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : format == "csv" ? "text/csv" : "text/plain";
        return Results.File(bytes, contentType, $"vmu-log-{DateTime.Now:yyyyMMdd-HHmmss}.{format}");
    }

    private string MonitorNavigation()
    {
        try
        {
            return string.Join("", _monitorService.List().Select(m =>
            {
                var avatar = m.Configuration.AvatarKind == "custom" ? $"<img src=\"/api/monitors/{Uri.EscapeDataString(m.Configuration.Name)}/avatar\">" : $"<span>{System.Net.WebUtility.HtmlEncode(MonitorAvatarService.GetEmoji(m.Configuration.AvatarKind, m.Configuration.AvatarValue))}</span>";
                return $"<a class=\"monitorNav\" href=\"/monitor/{Uri.EscapeDataString(m.Configuration.Name)}\">{avatar}<b>{System.Net.WebUtility.HtmlEncode(m.Configuration.Title)}</b></a>";
            }));
        }
        catch { return string.Empty; }
    }

    private IResult HtmlShell(string title, string body, string bodyClass = "")
    {
        var monitors = MonitorNavigation();
        var commonScript = """
<script>window.vmuAnimalEmoji=n=>({fox:'🦊',owl:'🦉',panda:'🐼',cat:'🐱',dog:'🐶',rabbit:'🐰',bear:'🐻',koala:'🐨',tiger:'🐯',lion:'🦁',penguin:'🐧',frog:'🐸'}[n]||'🖥️');const navButton=document.getElementById('navButton'),navMenu=document.getElementById('navMenu');navButton.onclick=e=>{e.stopPropagation();navMenu.classList.toggle('open')};document.addEventListener('click',()=>navMenu.classList.remove('open'));navMenu.onclick=e=>e.stopPropagation();let vmuHealthFailures=0,vmuRecoveryRunning=false;async function vmuHealth(){try{const r=await fetch('/api/health',{cache:'no-store'});if(!r.ok)throw new Error();vmuHealthFailures=0}catch{vmuHealthFailures++;if(vmuHealthFailures>=2&&!vmuRecoveryRunning)window.vmuWaitForEndpoint(location.origin+'/',10000,null)}}setInterval(vmuHealth,1000);window.vmuWaitForEndpoint=async function(target,waitMs,path){if(vmuRecoveryRunning)return;vmuRecoveryRunning=true;const overlay=connectionOverlay,bar=connectionBar;overlay.classList.add('show');connectionHeadline.textContent='Restarting Web Server...';connectionMessage.textContent='Waiting for VMU to become available.';connectionRetry.classList.add('hidden');const started=Date.now(),timer=setInterval(()=>bar.style.width=Math.min(99,Math.round((Date.now()-started)/waitMs*100))+'%',100);while(Date.now()-started<waitMs){try{const r=await fetch(target+'api/health',{cache:'no-store'});if(r.ok){clearInterval(timer);bar.style.width='100%';setTimeout(()=>location.href=target+(path||location.pathname.replace(/^\//,'')),250);return}}catch{}await new Promise(r=>setTimeout(r,350))}clearInterval(timer);connectionHeadline.textContent='Connection to VMU was lost';connectionRetry.classList.remove('hidden');connectionRetry.onclick=()=>{vmuRecoveryRunning=false;bar.style.width='0';window.vmuWaitForEndpoint(target,waitMs,path)}};</script>
""";
        var css = """
*{box-sizing:border-box}html,body{margin:0;font-family:Segoe UI,Arial,sans-serif;color:#202124;background:#f5f6f8}body.logbody{height:100vh;overflow:hidden}nav{height:60px;background:#202124;display:flex;align-items:stretch;padding:0 10px}.navwrap{position:relative;display:flex;align-items:center;border-right:1px solid #555;padding-right:10px;margin-right:0}.gear{width:40px;height:40px;border:0;background:transparent;color:white;font-size:20px;border-radius:5px;cursor:pointer}.gear:hover{background:#34373b}.navmenu{display:none;position:absolute;top:52px;left:0;min-width:150px;background:white;border:1px solid #bbb;border-radius:6px;box-shadow:0 5px 18px #0004;padding:5px;z-index:1000}.navmenu.open{display:block}.navmenu a{display:block;color:#202124;text-decoration:none;padding:8px 10px;border-radius:4px}.navmenu a:hover{background:#eef1f5}.monitorNav{height:60px;display:flex;align-items:center;gap:7px;padding:0 14px;border-right:1px solid #555;color:#fff;text-decoration:none;white-space:nowrap}.monitorNav:hover{background:#34373b}.monitorNav img{width:24px;height:24px;object-fit:contain}.monitorNav span{font-size:21px}.page{max-width:1000px;margin:30px auto;padding:0 20px}.muted,.hint,.fieldhint{color:#687078}.fieldhint{font-size:12px;margin:-5px 0 6px 172px}.cards,.stats,.projecttiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}.service,.stats>div,.stats>a,.projecttiles>a{background:white;border:1px solid #d9dde3;border-radius:7px;padding:14px;display:flex;gap:10px;align-items:center;text-align:center;justify-content:center;color:#202124;text-decoration:none}.stats>a,.stats>div{flex-direction:column}.stats strong{font-size:24px}.projecttiles>a{min-height:88px;flex-direction:column}.projecttiles b{font-size:24px}.dot{display:inline-block;width:11px;height:11px;border-radius:50%;background:#aaa}.dot.on{background:#25a746}.settingsform{width:470px;max-width:100%}.settings{border-collapse:collapse;width:100%}.settings th,.settings td{padding:7px 10px;text-align:left}.settings input,.settings select,.properties input,.properties select{padding:6px}.formgrid{display:grid;grid-template-columns:150px 220px;gap:10px;align-items:center}.actions{display:flex;gap:8px;margin-top:18px;align-items:center;flex-wrap:wrap}.actions button,.logfooter button,dialog button,.buttonlink,.keyrow button,.ruleadd button{padding:7px 14px}.buttonlink{border:1px solid #aaa;border-radius:3px;text-decoration:none;color:#202124;background:#f4f4f4}.danger{color:#9b1c1c}.error{color:#b3261e;margin:10px 0}.hidden{display:none!important}.invalid{border:2px solid #b3261e!important;background:#fff5f5}.monitorgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:16px}.monitorcard{background:white;border:1px solid #d9dde3;border-radius:8px;padding:18px;text-align:center;color:#202124;text-decoration:none;min-height:230px}.monitorcard:hover{box-shadow:0 3px 10px #0002}.monitorpic{height:115px;position:relative}.preview,.screen{width:125px;height:78px;border:7px solid #4b5563;border-radius:5px;margin:auto;object-fit:cover;background:#dceefa}.stand{width:45px;height:8px;background:#4b5563;margin:8px auto}.plus{font-size:92px;line-height:120px}.avatar{width:28px;height:28px;object-fit:contain;vertical-align:middle}.avatarEmoji{font-size:25px;vertical-align:middle}.avatarcontrols{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.properties{max-width:760px}.properties>label{display:grid;grid-template-columns:160px 1fr;gap:12px;align-items:center;margin:10px 0}.properties fieldset>label{display:grid;grid-template-columns:140px 1fr;gap:10px;align-items:center;margin:10px 0}.inlinechecks{display:flex;gap:18px;margin:8px 0 12px 150px}.inlinechecks label{display:flex;gap:5px}.keyrow{display:flex;gap:8px}.keyrow input{flex:1}.accessrules{border-collapse:collapse;width:100%;font-size:13px;margin-top:12px}.accessrules th,.accessrules td{border:1px solid #ddd;padding:5px}.ruleadd{display:grid;grid-template-columns:repeat(3,1fr);gap:5px;margin-top:8px}.operation,.terminalPlaceholder{background:white;border:1px solid #ccd1d8;border-radius:8px;padding:20px;margin-top:18px}.terminalPlaceholder img{max-width:100%;border:1px solid #555}.logpage{display:grid;grid-template-columns:190px minmax(0,1fr);height:calc(100vh - 60px);padding:10px;gap:8px;overflow:hidden}.filters{background:#eee;border:1px solid #ccc;padding:8px;overflow:auto}.filters label{display:flex;align-items:center;gap:5px;padding:4px}.filters .dot{margin-left:auto}.logmain{display:grid;grid-template-rows:auto minmax(0,1fr) auto;min-width:0;min-height:0}.toolbar{display:flex;justify-content:flex-end;margin-bottom:6px}.searchbox{display:flex}.searchbox input{width:260px;padding:6px}.searchbox button{width:30px}.tablewrap{background:white;border:1px solid #bbb;overflow:auto;min-height:0}#log{border-collapse:collapse;width:100%;font:13px Consolas,monospace}#log th{position:sticky;top:0;background:#e1e6ec;border-bottom:1px solid #aaa;padding:6px;text-align:left;cursor:pointer;z-index:2}#log td{padding:5px;border-bottom:1px solid #eee;white-space:nowrap}#log tr.selected{background:#cfe4ff}.logfooter{display:flex;justify-content:space-between;align-items:center;padding-top:8px}.logfooter>div{display:flex;gap:6px}fieldset{border:1px solid #b8bec7;border-radius:4px;padding:12px;margin-top:14px}dialog{max-width:800px;width:70%;border:1px solid #888;border-radius:6px}.connection{display:none;position:fixed;inset:0;background:#f5f6f8f5;z-index:5000;align-items:center;justify-content:center}.connection.show{display:flex}.connectionbox{width:min(600px,90vw);text-align:center;background:white;border:1px solid #ccd1d8;border-radius:9px;padding:28px}.progress{height:16px;background:#ddd;border-radius:9px;overflow:hidden;margin:18px 0}.progress div{height:100%;width:0;background:#2d7dd2;transition:width .1s}@media(max-width:700px){.monitorNav b{display:none}.properties>label,.properties fieldset>label,.formgrid{grid-template-columns:1fr}.fieldhint,.inlinechecks{margin-left:0}.ruleadd{grid-template-columns:1fr}}
""";
        var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + System.Net.WebUtility.HtmlEncode(title) + " - VMU</title><style>" + css + "</style></head><body class=\"" + bodyClass + "\"><nav><div class=\"navwrap\"><button id=\"navButton\" class=\"gear\" title=\"Navigation\">⚙</button><div id=\"navMenu\" class=\"navmenu\"><a href=\"/\">Status</a><a href=\"/monitors\">Monitors</a><a href=\"/settings\">Settings</a><a href=\"/log\">View Log</a></div></div>" + monitors + "</nav>" + body + "<div id=\"connectionOverlay\" class=\"connection\"><div class=\"connectionbox\"><h1 id=\"connectionHeadline\">Restarting Web Server...</h1><div class=\"progress\"><div id=\"connectionBar\"></div></div><p id=\"connectionMessage\"></p><button id=\"connectionRetry\" class=\"hidden\">Retry</button></div></div>" + commonScript + "</body></html>";
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
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; await context.Response.WriteAsync("WebSocket endpoint"); return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync(); var buffer = new byte[4096];
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) { await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted); break; }
            }
        });
    }
}
