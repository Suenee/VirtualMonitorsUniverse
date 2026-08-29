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

    public const string Terminal = """
<script>
(() => {
  let previousOnline = true;
  async function reconnectTerminal() {
    try {
      const [s, m] = await Promise.all([
        fetch('/api/status', { cache: 'no-store' }).then(r => { if (!r.ok) throw new Error(); return r.json(); }),
        fetch(location.pathname.replace('/monitor/', '/api/monitors/'), { cache: 'no-store' }).then(r => { if (!r.ok) throw new Error(); return r.json(); })
      ]);
      const vmu = s.services.find(x => x.key === 'VMU_SERVER')?.running === true;
      const ready = vmu && m.connected && !m.health.isError;
      if (!previousOnline && ready) {
        stopStream();
        startStream();
      }
      previousOnline = true;
    } catch {
      previousOnline = false;
      stopStream();
    }
  }
  setInterval(reconnectTerminal, 1000);
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
