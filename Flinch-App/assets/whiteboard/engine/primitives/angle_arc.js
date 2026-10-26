/**
 * Primitive: angle_arc
 * Renders an arc indicating an angle with optional label.
 * Params: { type: "angle_arc", cx: 100, cy: 100, r: 35, start: 0, end: 1.57, label: "θ", color: "#38BDF8" }
 */
window.drawAngleArc = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-angle-arc';

  const cx = params.cx !== undefined ? params.cx : (params.x || 100);
  const cy = params.cy !== undefined ? params.cy : (params.y || 100);
  const r = params.r || params.radius || 32;
  const startAngle = params.start !== undefined ? params.start : 0;
  const endAngle = params.end !== undefined ? params.end : Math.PI / 2;
  const color = window.WhiteboardUtils.resolveColor(params.color, '#38BDF8');

  // Rough.js arc takes (x, y, width, height, startRad, stopRad, closed, options)
  const arcNode = rc.arc(cx, cy, r * 2, r * 2, startAngle, endAngle, false, {
    stroke: color,
    strokeWidth: params.strokeWidth || 2.2,
    roughness: 1.1
  });
  g.appendChild(arcNode);

  if (params.label) {
    const midAngle = (startAngle + endAngle) / 2;
    const lx = cx + (r + 14) * Math.cos(midAngle);
    const ly = cy + (r + 14) * Math.sin(midAngle);

    const txt = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    txt.setAttribute('x', lx);
    txt.setAttribute('y', ly);
    txt.setAttribute('fill', color);
    txt.setAttribute('font-size', '16');
    txt.setAttribute('font-family', "'Kalam', cursive, sans-serif");
    txt.setAttribute('text-anchor', 'middle');
    txt.setAttribute('dominant-baseline', 'central');
    txt.textContent = params.label;
    txt.style.opacity = '0';
    txt.style.transition = `opacity ${durationMs * 0.5}ms ease-in ${durationMs * 0.5}ms`;
    setTimeout(() => { txt.style.opacity = '1'; }, 50);
    g.appendChild(txt);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
