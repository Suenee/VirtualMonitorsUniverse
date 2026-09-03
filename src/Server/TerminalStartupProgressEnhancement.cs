namespace VirtualMonitorsUniverse.Server;

internal static class TerminalStartupProgressEnhancement
{
    public const string Content = """
<style>
.terminalStartingCard h2{white-space:nowrap}.terminalStartingPercent{margin-left:.35em}.terminalStartingProgress.adaptive>div{animation:none;transform:none;width:0;transition:width .12s linear}.terminalStartingProgress.indeterminate>div{width:32%;animation:terminalThirst 1.05s ease-in-out infinite}
.terminalRecursionGuard{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;padding:28px;background:#111820;color:#fff;z-index:8;text-align:center}.terminalRecursionGuard.hidden{display:none}.terminalRecursionGuardCard{max-width:720px;padding:28px 32px;border-radius:12px;background:#1d2732;box-shadow:0 10px 40px #0008}.terminalRecursionGuard h2{margin:0 0 10px}.terminalRecursionGuard p{margin:0;line-height:1.5;color:#d7dde4}.terminalRecursionGuard strong{color:#fff}.terminalPage{position:relative}
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

  let fullscreenHotkey='Win+Alt+F11';
  fetch('/api/settings',{cache:'no-store'}).then(r=>r.ok?r.json():null).then(settings=>{fullscreenHotkey=settings?.hotkeys?.fullscreenExit||fullscreenHotkey;}).catch(()=>{});
  function eventHotkey(event){
    const key=event.key.length===1?event.key.toUpperCase():event.key;
    const parts=[];
    if(event.metaKey)parts.push('Win');
    if(event.ctrlKey)parts.push('Ctrl');
    if(event.altKey)parts.push('Alt');
    if(event.shiftKey)parts.push('Shift');
    if(!['Meta','Control','Alt','Shift'].includes(event.key))parts.push(key);
    return parts.join('+');
  }
  document.addEventListener('keydown',async event=>{
    if(eventHotkey(event)!==fullscreenHotkey)return;
    event.preventDefault();event.stopPropagation();
    if(document.fullscreenElement){try{await document.exitFullscreen();}catch{}}
    document.body.classList.remove('navPeek');
  },true);

  const page=document.querySelector('.terminalPage');
  const guard=document.createElement('div');
  guard.className='terminalRecursionGuard hidden';
  guard.innerHTML='<div class="terminalRecursionGuardCard"><h2>Recursive Terminal placement detected.</h2><p>Terminal <strong id="terminalRecursionTitle"></strong> is displayed on its own virtual monitor. Move this browser window to a physical monitor.</p></div>';
  page?.appendChild(guard);
  const guardTitle=guard.querySelector('#terminalRecursionTitle');
  let targetDisplay=null,monitorTitle=monitorName,guardActive=false,arrangementRefreshAt=0;

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
  }
  async function refreshPlacementData(){
    try{
      const [monitor,arrangement]=await Promise.all([
        fetch('/api/monitors/'+encodeURIComponent(monitorName),{cache:'no-store'}).then(r=>r.ok?r.json():null),
        fetch('/api/arrangement',{cache:'no-store'}).then(r=>r.ok?r.json():[])
      ]);
      monitorTitle=monitor?.configuration?.title||monitorName;
      if(guardTitle)guardTitle.textContent=monitorTitle;
      targetDisplay=arrangement.find(x=>x.isVirtual&&x.monitorName===monitorName)||null;
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
})();
</script>
""";
}
