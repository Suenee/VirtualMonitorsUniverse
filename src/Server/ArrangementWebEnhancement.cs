namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Small enhancement kept separate from the main HTML renderer so the arrangement
/// editor's pointer-capture behavior remains easy to audit and iterate on.
/// </summary>
internal static class ArrangementWebEnhancement
{
    public const string Script = """
<script>
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

// Re-bind the nodes produced by the initial script.
wire();
</script>
""";
}
