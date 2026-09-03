namespace VirtualMonitorsUniverse.Server;

internal static class TerminalStartupProgressEnhancement
{
    public const string Content = """
<style>
.terminalStartingCard h2{white-space:nowrap}.terminalStartingPercent{margin-left:.35em}.terminalStartingProgress.adaptive>div{animation:none;transform:none;width:0;transition:width .12s linear}.terminalStartingProgress.indeterminate>div{width:32%;animation:terminalThirst 1.05s ease-in-out infinite}
.terminalRecursionGuard{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;padding:28px;background:#111820;color:#fff;z-index:8;text-align:center}.terminalRecursionGuard.hidden{display:none}.terminalRecursionGuardCard{max-width:720px;padding:28px 32px;border-radius:12px;background:#1d2732;box-shadow:0 10px 40px #0008}.terminalRecursionGuard h2{margin:0 0 10px}.terminalRecursionGuard p{margin:0;line-height:1.5;color:#d7dde4}.terminalRecursionGuard strong{color:#fff}.terminalPage{position:relative}
.terminalPortalPulse{position:fixed;width:34px;height:34px;margin:-17px 0 0 -17px;border:3px solid #fff3a1;border-radius:50%;box-shadow:0 0 0 2px #d4bc57aa,0 0 18px #ffe979cc;pointer-events:none;z-index:4500;animation:terminalPortalFade 2.6s ease-out forwards}@keyframes terminalPortalFade{0%{opacity:1;transform:scale(.72)}18%{opacity:1;transform:scale(1)}100%{opacity:0;transform:scale(1.38)}}
</style>
<script>
(() => {
  const live=document.getElementById('live'),starting=document.querySelector('.terminalStarting'),bar=starting?.querySelector('.terminalStartingProgress'),fill=bar?.querySelector('div'),heading=starting?.querySelector('h2');
  if(!live||!starting||!bar||!fill||!heading)return;
  const monitorName=decodeURIComponent(location.pathname.substring('/monitor/'.length));
  const startedAt=performance.now();
  let expectedMs=0,finished=false,timedOut=false,timer=null;
  const percent=document.createElement('span');percent.className='terminalStartingPercent';heading.appendChild(percent);

  function setIndeterminate(message){bar.classList.remove('adaptive');bar.classList.add('indeterminate');percent.textContent='';if(message)heading.firstChild.textContent=message+' ';}
  function setAdaptive(){bar.classList.remove('indeterminate');bar.classList.add('adaptive');}
  async function report(success,duration,reason){try{await fetch('/api/monitors/'+encodeURIComponent(monitorName)+'/terminal-startup-sample',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({durationMs:Math.round(duration),success,expectedMs:Math.round(expectedMs),reason:reason||null})});}catch{}}
  function complete(){if(finished||!live.naturalWidth||!live.naturalHeight)return;finished=true;clearInterval(timer);fill.style.width='100%';percent.textContent='100%';report(true,performance.now()-startedAt,null);setTimeout(()=>{percent.textContent='';},180);}
  function tick(){if(finished)return;const elapsed=performance.now()-startedAt;if(expectedMs<=0){setIndeterminate('Terminal is starting…');return;}if(elapsed>=expectedMs){if(!timedOut){timedOut=true;report(false,elapsed,'expected-time-exceeded');}setIndeterminate('Terminal is taking longer than expected…');return;}setAdaptive();const value=Math.max(1,Math.min(99,Math.floor((elapsed/expectedMs)*99)));fill.style.width=value+'%';percent.textContent=value+'%';}

  fetch('/api/monitors/'+encodeURIComponent(monitorName)+'/terminal-startup-stats',{cache:'no-store'}).then(r=>r.ok?r.json():null).then(s=>{expectedMs=Number(s?.expectedMilliseconds)||0;tick();}).catch(()=>setIndeterminate('Terminal is starting…'));
  live.addEventListener('load',complete);timer=setInterval(tick,120);tick();if(live.naturalWidth>0&&live.naturalHeight>0)complete();
  window.addEventListener('beforeunload',()=>clearInterval(timer),{once:true});

  const nav=document.getElementById('topNav');
  const hotspot=document.getElementById('topHotspot');
  function fullscreenActive(){return document.body.classList.contains('fullscreen-mode');}
  function revealNav(){if(fullscreenActive())document.body.classList.add('navPeek');}
  function hideNav(){if(fullscreenActive())document.body.classList.remove('navPeek');}
  document.addEventListener('pointermove',event=>{
    if(!fullscreenActive())return;
    if(event.clientY<=12){revealNav();return;}
    if(document.body.classList.contains('navPeek')&&nav&&!nav.matches(':hover')&&event.clientY>Math.max(12,nav.getBoundingClientRect().bottom))hideNav();
  },{passive:true});
  hotspot?.addEventListener('pointerenter',revealNav);
  nav?.addEventListener('pointerenter',revealNav);
  nav?.addEventListener('pointerleave',event=>{if(fullscreenActive()&&event.clientY>0)hideNav();});
  document.addEventListener('fullscreenchange',()=>{if(!document.fullscreenElement)document.body.classList.remove('navPeek');});

  const page=document.querySelector('.terminalPage');
  const guard=document.createElement('div');
  guard.className='terminalRecursionGuard hidden';
  guard.innerHTML='<div class="terminalRecursionGuardCard"><h2>Recursive Terminal placement detected.</h2><p>Terminal <strong id="terminalRecursionTitle"></strong> is displayed on its own virtual monitor. Move this browser window to a physical monitor.</p></div>';
  page?.appendChild(guard);
  const guardTitle=guard.querySelector('#terminalRecursionTitle');
  let targetDisplay=null,monitorTitle=monitorName,guardActive=false,arrangementRefreshAt=0;
  let monitorConfiguration=null,terminalSettings=null,socketPort=8182,terminalF11Hotkey='Win+Alt+F11',mouseImmediate=false;
  let vppSocket=null,vppConnecting=null,insideActiveImage=false;

  function overlapArea(a,b){const width=Math.max(0,Math.min(a.right,b.right)-Math.max(a.left,b.left));const height=Math.max(0,Math.min(a.bottom,b.bottom)-Math.max(a.top,b.top));return width*height;}
  function browserRects(){
    const raw={left:window.screenX,top:window.screenY,right:window.screenX+window.outerWidth,bottom:window.screenY+window.outerHeight};
    const dpr=Math.max(1,window.devicePixelRatio||1);
    const scaled={left:raw.left*dpr,top:raw.top*dpr,right:raw.right*dpr,bottom:raw.bottom*dpr};
    return [raw,scaled];
  }
  function recursionDetected(){
    if(!targetDisplay)return false;
    const inset=24;
    const target={left:targetDisplay.x+inset,top:targetDisplay.y+inset,right:targetDisplay.x+targetDisplay.width-inset,bottom:targetDisplay.y+targetDisplay.height-inset};
    return browserRects().some(rect=>{
      const area=Math.max(1,(rect.right-rect.left)*(rect.bottom-rect.top));
      const overlap=overlapArea(rect,target);
      const ratio=overlap/area;
      const centerX=(rect.left+rect.right)/2,centerY=(rect.top+rect.bottom)/2;
      const centerInside=centerX>=target.left&&centerX<=target.right&&centerY>=target.top&&centerY<=target.bottom;
      return ratio>=0.08||(centerInside&&ratio>=0.03);
    });
  }
  function applyGuard(active){
    if(active===guardActive)return;
    guardActive=active;
    guard.classList.toggle('hidden',!active);
    live.style.visibility=active?'hidden':'';
    if(starting)starting.style.visibility=active?'hidden':'';
    if(active)insideActiveImage=false;
  }
  async function refreshPlacementData(){
    try{
      const [monitor,arrangement,settings]=await Promise.all([
        fetch('/api/monitors/'+encodeURIComponent(monitorName),{cache:'no-store'}).then(r=>r.ok?r.json():null),
        fetch('/api/arrangement',{cache:'no-store'}).then(r=>r.ok?r.json():[]),
        terminalSettings?Promise.resolve(terminalSettings):fetch('/api/settings',{cache:'no-store'}).then(r=>r.ok?r.json():null)
      ]);
      monitorConfiguration=monitor?.configuration||monitorConfiguration;
      monitorTitle=monitorConfiguration?.title||monitorName;
      if(guardTitle)guardTitle.textContent=monitorTitle;
      targetDisplay=arrangement.find(x=>x.isVirtual&&x.monitorName===monitorName)||null;
      if(settings){
        terminalSettings=settings;
        socketPort=Number(settings.socket?.port)||8182;
        terminalF11Hotkey=settings.hotkeys?.terminalF11Forward||settings.hotkeys?.fullscreenExit||'Win+Alt+F11';
        mouseImmediate=settings.terminalInput?.mousePassthroughImmediately===true;
      }
    }catch{}
  }
  async function placementTick(){
    const now=Date.now();
    if(now>=arrangementRefreshAt){arrangementRefreshAt=now+2000;await refreshPlacementData();}
    applyGuard(recursionDetected());
  }
  const placementTimer=setInterval(placementTick,400);
  window.addEventListener('resize',placementTick,{passive:true});
  window.addEventListener('focus',placementTick,{passive:true});
  document.addEventListener('visibilitychange',()=>{if(!document.hidden)placementTick();});
  placementTick();
  window.addEventListener('beforeunload',()=>clearInterval(placementTimer),{once:true});

  function activeImageRect(){
    const rect=live.getBoundingClientRect();
    const sourceWidth=live.naturalWidth||monitorConfiguration?.width||0;
    const sourceHeight=live.naturalHeight||monitorConfiguration?.height||0;
    if(rect.width<=0||rect.height<=0||sourceWidth<=0||sourceHeight<=0)return null;
    const sourceAspect=sourceWidth/sourceHeight,hostAspect=rect.width/rect.height;
    let width,height,left,top;
    if(hostAspect>sourceAspect){height=rect.height;width=height*sourceAspect;left=rect.left+(rect.width-width)/2;top=rect.top;}
    else{width=rect.width;height=width/sourceAspect;left=rect.left;top=rect.top+(rect.height-height)/2;}
    return {left,top,right:left+width,bottom:top+height,width,height};
  }
  function pointInImage(clientX,clientY){
    const rect=activeImageRect();
    if(!rect||clientX<rect.left||clientX>rect.right||clientY<rect.top||clientY>rect.bottom)return null;
    return {x:Math.max(0,Math.min(1,(clientX-rect.left)/rect.width)),y:Math.max(0,Math.min(1,(clientY-rect.top)/rect.height))};
  }
  function pulse(clientX,clientY){
    const old=document.querySelector('.terminalPortalPulse');old?.remove();
    const ring=document.createElement('div');ring.className='terminalPortalPulse';ring.style.left=clientX+'px';ring.style.top=clientY+'px';document.body.appendChild(ring);ring.addEventListener('animationend',()=>ring.remove(),{once:true});
  }
  function vppId(){return globalThis.crypto?.randomUUID?.()||('vmu-'+Date.now()+'-'+Math.random().toString(16).slice(2));}
  function ensureVppSocket(){
    if(vppSocket?.readyState===WebSocket.OPEN)return Promise.resolve(vppSocket);
    if(vppConnecting)return vppConnecting;
    vppConnecting=new Promise((resolve,reject)=>{
      const protocol=location.protocol==='https:'?'wss':'ws';
      const socket=new WebSocket(protocol+'://'+location.hostname+':'+socketPort+'/');
      const timeout=setTimeout(()=>{try{socket.close();}catch{}reject(new Error('VMU Socket Server connection timed out.'));},1500);
      socket.onopen=()=>{clearTimeout(timeout);vppSocket=socket;vppConnecting=null;resolve(socket);};
      socket.onmessage=()=>{};
      socket.onerror=()=>{};
      socket.onclose=()=>{clearTimeout(timeout);if(vppSocket===socket)vppSocket=null;if(vppConnecting){vppConnecting=null;reject(new Error('VMU Socket Server is unavailable.'));}};
    });
    return vppConnecting;
  }
  async function sendTerminalAction(method,args){
    try{
      const socket=await ensureVppSocket();
      socket.send(JSON.stringify({protocolVersion:1,id:vppId(),type:'call',from:'sum',recipient:'vmu',method,args,expectsResponse:false,timestamp:new Date().toISOString()}));
      return true;
    }catch(error){console.warn('VMU Terminal input failed:',error);return false;}
  }
  function mouseButton(button){return button===2?'right':button===1?'middle':'left';}
  async function enterMouse(point,clientX,clientY,button='none'){
    if(guardActive||!monitorConfiguration?.collaborationMouse||!document.hasFocus())return false;
    pulse(clientX,clientY);
    return await sendTerminalAction('terminal_mouse_enter',{monitor:monitorName,x:point.x,y:point.y,button});
  }

  page?.addEventListener('pointerdown',event=>{
    if(mouseImmediate||guardActive||!monitorConfiguration?.collaborationMouse||!document.hasFocus())return;
    const point=pointInImage(event.clientX,event.clientY);if(!point)return;
    event.preventDefault();event.stopPropagation();
    enterMouse(point,event.clientX,event.clientY,mouseButton(event.button));
  },true);
  page?.addEventListener('contextmenu',event=>{if(!mouseImmediate&&monitorConfiguration?.collaborationMouse&&pointInImage(event.clientX,event.clientY)){event.preventDefault();event.stopPropagation();}},true);
  document.addEventListener('pointermove',event=>{
    const point=pointInImage(event.clientX,event.clientY);
    const inside=!!point&&!guardActive;
    if(mouseImmediate&&inside&&!insideActiveImage&&monitorConfiguration?.collaborationMouse&&document.hasFocus())enterMouse(point,event.clientX,event.clientY,'none');
    insideActiveImage=inside;
  },{passive:true});
  window.addEventListener('blur',()=>{insideActiveImage=false;});

  function keyName(event){
    const key=event.key;
    if(['Control','Shift','Alt','Meta'].includes(key))return null;
    if(/^F([1-9]|1[0-9]|2[0-4])$/.test(key))return key;
    if(key.length===1&&/[a-z0-9]/i.test(key))return key.toUpperCase();
    const names={Escape:'Esc',Enter:'Enter',Space:'Space',Tab:'Tab',Home:'Home',End:'End',PageUp:'PageUp',PageDown:'PageDown',ArrowUp:'Up',ArrowDown:'Down',ArrowLeft:'Left',ArrowRight:'Right',Insert:'Insert',Delete:'Delete'};
    return names[key]||null;
  }
  function eventHotkey(event){
    const key=keyName(event);if(!key)return '';
    const parts=[];
    if(event.metaKey)parts.push('Win');
    if(event.ctrlKey)parts.push('Ctrl');
    if(event.altKey)parts.push('Alt');
    if(event.shiftKey)parts.push('Shift');
    parts.push(key);
    return parts.join('+');
  }
  document.addEventListener('keydown',event=>{
    if(!monitorConfiguration?.collaborationKeyboard||eventHotkey(event)!==terminalF11Hotkey)return;
    event.preventDefault();event.stopPropagation();
    sendTerminalAction('terminal_key_press',{monitor:monitorName,key:'F11'});
  },true);

  window.addEventListener('beforeunload',()=>{try{vppSocket?.close();}catch{}},{once:true});
})();
</script>
""";
}
