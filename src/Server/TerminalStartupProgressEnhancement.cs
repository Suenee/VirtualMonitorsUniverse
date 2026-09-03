namespace VirtualMonitorsUniverse.Server;

internal static class TerminalStartupProgressEnhancement
{
    public const string Content = """
<style>
.terminalStartingCard h2{white-space:nowrap}.terminalStartingPercent{margin-left:.35em}.terminalStartingProgress.adaptive>div{animation:none;transform:none;width:0;transition:width .12s linear}.terminalStartingProgress.indeterminate>div{width:32%;animation:terminalThirst 1.05s ease-in-out infinite}
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
})();
</script>
""";
}
