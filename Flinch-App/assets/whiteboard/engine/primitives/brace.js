/**
 * Primitive: brace
 * Renders curly grouping brackets for terms and equations.
 * Params: { type: "brace", x: 100, y: 100, w: 150, orientation: "bottom" | "top" | "left" | "right", label: "terms", color: "#38BDF8" }
 */
window.drawBrace = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-brace';

  const x = params.x !== undefined ? params.x : 100;
  const y = params.y !== undefined ? params.y : 100;
  const w = params.w || params.width || 120;
  const orientation = params.orientation || 'bottom';
  const color = window.WhiteboardUtils.resolveColor(params.color, '#38BDF8');
  const strokeWidth = params.strokeWidth || 2.2;

  let pathD = '';
  let labelX = x + w / 2;
  let labelY = y;

  if (orientation === 'bottom') {
    const h = 14;
    pathD = `M ${x} ${y} Q ${x + w * 0.25} ${y + h * 0.4} ${x + w * 0.45} ${y + h * 0.5} Q ${x + w * 0.5} ${y + h} ${x + w * 0.5} ${y + h} Q ${x + w * 0.5} ${y + h} ${x + w * 0.55} ${y + h * 0.5} Q ${x + w * 0.75} ${y + h * 0.4} ${x + w} ${y}`;
    labelY = y + h + 16;
  } else if (orientation === 'top') {
    const h = 14;
    pathD = `M ${x} ${y} Q ${x + w * 0.25} ${y - h * 0.4} ${x + w * 0.45} ${y - h * 0.5} Q ${x + w * 0.5} ${y - h} ${x + w * 0.5} ${y - h} Q ${x + w * 0.5} ${y - h} ${x + w * 0.55} ${y - h * 0.5} Q ${x + w * 0.75} ${y - h * 0.4} ${x + w} ${y}`;
    labelY = y - h - 10;
  } else {
    // vertical
    const h = params.h || params.height || 100;
    const d = 14;
    pathD = `M ${x} ${y} Q ${x - d * 0.4} ${y + h * 0.25} ${x - d * 0.5} ${y + h * 0.45} Q ${x - d} ${y + h * 0.5} ${x - d} ${y + h * 0.5} Q ${x - d} ${y + h * 0.5} ${x - d * 0.5} ${y + h * 0.55} Q ${x - d * 0.4} ${y + h * 0.75} ${x} ${y + h}`;
    labelX = x - d - 18;
    labelY = y + h / 2;
  }

  const braceNode = rc.path(pathD, {
    stroke: color,
    strokeWidth: strokeWidth,
    roughness: 1.1
  });
  g.appendChild(braceNode);

  if (params.label) {
    const txt = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    txt.setAttribute('x', labelX);
    txt.setAttribute('y', labelY);
    txt.setAttribute('fill', color);
    txt.setAttribute('font-size', '15');
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
