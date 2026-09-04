using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
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
internal sealed record TerminalStartupSampleRequest(int DurationMs, bool Success, int ExpectedMs, string? Reason);

internal abstract class NetworkService : IAsyncDisposable
{
    private WebApplication? _application;
    protected NetworkService(string name, string serviceKey, LogStore logStore){Name=name;ServiceKey=serviceKey;LogStore=logStore;}
    public string Name { get; }
    public string ServiceKey { get; }
    protected LogStore LogStore { get; }
    public bool IsRunning => _application is not null;
    public int? ActivePort { get; private set; }
    public async Task StartAsync(ServiceEndpointSettings endpoint){if(IsRunning)return;try{_application=await BuildAndStartAsync(endpoint);ActivePort=endpoint.Port;LogStore.Write("INFO",ServiceKey,"SERVICE_START",$"{Name} started on {endpoint.Interface}:{endpoint.Port}");}catch(Exception ex){LogStore.Write("ERROR",ServiceKey,"SERVICE_START_FAILED",$"{Name} failed to start: {ex.Message}",detailsJson:JsonSerializer.Serialize(new{exception=ex.ToString()}));throw;}}
    public async Task StopAsync(){if(_application is null)return;var application=_application;_application=null;ActivePort=null;await application.StopAsync(TimeSpan.FromSeconds(5));await application.DisposeAsync();LogStore.Write("INFO",ServiceKey,"SERVICE_STOP",$"{Name} stopped");}
    public async Task RestartAsync(ServiceEndpointSettings endpoint){if(_application is not null)await StopAsync();await StartAsync(endpoint);}
    private async Task<WebApplication> BuildAndStartAsync(ServiceEndpointSettings endpoint){var builder=WebApplication.CreateSlimBuilder();builder.WebHost.UseUrls(endpoint.Interface.Equals("any",StringComparison.OrdinalIgnoreCase)?$"http://0.0.0.0:{endpoint.Port}":$"http://127.0.0.1:{endpoint.Port}");ConfigureServices(builder.Services);var application=builder.Build();ConfigureApplication(application);await application.StartAsync();return application;}
    protected virtual void ConfigureServices(IServiceCollection services){}
    protected abstract void ConfigureApplication(WebApplication app);
    public async ValueTask DisposeAsync(){if(_application is null)return;var application=_application;_application=null;ActivePort=null;await application.StopAsync(TimeSpan.FromSeconds(2));await application.DisposeAsync();}
}

internal sealed class WebServerService : NetworkService
{
    private static readonly string[] ServiceKeys=["VMU","VMU_SERVER","WEB","SOCKET"];
    private readonly MonitorApplicationService _monitors;
    private readonly MonitorThumbnailService _capture=new();
    private readonly SystemResourceService _resources=new();
    private readonly DisplayArrangementCoordinator _arrangements;
    private readonly TerminalStartupStatsStore _terminalStartupStats;
    private readonly WindowsCursorTransitionService _cursorTransitions;
    private readonly ConcurrentDictionary<string,ConcurrentDictionary<Guid,LiveTerminalClient>> _terminalClients=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,byte> _terminalRefreshPending=new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyDictionary<string,bool>> _statusProvider;
    private readonly Func<ServerSettings> _settingsProvider;
    private readonly Func<ServerSettings,Task<WebSettingsSaveResult>> _settingsSaver;
    private readonly Func<int,bool> _isOwnedListener;

    public WebServerService(LogStore logs,MonitorApplicationService monitors,Func<IReadOnlyDictionary<string,bool>> status,Func<ServerSettings> settings,Func<ServerSettings,Task<WebSettingsSaveResult>> saver,Func<int,bool> owned):base("Web Server","WEB",logs)
    {
        _monitors=monitors;
        _statusProvider=status;
        _settingsProvider=settings;
        _settingsSaver=saver;
        _isOwnedListener=owned;
        _arrangements=new DisplayArrangementCoordinator(logs);
        _terminalStartupStats=new TerminalStartupStatsStore(logs.DatabasePath);
        _cursorTransitions=new WindowsCursorTransitionService(()=>_monitors.List(),logs);
        _cursorTransitions.Transition+=OnCursorTransition;
        TerminalMousePortalService.ConfigureLogging(logs);
    }

    protected override void ConfigureApplication(WebApplication app){app.MapGet("/",StatusPage);app.MapGet("/settings",SettingsPage);app.MapGet("/settings/arrangement",()=>Results.Redirect("/arrangement"));app.MapGet("/arrangement",ArrangementPage);app.MapGet("/monitors",MonitorsPage);app.MapGet("/monitors/new",NewMonitorPage);app.MapGet("/monitors/{id}",MonitorPropertiesPage);app.MapGet("/monitor/{id}",TerminalPage);app.MapGet("/log",LogPage);app.MapGet("/api/health",()=>Results.Json(new{status="ok",version=ProjectInfo.Version}));app.MapGet("/api/status",()=>Results.Json(StatusModel()));app.MapGet("/api/resources",()=>Results.Json(_resources.Read()));app.MapGet("/api/arrangement",()=>Results.Json(ArrangementModel()));app.MapPost("/api/arrangement/apply",ApplyArrangementAsync);app.MapPost("/api/arrangement/keep",()=>Results.Json(new{kept=_arrangements.Keep()}));app.MapPost("/api/arrangement/revert",()=>Results.Json(new{reverted=_arrangements.Revert()}));app.MapPost("/api/arrangement/open-windows-settings",OpenWindowsDisplaySettings);app.MapGet("/api/settings",()=>Results.Json(_settingsProvider()));app.MapPost("/api/settings",SaveSettingsAsync);app.MapGet("/api/log",(HttpRequest request)=>Results.Json(ReadLog(request)));app.MapGet("/api/log/count",(HttpRequest request)=>Results.Json(ReadLogCount(request)));app.MapDelete("/api/log",()=>{LogStore.Clear();return Results.NoContent();});app.MapGet("/api/log/export/{format}",ExportLog);app.MapGet("/api/avatars",()=>Results.Json(new{revision=MonitorAvatarService.Revision,ids=MonitorAvatarService.AnimalNames}));app.MapGet("/api/avatars/{id}",BuiltInAvatar);app.MapGet("/api/monitors",()=>Results.Json(_monitors.List()));app.MapPost("/api/monitors/order",ReorderAsync);app.MapGet("/api/monitors/name-available/{name}",(string name,string? except)=>Results.Json(new{available=_monitors.NameAvailable(name,except)}));app.MapPost("/api/monitors",CreateAsync);app.MapGet("/api/monitors/{id}",(string id)=>_monitors.Get(id) is{} monitor?Results.Json(monitor):Results.NotFound());app.MapPut("/api/monitors/{id}",UpdateAsync);app.MapPost("/api/monitors/{id}/connect",(string id)=>Action(()=>_monitors.Connect(id)));app.MapPost("/api/monitors/{id}/disconnect",(string id)=>Action(()=>_monitors.Disconnect(id)));app.MapPost("/api/monitors/{id}/uninstall",Uninstall);app.MapGet("/api/monitors/{id}/thumbnail",ThumbnailAsync);app.MapGet("/api/monitors/{id}/live",LiveAsync);app.MapGet("/api/monitors/{id}/terminal-startup-stats",TerminalStartupStats);app.MapPost("/api/monitors/{id}/terminal-startup-sample",TerminalStartupSampleAsync);app.MapGet("/api/monitors/{id}/avatar",Avatar);app.MapPost("/api/monitors/{id}/avatar/animal/{animal}",(string id,string animal)=>Action(()=>_monitors.SetAnimalAvatar(id,animal)));app.MapPost("/api/monitors/{id}/avatar/upload",UploadAvatarAsync);app.MapGet("/api/monitors/{id}/access-rules",(string id)=>Results.Json(_monitors.ListAccessRules(id)));app.MapPost("/api/monitors/{id}/access-rules",RuleAsync);app.MapDelete("/api/monitors/{id}/access-rules/{ruleId:long}",(string id,long ruleId)=>{_monitors.DeleteAccessRule(id,ruleId);return Results.NoContent();});}

    private object StatusModel(){var status=_statusProvider();var monitors=_monitors.List();return new{application=ProjectInfo.ProductName,version=ProjectInfo.Version,services=new[]{new{key="VMU",name="VMU",running=status.GetValueOrDefault("VMU")},new{key="VMU_SERVER",name="VMU Server",running=status.GetValueOrDefault("VMU_SERVER")},new{key="WEB",name="Web Server",running=status.GetValueOrDefault("WEB")},new{key="SOCKET",name="Socket Server",running=status.GetValueOrDefault("SOCKET")}},monitors=new{installed=monitors.Count(x=>x.Installed),connected=monitors.Count(x=>x.Connected)},remote=new{enabled=monitors.Any(x=>x.Configuration.RemoteAccess!=RemoteAccessMode.Disabled),clients=0},links=new{github=ProjectInfo.RepositoryUrl,documentation=ProjectInfo.DocumentationUrl,guide=ProjectInfo.GuideUrl,bugs=ProjectInfo.RepositoryUrl.TrimEnd('/')+"/issues"}};}
    private IResult StatusPage()=>Shell("Status",WebUiRenderer.StatusBody());private IResult SettingsPage()=>Shell("Settings",WebUiRenderer.SettingsBody()+WebPageEnhancements.Settings+SettingsLayoutEnhancement.Content);private IResult ArrangementPage()=>Shell("Arrangement",WebUiRenderer.ArrangementBody()+ArrangementWebEnhancement.Script,"arrangementbody");private IResult MonitorsPage()=>Shell("Monitors",WebUiRenderer.MonitorsBody(_settingsProvider().WebUi.MonitorPreviewRefreshSeconds)+WebPageEnhancements.Monitors);private IResult LogPage()=>Shell("Log",WebUiRenderer.LogBody()+WebPageEnhancements.Log,"logbody");
    private IResult NewMonitorPage(){var animal=MonitorAvatarService.RandomAnimal();var rates=BuildRefreshRateOptions(60);return Shell("Add Monitor",WebUiRenderer.NewMonitorBody(WebUiRenderer.AvatarPicker("animal",animal),rates));}
    private IResult MonitorPropertiesPage(string id){var monitor=_monitors.Get(id);if(monitor is null)return Results.NotFound();if(!id.Equals(monitor.Configuration.Name,StringComparison.OrdinalIgnoreCase))return Results.Redirect("/monitors/"+Uri.EscapeDataString(monitor.Configuration.Name),permanent:true);var rates=BuildRefreshRateOptions(monitor.Configuration.RefreshRate);var picker=WebUiRenderer.AvatarPicker(monitor.Configuration.AvatarKind,monitor.Configuration.AvatarValue,monitor.Configuration.Name);return Shell("Monitor "+monitor.Configuration.Title,WebUiRenderer.MonitorPropertiesBody(monitor.Configuration.Name,picker,rates)+MonitorPropertiesWebEnhancement.Content);}
    private IResult TerminalPage(string id){var monitor=_monitors.Get(id);if(monitor is null)return Results.NotFound();if(!id.Equals(monitor.Configuration.Name,StringComparison.OrdinalIgnoreCase))return Results.Redirect("/monitor/"+Uri.EscapeDataString(monitor.Configuration.Name),permanent:true);var vmuRunning=IsVmuServerRunning();var ready=monitor.Connected&&!monitor.Health.IsError;return Shell("Terminal "+monitor.Configuration.Title,WebUiRenderer.TerminalBody(monitor.Configuration.Name,ready,vmuRunning)+WebPageEnhancements.Terminal+TerminalStartupProgressEnhancement.Content,"terminalbody");}
    private object ArrangementModel(){var virtualMonitors=_monitors.List().Where(x=>x.DeviceName is not null).ToDictionary(x=>x.DeviceName!,StringComparer.OrdinalIgnoreCase);return WindowsArrangementService.GetActive().Select(display=>{virtualMonitors.TryGetValue(display.DeviceName,out var monitor);return new{display.WindowsNumber,display.DeviceName,display.X,display.Y,display.Width,display.Height,display.Primary,title=monitor?.Configuration.Title,monitorName=monitor?.Configuration.Name,isVirtual=monitor is not null};});}
    private async Task<IResult> ApplyArrangementAsync(HttpRequest request){try{var payload=await request.ReadFromJsonAsync<ArrangementApplyRequest>();if(payload?.Displays is null||payload.Displays.Length==0)return Results.BadRequest(new{error="Display arrangement is empty."});var active=WindowsArrangementService.GetActive();var requestedNames=payload.Displays.Select(x=>x.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);if(active.Count!=payload.Displays.Length||active.Any(x=>!requestedNames.Contains(x.DeviceName)))return Results.Conflict(new{error="The active Windows display set changed. Reset the arrangement and try again."});var positions=payload.Displays.Select(x=>new DisplayArrangementEntry(x.DeviceName,x.X,x.Y)).ToArray();return Results.Json(_arrangements.Apply(positions));}catch(Exception ex){LogStore.Write("ERROR","VMU","ARRANGEMENT_APPLY_FAILED",$"Display arrangement failed: {ex.Message}",detailsJson:JsonSerializer.Serialize(new{exception=ex.ToString()}));return Results.BadRequest(new{error=ex.Message});}}
    private IResult OpenWindowsDisplaySettings(HttpContext context){var remote=context.Connection.RemoteIpAddress;if(remote is null||!IPAddress.IsLoopback(remote))return Results.StatusCode(StatusCodes.Status403Forbidden);try{Process.Start(new ProcessStartInfo("ms-settings:display"){UseShellExecute=true});return Results.NoContent();}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}}

    private async Task LiveAsync(string id,HttpContext context)
    {
        var monitor=_monitors.Get(id);
        if(!IsVmuServerRunning()){context.Response.StatusCode=StatusCodes.Status503ServiceUnavailable;return;}
        if(monitor is null||!monitor.Connected||monitor.Health.IsError||monitor.DeviceName is null){context.Response.StatusCode=StatusCodes.Status404NotFound;return;}

        context.Response.ContentType="multipart/x-mixed-replace; boundary=vmu";
        var clientId=Guid.NewGuid();
        var client=new LiveTerminalClient(context);
        var clients=_terminalClients.GetOrAdd(monitor.Configuration.VmuId,_=>new ConcurrentDictionary<Guid,LiveTerminalClient>());
        clients[clientId]=client;
        using var cursorLease=_cursorTransitions.Acquire();

        try
        {
            while(!context.RequestAborted.IsCancellationRequested&&IsVmuServerRunning())
            {
                var frame=await _capture.GetLiveFrameAsync(monitor.Configuration.VmuId,monitor.DeviceName,context.RequestAborted);
                if(frame is null){await Task.Delay(40,context.RequestAborted);continue;}
                await WriteMjpegFrameAsync(client,frame,context.RequestAborted);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){LogStore.Write("WARN","WEB","TERMINAL_STREAM_FAILED",ex.Message,monitor.Configuration.VmuId);}
        finally
        {
            clients.TryRemove(clientId,out _);
            if(clients.IsEmpty)_terminalClients.TryRemove(monitor.Configuration.VmuId,out _);
        }
    }

    private async Task WriteMjpegFrameAsync(LiveTerminalClient client,byte[] frame,CancellationToken cancellationToken)
    {
        await client.WriteGate.WaitAsync(cancellationToken);
        try
        {
            var header=Encoding.ASCII.GetBytes($"--vmu\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
            await client.Context.Response.Body.WriteAsync(header,cancellationToken);
            await client.Context.Response.Body.WriteAsync(frame,cancellationToken);
            await client.Context.Response.Body.WriteAsync("\r\n"u8.ToArray(),cancellationToken);
            await client.Context.Response.Body.FlushAsync(cancellationToken);
        }
        finally{client.WriteGate.Release();}
    }

    private void OnCursorTransition(CursorDisplayTransition transition)
    {
        var previous=transition.PreviousDisplay;
        if(previous is null||!previous.IsVirtual||string.IsNullOrWhiteSpace(previous.VmuId))return;
        if(!_terminalClients.ContainsKey(previous.VmuId))return;
        if(!_terminalRefreshPending.TryAdd(previous.VmuId,0))return;
        _=Task.Run(()=>BroadcastTerminalRefreshAsync(previous.VmuId,previous.DeviceName,previous.CName,transition));
    }

    private async Task BroadcastTerminalRefreshAsync(string vmuId,string deviceName,string? cname,CursorDisplayTransition transition)
    {
        try
        {
            if(!_terminalClients.TryGetValue(vmuId,out var clients)||clients.IsEmpty)return;
            var display=WindowsArrangementService.GetActive().FirstOrDefault(x=>x.DeviceName.Equals(deviceName,StringComparison.OrdinalIgnoreCase));
            if(display is null)return;
            var frame=TerminalFrameRefreshService.Capture(display.X,display.Y,display.Width,display.Height);
            foreach(var client in clients.Values.ToArray())
            {
                try{await WriteMjpegFrameAsync(client,frame,client.Context.RequestAborted);}catch(OperationCanceledException){}catch(IOException){}
            }
        }
        catch(Exception ex)
        {
            LogStore.Write("WARN","WEB","TERMINAL_CURSOR_REFRESH_FAILED",$"Corrective Terminal frame failed: {ex.Message}",vmuId,JsonSerializer.Serialize(new{deviceName,cname,previous=transition.PreviousPosition,current=transition.CurrentPosition}));
        }
        finally{_terminalRefreshPending.TryRemove(vmuId,out _);}
    }

    private object TerminalStartupStats(string id){var monitor=_monitors.Get(id);if(monitor is null)return new{expectedMs=5000,sampleCount=0};var estimate=_terminalStartupStats.GetEstimate(monitor.Configuration.VmuId);return new{expectedMs=estimate.ExpectedMs,sampleCount=estimate.SampleCount};}
    private async Task<IResult> TerminalStartupSampleAsync(string id,HttpRequest request){var monitor=_monitors.Get(id);if(monitor is null)return Results.NotFound();var sample=await request.ReadFromJsonAsync<TerminalStartupSampleRequest>();if(sample is null||sample.DurationMs<0)return Results.BadRequest(new{error="Invalid startup sample."});_terminalStartupStats.Record(monitor.Configuration.VmuId,sample.DurationMs,sample.Success,sample.ExpectedMs,sample.Reason);return Results.NoContent();}
    private IResult Uninstall(string id){var monitor=_monitors.Get(id);if(monitor is null)return Results.NotFound();var vmuId=monitor.Configuration.VmuId;var result=Action(()=>_monitors.Uninstall(id));_terminalStartupStats.DeleteMonitor(vmuId);return result;}
    private async Task<IResult> CreateAsync(HttpRequest request){try{var payload=await request.ReadFromJsonAsync<MonitorCreateRequest>();if(payload is null)return Results.BadRequest(new{error="Invalid request."});var result=_monitors.Create(payload.Name??"",payload.Title??"",payload.Width,payload.Height,payload.RefreshRate,payload.Portrait,payload.AvatarAnimal);return Results.Json(result);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}}
    private async Task<IResult> UpdateAsync(string id,HttpRequest request){try{var payload=await request.ReadFromJsonAsync<MonitorUpdateRequest>();if(payload is null)return Results.BadRequest(new{error="Invalid request."});if(!Enum.TryParse<RemoteAccessMode>(payload.RemoteAccess,true,out var remote))return Results.BadRequest(new{error="Invalid Remote Access mode."});if(!Enum.TryParse<SecurityMode>(payload.SecurityMode,true,out var security))return Results.BadRequest(new{error="Invalid Security mode."});var result=_monitors.UpdateProperties(id,payload.Name??"",payload.Title??"",payload.Width,payload.Height,payload.RefreshRate,payload.Portrait,remote,security,payload.Password,payload.RegenerateApiKey,payload.CollaborationClipboard,payload.CollaborationMouse,payload.CollaborationKeyboard);return Results.Json(result);}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}}
    private async Task<IResult> ReorderAsync(HttpRequest request){try{var payload=await request.ReadFromJsonAsync<MonitorOrderRequest>();if(payload?.Ids is null)return Results.BadRequest(new{error="Invalid request."});_monitors.Reorder(payload.Ids);return Results.NoContent();}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}}
    private async Task<IResult> RuleAsync(string id,HttpRequest request){try{var payload=await request.ReadFromJsonAsync<AccessRuleRequest>();if(payload is null)return Results.BadRequest(new{error="Invalid request."});_monitors.AddAccessRule(id,payload.ClientId,payload.IpAddress,payload.MacAddress,payload.ComputerName,payload.UserName,payload.Permission);return Results.NoContent();}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}}
    private async Task<IResult> UploadAvatarAsync(string id,HttpRequest request){try{if(!request.HasFormContentType)return Results.BadRequest(new{error="multipart/form-data expected"});var form=await request.ReadFormAsync();var file=form.Files.FirstOrDefault();if(file is null)return Results.BadRequest(new{error="No file uploaded."});await using var stream=file.OpenReadStream();_monitors.SetUploadedAvatar(id,stream);return Results.NoContent();}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}}
    private IResult Avatar(string id){var monitor=_monitors.Get(id);if(monitor is null)return Results.NotFound();return monitor.Configuration.AvatarKind.Equals("animal",StringComparison.OrdinalIgnoreCase)?BuiltInAvatar(monitor.Configuration.AvatarValue):Results.File(_monitors.GetAvatarBytes(id),"image/png");}
    private IResult BuiltInAvatar(string id){try{return Results.File(MonitorAvatarService.GetBuiltInPng(id),"image/png");}catch{return Results.NotFound();}}
    private IResult ThumbnailAsync(string id){var monitor=_monitors.Get(id);if(monitor is null||!monitor.Connected||monitor.Health.IsError||monitor.DeviceName is null)return Results.NotFound();try{return Results.File(_capture.GetJpeg(monitor.Configuration.VmuId,monitor.DeviceName),"image/jpeg");}catch(Exception ex){LogStore.Write("WARN","WEB","THUMBNAIL_FAILED",ex.Message,monitor.Configuration.VmuId);return Results.NotFound();}}
    private object ReadLog(HttpRequest request){var search=request.Query["search"].FirstOrDefault();var afterText=request.Query["after"].FirstOrDefault();_ = long.TryParse(afterText,out var after);var services=request.Query["services"].SelectMany(x=>x?.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)??[]).ToArray();return LogStore.Read(search,services.Length==0?null:services,after);}
    private object ReadLogCount(HttpRequest request){var search=request.Query["search"].FirstOrDefault();var services=request.Query["services"].SelectMany(x=>x?.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)??[]).ToArray();return LogStore.Count(search,services.Length==0?null:services);}
    private IResult ExportLog(string format){var rows=LogStore.ReadAll();var bytes=LogExportService.Export(rows,format);var contentType=format.ToLowerInvariant() switch{"csv"=>"text/csv; charset=utf-8","xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","txt"=>"text/plain; charset=utf-8",_=>"application/octet-stream"};return Results.File(bytes,contentType,$"vmu-log-{DateTime.Now:yyyyMMdd-HHmmss}.{format.ToLowerInvariant()}");}
    private IResult Action(Func<MonitorSnapshot> action){try{return Results.Json(action());}catch(Exception ex){return Results.BadRequest(new{error=ex.Message});}}
    private bool IsVmuServerRunning()=>_statusProvider().GetValueOrDefault("VMU_SERVER");
    private string Shell(string title,string body,string? bodyClass=null)=>WebUiRenderer.Shell(title,body,_statusProvider(),_monitors.List(),_settingsProvider().WebUi.ShowHelpLinks,bodyClass);
    private static string BuildRefreshRateOptions(int selected)=>string.Join("",MonitorApplicationService.SupportedRefreshRates.Select(x=>$"<option value='{x}'{(x==selected?" selected":"")}>{x} Hz</option>"));
    private sealed class LiveTerminalClient(HttpContext context){public HttpContext Context{get;}=context;public SemaphoreSlim WriteGate{get;}=new(1,1);}
}

internal sealed class SocketServerService : NetworkService
{
    private readonly MonitorApplicationService _monitors;
    private readonly RemoteActionRegistry _actions;
    private readonly ConcurrentDictionary<Guid,WebSocket> _clients=new();
    private readonly Func<ServerSettings> _settingsProvider;
    public SocketServerService(LogStore logs,MonitorApplicationService monitors,Func<int> webPort,Func<ServerSettings> settings):base("Socket Server","SOCKET",logs){_monitors=monitors;_settingsProvider=settings;_actions=new RemoteActionRegistry(monitors,webPort);}
    protected override void ConfigureApplication(WebApplication app){app.UseWebSockets();app.MapGet("/",()=>Results.Json(new{service="VMU Socket Server",transport="websocket",protocol="VPP",protocolVersion=1,path="/vpp"}));app.Map("/vpp",HandleAsync);}
    private async Task HandleAsync(HttpContext context){if(!context.WebSockets.IsWebSocketRequest){context.Response.StatusCode=400;return;}using var socket=await context.WebSockets.AcceptWebSocketAsync();var id=Guid.NewGuid();_clients[id]=socket;LogStore.Write("INFO","SOCKET","CLIENT_CONNECTED",$"Client {id} connected");try{var buffer=new byte[65536];while(socket.State==WebSocketState.Open){var result=await socket.ReceiveAsync(buffer,context.RequestAborted);if(result.MessageType==WebSocketMessageType.Close)break;var text=Encoding.UTF8.GetString(buffer,0,result.Count);await HandleMessageAsync(socket,text);}}catch(OperationCanceledException){}catch(Exception ex){LogStore.Write("WARN","SOCKET","CLIENT_ERROR",ex.Message);}finally{_clients.TryRemove(id,out _);if(socket.State==WebSocketState.Open)await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"bye",CancellationToken.None);}}
    private async Task HandleMessageAsync(WebSocket socket,string text){try{using var document=JsonDocument.Parse(text);var root=document.RootElement;var protocol=root.TryGetProperty("protocol",out var p)?p.GetString():null;var version=root.TryGetProperty("protocolVersion",out var pv)&&pv.TryGetInt32(out var v)?v:0;var type=root.TryGetProperty("type",out var t)?t.GetString():null;var from=root.TryGetProperty("from",out var f)?f.GetString():null;var recipient=root.TryGetProperty("recipient",out var r)?r.GetString():null;if(protocol!="VPP"||version!=1||type!="command"||string.IsNullOrWhiteSpace(from)||recipient!="vmu")throw new RemoteActionException("INVALID_ENVELOPE","Invalid VPP command envelope.");var requestId=root.TryGetProperty("requestId",out var ri)?ri.GetString():null;if(string.IsNullOrWhiteSpace(requestId))throw new RemoteActionException("INVALID_ENVELOPE","requestId is required.");var action=root.TryGetProperty("action",out var a)?a.GetString():null;if(string.IsNullOrWhiteSpace(action))throw new RemoteActionException("INVALID_ENVELOPE","action is required.");var args=root.TryGetProperty("args",out var ar)?ar:default;var response=_actions.Execute(action,args);await SendAsync(socket,new{protocol="VPP",protocolVersion=1,type="response",from="vmu",recipient=from,requestId,action,success=true,result=response});}catch(RemoteActionException ex){await SendAsync(socket,new{protocol="VPP",protocolVersion=1,type="response",from="vmu",recipient="client",requestId=TryRequestId(text),success=false,error=new{code=ex.Code,message=ex.Message}});}catch(Exception ex){await SendAsync(socket,new{protocol="VPP",protocolVersion=1,type="response",from="vmu",recipient="client",requestId=TryRequestId(text),success=false,error=new{code="INTERNAL_ERROR",message=ex.Message}});}}
    private static string? TryRequestId(string text){try{using var d=JsonDocument.Parse(text);return d.RootElement.TryGetProperty("requestId",out var r)?r.GetString():null;}catch{return null;}}
    private static Task SendAsync(WebSocket socket,object payload)=>socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),WebSocketMessageType.Text,true,CancellationToken.None);
}
