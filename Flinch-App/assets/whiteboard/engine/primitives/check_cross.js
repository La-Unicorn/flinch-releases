/**
 * Primitive: check, cross
 * Renders hand-drawn checkmarks and crossmarks with animated stroke drawing.
 * Params: { type: "check" | "cross", x: 100, y: 100, size: 30, color: "#3DDC84" }
 */
window.drawCheckCross = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-check-cross';

  const x = params.x !== undefined ? params.x : 100;
  const y = params.y !== undefined ? params.y : 100;
  const s = params.size || 28;
  const isCheck = params.type === 'check';
  const color = window.WhiteboardUtils.resolveColor(params.color, isCheck ? '#3DDC84' : '#F472B6');
  const strokeWidth = params.strokeWidth || 3.2;

  if (isCheck) {
    // Checkmark: short leg down-right, long leg up-right
    const p1 = [x, y + s * 0.5];
    const p2 = [x + s * 0.35, y + s];
    const p3 = [x + s, y];
    const checkNode = rc.linearPath([p1, p2, p3], {
      stroke: color,
      strokeWidth: strokeWidth,
      roughness: 1.3
    });
    g.appendChild(checkNode);
  } else {
    // Cross: two intersecting lines
    const l1 = rc.line(x, y, x + s, y + s, { stroke: color, strokeWidth, roughness: 1.3 });
    const l2 = rc.line(x + s, y, x, y + s, { stroke: color, strokeWidth, roughness: 1.3 });
    g.appendChild(l1);
    g.appendChild(l2);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
