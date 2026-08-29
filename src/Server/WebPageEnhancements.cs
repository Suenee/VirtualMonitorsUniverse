namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Small, page-scoped web-client enhancements. Keeping these behaviors separate
/// from the base renderer makes regression fixes auditable and avoids rewriting
/// working Terminal/fullscreen and monitor-property UI code.
/// </summary>
internal static class WebPageEnhancements
{
    public static string ForTitle(string title) => title switch
    {
        "Settings" => Settings,
        "Monitors" => Monitors,
        "Log" => Log,
        _ when title.StartsWith("Terminal ", StringComparison.Ordinal) => Terminal,
        _ => string.Empty
    };

    private const string SharedMonitorTools = """
<script>
(() => {
  if(window.vmuSharedMonitorTools)return;window.vmuSharedMonitorTools=true;
  async function pngFromJpeg(blob){const bitmap=await createImageBitmap(blob);try{const canvas=document.createElement('canvas');canvas.width=bitmap.width;canvas.height=bitmap.height;const context=canvas.getContext('2d',{alpha:false});if(!context)throw new Error('Canvas capture is unavailable.');context.drawImage(bitmap,0,0);return await new Promise((resolve,reject)=>canvas.toBlob(x=>x?resolve(x):reject(new Error('PNG encoding failed.')),'image/png'));}finally{bitmap.close();}}
  window.vmuCaptureMonitor=async name=>{if(!navigator.clipboard?.write||typeof ClipboardItem==='undefined'||typeof createImageBitmap==='undefined')throw new Error('Screenshot is unavailable in this browser/context.');const response=await fetch('/api/monitors/'+encodeURIComponent(name)+'/thumbnail?force=1&t='+Date.now(),{cache:'no-store'});if(!response.ok)throw new Error('VMU could not capture the monitor image.');const png=await pngFromJpeg(await response.blob());await navigator.clipboard.write([new ClipboardItem({'image/png':png})]);};
  function decorateMenus(){document.querySelectorAll('.monitorNavWrap').forEach(w=>{const menu=w.querySelector('.monitorMenu');if(!menu||menu.querySelector('.vmuMenuScreenshot'))return;const hr=menu.querySelector('hr');const shot=document.createElement('button');shot.type='button';shot.className='vmuMenuScreenshot';shot.textContent='📷 Screenshot';shot.style.cssText='display:block;width:100%;text-align:left;padding:8px 9px;border:0;background:transparent;border-radius:4px;cursor:pointer';shot.onclick=async e=>{e.preventDefault();e.stopPropagation();try{await window.vmuCaptureMonitor(w.dataset.monitor);w.classList.remove('menuOpen');}catch(error){alert(error?.message||'Screenshot failed.');}};menu.insertBefore(shot,hr);});}
  const host=document.getElementById('monitorNavHost');if(host){new MutationObserver(decorateMenus).observe(host,{childList:true,subtree:true});decorateMenus();}
})();
</script>
""";

    public const string Settings = SharedMonitorTools + """
<script>
(() => {
  const form = document.getElementById('form');
  const preview = document.getElementById('preview');
  const exit = document.getElementById('exit');
  if (!form || !preview || !exit) return;
  [...exit.options].filter(x => x.value === 'Uninstall' || x.text === 'Uninstall').forEach(x => x.remove());
  const previewRow = preview.closest('div.formgrid');
  if (previewRow && !document.getElementById('snapTolerance')) {
    const label = document.createElement('label'); label.textContent = 'Arrangement Snap';
    const host = document.createElement('div'); host.innerHTML = '<input id="snapTolerance" class="smallNumber" type="number" min="5" max="50" step="1"> px';
    previewRow.append(label, host);
  }
  const snap = document.getElementById('snapTolerance'); let settingsSnapshot = null;
  fetch('/api/settings', { cache: 'no-store' }).then(r => r.json()).then(s => { settingsSnapshot = s; if (snap) snap.value = Math.min(50, Math.max(5, s.webUi?.arrangementSnapTolerancePx ?? 15)); if (exit.value === 'Uninstall') exit.value = 'Disconnect'; }).catch(() => {});
  form.onsubmit = async e => {
    e.preventDefault(); dependency(); const base = settingsSnapshot || original;
    const x = { vmu:{interface:q('vmuInterface').value,port:+q('vmuPort').value}, web:{interface:q('webInterface').value,port:+q('webPort').value}, socket:{interface:q('socketInterface').value,port:+q('socketPort').value}, logging:{retentionMinutes:+q('retention').value*1440}, webUi:{monitorPreviewRefreshSeconds:+q('preview').value,arrangementSnapTolerancePx:Math.min(50,Math.max(5,+(snap?.value||15)))}, exit:{monitorAction:q('exit').value==='Keep'?'Keep':'Disconnect',restoreServices:q('restore').checked}, serviceState:base.serviceState };
    const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(x)}); if(!r.ok){q('error').textContent=(await r.json()).error;return;} const result=await r.json(); if(result.restartRequired)location.href=result.targetUrl;else location.reload();
  };
})();
</script>
""";

    public const string Monitors = SharedMonitorTools + """
<style>
.monitorcard.dragging{opacity:1!important;border-color:#246bce;box-shadow:0 6px 18px #0002;transition:none}.monitorcard.dragSource{opacity:1!important}.vmuDragGhost{position:fixed!important;z-index:5000!important;pointer-events:none!important;opacity:.92!important;box-shadow:0 10px 28px #0005!important;margin:0!important}.vmuDragGhost .cardtools{display:none!important}.monitorcard.dropBefore{box-shadow:-5px 0 0 #246bce}.monitorcard.dropAfter{box-shadow:5px 0 0 #246bce}
.addmonitor .plus{font-size:62px;line-height:.86;color:#5d1b9b;position:relative;margin-bottom:18px}.addmonitor .plus::after{content:"";position:absolute;left:50%;transform:translateX(-50%);bottom:-12px;width:64px;height:7px;background:#5d1b9b}.addmonitor h3{margin:4px 0 0;color:#5d1b9b;text-decoration:underline}
</style>
<script>
(() => {
  const grid=document.getElementById('grid');if(!grid)return;
  async function persistOrder(){const response=await fetch('/api/monitors/order',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ids:[...grid.querySelectorAll('.monitorcard[data-id]')].map(x=>x.dataset.id)})});if(!response.ok)throw new Error('Monitor order could not be saved.');await window.vmuRefreshMonitorNav?.();}
  function clearTargets(){grid.querySelectorAll('.dropBefore,.dropAfter').forEach(x=>x.classList.remove('dropBefore','dropAfter'));}
  function wireStableDrag(){grid.querySelectorAll('.move').forEach(old=>{if(old.dataset.vmuStableDrag==='1')return;const handle=old.cloneNode(true);handle.dataset.vmuStableDrag='1';old.replaceWith(handle);let card=null,pointer=null,ghost=null,offsetX=0,offsetY=0;
    handle.onpointerdown=e=>{if(e.button!==0&&e.pointerType==='mouse')return;e.preventDefault();e.stopPropagation();card=handle.closest('.monitorcard[data-id]');if(!card)return;pointer=e.pointerId;handle.setPointerCapture(pointer);const r=card.getBoundingClientRect();offsetX=e.clientX-r.left;offsetY=e.clientY-r.top;ghost=card.cloneNode(true);ghost.classList.add('vmuDragGhost');ghost.style.width=r.width+'px';ghost.style.height=r.height+'px';ghost.style.left=(e.clientX-offsetX)+'px';ghost.style.top=(e.clientY-offsetY)+'px';document.body.appendChild(ghost);card.classList.add('dragging');};
    handle.onpointermove=e=>{if(!card||e.pointerId!==pointer)return;e.preventDefault();if(ghost){ghost.style.left=(e.clientX-offsetX)+'px';ghost.style.top=(e.clientY-offsetY)+'px';}clearTargets();const target=document.elementFromPoint(e.clientX,e.clientY)?.closest('.monitorcard[data-id]');if(!target||target===card)return;const r=target.getBoundingClientRect();const horizontal=Math.abs(e.clientX-(r.left+r.width/2))>=Math.abs(e.clientY-(r.top+r.height/2));const before=horizontal?e.clientX<r.left+r.width/2:e.clientY<r.top+r.height/2;target.classList.add(before?'dropBefore':'dropAfter');grid.insertBefore(card,before?target:target.nextSibling);};
    const finish=async e=>{if(!card||(e&&e.pointerId!==pointer))return;try{if(pointer!==null&&handle.hasPointerCapture(pointer))handle.releasePointerCapture(pointer);}catch{}clearTargets();card.classList.remove('dragging','dragSource');ghost?.remove();card=null;ghost=null;pointer=null;try{await persistOrder();}catch(error){console.error(error);}};handle.onpointerup=finish;handle.onpointercancel=finish;
  });}
  const observer=new MutationObserver(wireStableDrag);observer.observe(grid,{childList:true,subtree:true});wireStableDrag();
})();
</script>
""";

    public const string Terminal = SharedMonitorTools + """
<style>
.terminalToast{position:fixed;right:16px;bottom:16px;z-index:5000;padding:9px 12px;border-radius:7px;background:#282d33;color:#fff;box-shadow:0 3px 12px #0005;font-size:13px}.terminalToast.error{background:#8b1f28}#terminalScreenshot{color:white;font-size:21px;align-self:center}#terminalFreeze{position:absolute;inset:0;margin:auto;max-width:100%;max-height:100%;width:auto;height:auto;z-index:2;pointer-events:none;opacity:0;visibility:hidden;filter:none;transition:opacity .18s ease,filter .18s ease}#terminalFreeze.reconnecting{visibility:visible;opacity:.52;filter:blur(6px)}#live{z-index:1;cursor:default}.terminalStarting{position:absolute;inset:0;z-index:4;display:flex;align-items:center;justify-content:center;background:#292b2f;color:#fff}.terminalStartingCard{width:min(380px,70vw);text-align:center}.terminalStartingCard h2{margin:0 0 16px}.terminalStartingProgress{height:6px;background:#4a4e54;border-radius:4px;overflow:hidden}.terminalStartingProgress>div{height:100%;width:32%;background:#5b9cff;border-radius:4px;animation:terminalThirst 1.05s ease-in-out infinite}@keyframes terminalThirst{0%{transform:translateX(-110%)}100%{transform:translateX(330%)}}
</style>
<script>
(() => {
  let previousReady=null;const live=document.getElementById('live');if(!live)return;live.alt='';const monitorName=decodeURIComponent(location.pathname.substring('/monitor/'.length));const mouseUrl='/api/monitors/'+encodeURIComponent(monitorName)+'/mouse';const terminalPage=live.closest('.terminalPage');const freeze=document.createElement('canvas');freeze.id='terminalFreeze';freeze.setAttribute('aria-hidden','true');terminalPage?.insertBefore(freeze,live.nextSibling);const starting=document.createElement('div');starting.className='terminalStarting';starting.innerHTML='<div class="terminalStartingCard"><h2>Terminal is starting…</h2><div class="terminalStartingProgress"><div></div></div></div>';terminalPage?.appendChild(starting);const freezeContext=freeze.getContext('2d',{alpha:false});let haveFreeze=false,reconnecting=false,frameTimer=null,mouseAllowed=null,mouseHandoff=false,firstFrame=false;
  function rememberFrame(){if(document.hidden||!live.naturalWidth||!live.naturalHeight||live.classList.contains('hidden'))return;try{if(freeze.width!==live.naturalWidth||freeze.height!==live.naturalHeight){freeze.width=live.naturalWidth;freeze.height=live.naturalHeight;}freezeContext?.drawImage(live,0,0,freeze.width,freeze.height);haveFreeze=true;}catch{}}
  function showReconnectFrame(){rememberFrame();reconnecting=true;if(firstFrame&&haveFreeze)freeze.classList.add('reconnecting');live.style.opacity='0';if(!firstFrame)starting.classList.remove('hidden');}
  function showLiveFrame(){if(!live.naturalWidth||!live.naturalHeight)return;firstFrame=true;reconnecting=false;live.style.opacity='1';freeze.classList.remove('reconnecting');starting.classList.add('hidden');rememberFrame();}
  live.addEventListener('error',showReconnectFrame);live.addEventListener('load',showLiveFrame);frameTimer=setInterval(()=>{if(!reconnecting)rememberFrame();if(reconnecting&&live.naturalWidth>0&&live.naturalHeight>0)showLiveFrame();},1000);window.addEventListener('beforeunload',()=>clearInterval(frameTimer),{once:true});
  async function reconnectTerminal(){try{const [s,m]=await Promise.all([fetch('/api/status',{cache:'no-store'}).then(r=>{if(!r.ok)throw new Error();return r.json();}),fetch(location.pathname.replace('/monitor/','/api/monitors/'),{cache:'no-store'}).then(r=>{if(!r.ok)throw new Error();return r.json();})]);const vmu=s.services.find(x=>x.key==='VMU_SERVER')?.running===true;const ready=vmu&&m.connected&&!m.health.isError;mouseAllowed=m.configuration?.collaborationMouse===true;if(ready&&previousReady===false){showReconnectFrame();stopStream();startStream();}else if(!ready){showReconnectFrame();stopStream();}previousReady=ready;}catch{mouseAllowed=null;previousReady=false;showReconnectFrame();stopStream();}}
  function toast(message,error=false){document.getElementById('terminalToast')?.remove();const box=document.createElement('div');box.id='terminalToast';box.className='terminalToast'+(error?' error':'');box.textContent=message;document.body.appendChild(box);setTimeout(()=>box.remove(),2600);}async function beep(){try{const A=window.AudioContext||window.webkitAudioContext;if(!A)return;const c=new A(),o=c.createOscillator(),g=c.createGain();o.frequency.value=880;g.gain.value=.06;o.connect(g).connect(c.destination);o.start();o.stop(c.currentTime+.08);o.onended=()=>c.close();}catch{}}
  async function captureTerminalFrame(){try{await window.vmuCaptureMonitor(monitorName);await beep();toast('Monitor screenshot copied to clipboard.');}catch(error){toast(error?.message||'Screenshot failed.',true);}}
  async function postMouse(payload){const response=await fetch(mouseUrl,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});if(!response.ok){let message='Terminal mouse input failed.';try{message=(await response.json()).error||message;}catch{}throw new Error(message);}}
  function renderedPoint(e){const rect=live.getBoundingClientRect();if(rect.width<=0||rect.height<=0)return null;const x=e.clientX-rect.left,y=e.clientY-rect.top;if(x<0||y<0||x>rect.width||y>rect.height)return null;return{x:Math.max(0,Math.min(1,x/rect.width)),y:Math.max(0,Math.min(1,y/rect.height))};}
  live.addEventListener('click',async e=>{if(mouseHandoff||reconnecting||live.classList.contains('hidden'))return;if(mouseAllowed!==true){toast(mouseAllowed===false?'Mouse is disabled in Monitor Properties.':'Mouse control is not ready yet.',true);return;}const point=renderedPoint(e);if(!point)return;mouseHandoff=true;try{await postMouse({type:'portal',x:point.x,y:point.y,button:Math.round(e.screenX),delta:Math.round(e.screenY)});}catch(error){toast(error?.message||'Terminal mouse input failed.',true);}finally{mouseHandoff=false;}});
  const fullscreen=document.getElementById('fullscreenToggle');if(fullscreen&&!document.getElementById('terminalScreenshot')){const screenshot=document.createElement('button');screenshot.id='terminalScreenshot';screenshot.className='fullscreenToggle iconButton';screenshot.type='button';screenshot.textContent='📷';screenshot.setAttribute('aria-label','Copy monitor screenshot');screenshot.title='Copy monitor screenshot';screenshot.addEventListener('click',captureTerminalFrame);fullscreen.parentElement?.insertBefore(screenshot,fullscreen);}
  reconnectTerminal();setInterval(reconnectTerminal,750);window.addEventListener('online',reconnectTerminal);window.addEventListener('pageshow',reconnectTerminal);
})();
</script>
""";

    public const string Log = SharedMonitorTools + """
<script>
(() => { const actions=document.querySelector('.logActions');if(!actions||document.getElementById('logCancel'))return;const cancel=document.createElement('button');cancel.id='logCancel';cancel.type='button';cancel.textContent='Cancel';cancel.onclick=()=>location.href='/';actions.appendChild(cancel); })();
</script>
""";
}
