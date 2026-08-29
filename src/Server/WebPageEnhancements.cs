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
        _ when title.StartsWith("Monitor ", StringComparison.Ordinal) => MonitorProperties,
        _ when title.StartsWith("Terminal ", StringComparison.Ordinal) => Terminal,
        _ => string.Empty
    };

    public const string Settings = """
<script>
(() => {
  const form = document.getElementById('form');
  const preview = document.getElementById('preview');
  const exit = document.getElementById('exit');
  if (!form || !preview || !exit) return;

  [...exit.options].filter(x => x.value === 'Uninstall' || x.text === 'Uninstall').forEach(x => x.remove());

  const previewRow = preview.closest('div.formgrid');
  if (previewRow && !document.getElementById('snapTolerance')) {
    const label = document.createElement('label');
    label.textContent = 'Arrangement Snap';
    const host = document.createElement('div');
    host.innerHTML = '<input id="snapTolerance" class="smallNumber" type="number" min="5" max="50" step="1"> px';
    previewRow.append(label, host);
  }

  const snap = document.getElementById('snapTolerance');
  let settingsSnapshot = null;
  fetch('/api/settings', { cache: 'no-store' }).then(r => r.json()).then(s => {
    settingsSnapshot = s;
    if (snap) snap.value = Math.min(50, Math.max(5, s.webUi?.arrangementSnapTolerancePx ?? 15));
    if (exit.value === 'Uninstall') exit.value = 'Disconnect';
  }).catch(() => {});

  form.onsubmit = async e => {
    e.preventDefault();
    dependency();
    const base = settingsSnapshot || original;
    const x = {
      vmu: { interface: q('vmuInterface').value, port: +q('vmuPort').value },
      web: { interface: q('webInterface').value, port: +q('webPort').value },
      socket: { interface: q('socketInterface').value, port: +q('socketPort').value },
      logging: { retentionMinutes: +q('retention').value * 1440 },
      webUi: {
        monitorPreviewRefreshSeconds: +q('preview').value,
        arrangementSnapTolerancePx: Math.min(50, Math.max(5, +(snap?.value || 15)))
      },
      exit: { monitorAction: q('exit').value === 'Keep' ? 'Keep' : 'Disconnect', restoreServices: q('restore').checked },
      serviceState: base.serviceState
    };
    const r = await fetch('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(x) });
    if (!r.ok) { q('error').textContent = (await r.json()).error; return; }
    const result = await r.json();
    if (result.restartRequired) location.href = result.targetUrl; else location.reload();
  };
})();
</script>
""";

    public const string Monitors = """
<style>
.monitorcard.dragging{opacity:.72;border-color:#246bce;box-shadow:0 6px 18px #0002;transition:none}
.dragGhost{display:none!important}.monitorcard.dragSource{opacity:1!important}
.addmonitor .plus{font-size:62px;line-height:.86;color:#5d1b9b;position:relative;margin-bottom:18px}
.addmonitor .plus::after{content:"";position:absolute;left:50%;transform:translateX(-50%);bottom:-12px;width:64px;height:7px;background:#5d1b9b}
.addmonitor h3{margin:4px 0 0;color:#5d1b9b;text-decoration:underline}
</style>
<script>
(() => {
  const grid = document.getElementById('grid');
  if (!grid) return;

  function wireSimpleDrag() {
    grid.querySelectorAll('.move').forEach(handle => {
      if (handle.dataset.simpleDrag === '1') return;
      handle.dataset.simpleDrag = '1';
      let card = null;
      let pointer = null;

      handle.onpointerdown = e => {
        e.preventDefault(); e.stopPropagation();
        card = handle.closest('.monitorcard[data-id]');
        if (!card) return;
        pointer = e.pointerId;
        handle.setPointerCapture(pointer);
        card.classList.remove('dragSource');
        card.classList.add('dragging');
      };
      handle.onpointermove = e => {
        if (!card || e.pointerId !== pointer) return;
        const target = document.elementFromPoint(e.clientX, e.clientY)?.closest('.monitorcard[data-id]');
        if (!target || target === card) return;
        const r = target.getBoundingClientRect();
        const before = e.clientY < r.top + r.height / 2 || (Math.abs(e.clientY - (r.top + r.height / 2)) < r.height * .2 && e.clientX < r.left + r.width / 2);
        grid.insertBefore(card, before ? target : target.nextSibling);
      };
      const finish = async e => {
        if (!card || (e && e.pointerId !== pointer)) return;
        try { if (pointer !== null && handle.hasPointerCapture(pointer)) handle.releasePointerCapture(pointer); } catch {}
        card.classList.remove('dragging', 'dragSource');
        card = null; pointer = null;
        await saveOrder();
      };
      handle.onpointerup = finish;
      handle.onpointercancel = finish;
    });
  }

  const observer = new MutationObserver(wireSimpleDrag);
  observer.observe(grid, { childList: true, subtree: true });
  wireSimpleDrag();
})();
</script>
""";

    public const string MonitorProperties = """
<style>
.streamHelp{color:#687078;font-size:13px;line-height:1.35;margin:4px 0 10px 172px}.streamSaveRow{display:flex;justify-content:flex-end;gap:10px;align-items:center}.streamSaved{color:#218838;font-size:13px}.streamFixed.hidden{display:none!important}
@media(max-width:600px){.streamHelp{margin-left:0}}
</style>
<script>
(() => {
  const form = document.getElementById('f');
  const windowsFieldset = [...document.querySelectorAll('fieldset')].find(x => x.querySelector('legend')?.textContent === 'Windows Settings');
  if (!form || !windowsFieldset || document.getElementById('streamMode')) return;
  const monitorName = decodeURIComponent(location.pathname.split('/').filter(Boolean).pop() || '');
  const fieldset = document.createElement('fieldset');
  fieldset.innerHTML = `
    <legend>Terminal Streaming</legend>
    <label>Adaptation <select id="streamMode"><option value="Automatic">Automatic</option><option value="PreferQuality">Prefer Quality</option><option value="Fixed">Fixed</option></select></label>
    <p id="streamHelp" class="streamHelp"></p>
    <div id="streamFixed" class="streamFixed">
      <label style="display:grid;grid-template-columns:160px 1fr;gap:12px;margin:10px 0;align-items:center">Maximum Width <select id="streamWidth"><option value="1280">1280 px</option><option value="1600">1600 px</option><option value="1920">1920 px</option></select></label>
      <label style="display:grid;grid-template-columns:160px 1fr;gap:12px;margin:10px 0;align-items:center">JPEG Quality <input id="streamQuality" type="number" min="45" max="90" step="1"></label>
    </div>
    <p class="streamHelp">Localhost always uses the full 1920 px / quality 68 profile and never lowers transport quality because of network-congestion heuristics.</p>
    <div class="streamSaveRow"><span id="streamSaved" class="streamSaved"></span><button id="streamSave" type="button">Save Stream Settings</button></div>`;
  form.insertBefore(fieldset, windowsFieldset);

  const mode = document.getElementById('streamMode');
  const fixed = document.getElementById('streamFixed');
  const width = document.getElementById('streamWidth');
  const quality = document.getElementById('streamQuality');
  const help = document.getElementById('streamHelp');
  const saved = document.getElementById('streamSaved');
  const save = document.getElementById('streamSave');
  const helpText = {
    Automatic: 'VMU adapts JPEG quality and, when necessary, transport resolution to keep latency low. Quality is reduced quickly under sustained pressure and restored gradually.',
    PreferQuality: 'Keeps full transport resolution and quality. If capture or transport cannot keep up, stale frames are skipped instead of lowering image quality.',
    Fixed: 'Uses the selected transport resolution and JPEG quality without automatic quality reduction. Stale frames are still skipped so latency cannot build into a queue.'
  };
  function updateMode(){fixed.classList.toggle('hidden', mode.value !== 'Fixed');help.textContent=helpText[mode.value] || '';saved.textContent='';}
  mode.onchange=updateMode;width.onchange=()=>saved.textContent='';quality.oninput=()=>saved.textContent='';

  async function load(){
    try {
      const r = await fetch('/api/monitors/'+encodeURIComponent(monitorName)+'/stream-settings',{cache:'no-store'});
      if(!r.ok) return;
      const s=await r.json();mode.value=s.mode||'Automatic';width.value=String(s.fixedMaximumWidth||1920);quality.value=s.fixedJpegQuality||68;updateMode();
    } catch {}
  }
  save.onclick=async()=>{
    save.disabled=true;saved.textContent='';
    try{
      const payload={mode:mode.value,fixedMaximumWidth:+width.value,fixedJpegQuality:+quality.value};
      const r=await fetch('/api/monitors/'+encodeURIComponent(monitorName)+'/stream-settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
      if(!r.ok)throw new Error((await r.json()).error||'Could not save Terminal stream settings.');
      saved.textContent='Saved';
    }catch(ex){document.getElementById('error').textContent=ex.message;}finally{save.disabled=false;}
  };
  load();
})();
</script>
""";

    public const string Terminal = """
<style>
/* Transport resolution must never change the visible Terminal viewport size. */
.terminalImage{width:100%!important;height:100%!important;max-width:100%!important;max-height:100%!important;object-fit:contain!important}
</style>
<script>
(() => {
  let previousReady = null;
  async function reconnectTerminal() {
    try {
      const [s, m] = await Promise.all([
        fetch('/api/status', { cache: 'no-store' }).then(r => { if (!r.ok) throw new Error(); return r.json(); }),
        fetch(location.pathname.replace('/monitor/', '/api/monitors/'), { cache: 'no-store' }).then(r => { if (!r.ok) throw new Error(); return r.json(); })
      ]);
      const vmu = s.services.find(x => x.key === 'VMU_SERVER')?.running === true;
      const ready = vmu && m.connected && !m.health.isError;
      if (ready && previousReady === false) {
        stopStream();
        startStream();
      }
      if (!ready) stopStream();
      previousReady = ready;
    } catch {
      previousReady = false;
      stopStream();
    }
  }
  setInterval(reconnectTerminal, 750);
  window.addEventListener('online', reconnectTerminal);
  window.addEventListener('pageshow', reconnectTerminal);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) reconnectTerminal(); });
})();
</script>
""";

    public const string Log = """
<script>
(() => {
  const actions = document.querySelector('.logActions');
  if (!actions || document.getElementById('logCancel')) return;
  const cancel = document.createElement('button');
  cancel.id = 'logCancel';
  cancel.type = 'button';
  cancel.textContent = 'Cancel';
  cancel.onclick = () => location.href = '/';
  actions.appendChild(cancel);
})();
</script>
""";
}
