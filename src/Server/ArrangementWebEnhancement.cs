namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Page-scoped arrangement enhancements. The editor keeps one live dragged DOM
/// node, uses configurable Windows-coordinate snapping, rejects isolated monitor
/// drops before Apply, and preserves the server-side Windows-style rollback model.
/// </summary>
internal static class ArrangementWebEnhancement
{
    public const string Script = """
<script>
let arrangementSnapTolerance = 15;
fetch('/api/settings', { cache: 'no-store' }).then(r => r.json()).then(s => {
  arrangementSnapTolerance = Math.min(50, Math.max(5, s.webUi?.arrangementSnapTolerancePx ?? 15));
}).catch(() => {});

// The arrangement page does not need the redundant VMU home shortcut in the
// monitor navigation strip. Primary navigation remains available from the menu.
document.querySelector('.vmuAppHome')?.remove();

// Override the first-pass snap function with the configured tolerance expressed
// in real Windows desktop pixels, independent of browser zoom or workspace scale.
snap = function (i, nx, ny) {
  if (!snapToggle.checked) return [Math.round(nx), Math.round(ny)];
  const d = displays[i], threshold = arrangementSnapTolerance;
  for (let j = 0; j < displays.length; j++) {
    if (j === i) continue;
    const o = displays[j];
    const cx = [[nx, o.x + o.width], [nx + d.width, o.x], [nx, o.x], [nx + d.width, o.x + o.width]];
    const cy = [[ny, o.y + o.height], [ny + d.height, o.y], [ny, o.y], [ny + d.height, o.y + o.height]];
    for (const [a, b] of cx) if (Math.abs(a - b) <= threshold) { nx += b - a; break; }
    for (const [a, b] of cy) if (Math.abs(a - b) <= threshold) { ny += b - a; break; }
  }
  return [Math.round(nx), Math.round(ny)];
};

window.vmuArrangementConnected = function (items) {
  if (items.length <= 1) return true;
  const touches = (a, b) => {
    const horizontalGap = Math.max(0, Math.max(a.x - (b.x + b.width), b.x - (a.x + a.width)));
    const verticalGap = Math.max(0, Math.max(a.y - (b.y + b.height), b.y - (a.y + a.height)));
    return horizontalGap <= 1 && verticalGap <= 1;
  };
  const seen = new Set([0]), queue = [0];
  while (queue.length) {
    const current = queue.shift();
    for (let i = 0; i < items.length; i++) {
      if (seen.has(i) || !touches(items[current], items[i])) continue;
      seen.add(i); queue.push(i);
    }
  }
  return seen.size === items.length;
};

// Replace the first-pass drag binding with a stable Pointer Events implementation.
// The dragged DOM node stays alive for the complete pointer capture; the full
// topology is re-rendered only after the pointer is released.
wire = function () {
  arr.querySelectorAll('.arrdisplay').forEach(el => {
    let drag = null;
    let lastTap = 0;

    el.onpointerdown = e => {
      const i = Number(el.dataset.i);
      drag = {
        i,
        pointerId: e.pointerId,
        startX: e.clientX,
        startY: e.clientY,
        originX: displays[i].x,
        originY: displays[i].y,
        moved: false,
        left: Number.parseFloat(el.style.left) || 0,
        top: Number.parseFloat(el.style.top) || 0
      };
      el.setPointerCapture(e.pointerId);
      el.classList.add('dragging');
    };

    el.onpointermove = e => {
      if (!drag || e.pointerId !== drag.pointerId) return;
      const screenDx = e.clientX - drag.startX;
      const screenDy = e.clientY - drag.startY;
      const dx = screenDx / scale;
      const dy = screenDy / scale;
      if (Math.abs(screenDx) + Math.abs(screenDy) > 3) drag.moved = true;

      const next = snap(drag.i, drag.originX + dx, drag.originY + dy);
      displays[drag.i].x = next[0];
      displays[drag.i].y = next[1];
      dirty = true;

      el.style.left = (drag.left + (next[0] - drag.originX) * scale) + 'px';
      el.style.top = (drag.top + (next[1] - drag.originY) * scale) + 'px';
    };

    const finish = e => {
      if (!drag || (e && e.pointerId !== drag.pointerId)) return;
      const completed = drag;
      try { if (el.hasPointerCapture(completed.pointerId)) el.releasePointerCapture(completed.pointerId); } catch { }
      drag = null;
      el.classList.remove('dragging');

      if (!completed.moved) {
        const now = Date.now();
        if (now - lastTap < 360) openDisplay(completed.i, el);
        lastTap = now;
      } else {
        if (!window.vmuArrangementConnected(displays)) {
          displays[completed.i].x = completed.originX;
          displays[completed.i].y = completed.originY;
        }
        render();
      }
    };

    el.onpointerup = finish;
    el.onpointercancel = finish;
    el.ondblclick = e => {
      e.preventDefault();
      openDisplay(Number(el.dataset.i), el);
    };
  });
};

const arrangementActions = document.querySelector('.arrangementActions');
if (arrangementActions && !document.getElementById('arrangementCancel')) {
  const cancel = document.createElement('button');
  cancel.id = 'arrangementCancel';
  cancel.type = 'button';
  cancel.textContent = 'Cancel';
  cancel.onclick = () => location.href = '/';
  arrangementActions.appendChild(cancel);
}

// Re-bind the nodes produced by the initial script.
wire();
</script>
""";
}
