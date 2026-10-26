/**
 * Primitive: point
 * Renders a marked coordinate point with a hand-drawn dot and label.
 * Params: { type: "point", x: 250, y: 180, label: "P(2, 3)", color: "#F472B6", r: 5 }
 */
window.drawPoint = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-point';

  const x = params.x !== undefined ? params.x : 100;
  const y = params.y !== undefined ? params.y : 100;
  const r = params.r || 5;
  const color = window.WhiteboardUtils.resolveColor(params.color, '#F472B6');

  // Solid point dot
  const dot = rc.circle(x, y, r * 2, {
    stroke: color,
    fill: color,
    fillStyle: 'solid',
    roughness: 0.8
  });
  g.appendChild(dot);

  if (params.label) {
    const txt = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    txt.setAttribute('x', x + r + 8);
    txt.setAttribute('y', y - 6);
    txt.setAttribute('fill', color);
    txt.setAttribute('font-size', '16');
    txt.setAttribute('font-family', "'Kalam', cursive, sans-serif");
    txt.textContent = params.label;
    txt.style.opacity = '0';
    txt.style.transition = `opacity ${durationMs * 0.4}ms ease-in ${durationMs * 0.4}ms`;
    setTimeout(() => { txt.style.opacity = '1'; }, 40);
    g.appendChild(txt);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
