/**
 * Primitive: line, arrow
 * Renders hand-drawn lines and arrows with stroke-dashoffset animation.
 * Params: { type: "line" | "arrow", x1: 100, y1: 100, x2: 300, y2: 200, curve: 0, color: "#3DDC84", strokeWidth: 2.5 }
 */
window.drawLineArrow = function (container, rc, params, durationMs = 800) {
  const x1 = params.x1 !== undefined ? params.x1 : 0;
  const y1 = params.y1 !== undefined ? params.y1 : 0;
  const x2 = params.x2 !== undefined ? params.x2 : 100;
  const y2 = params.y2 !== undefined ? params.y2 : 100;
  const color = window.WhiteboardUtils.resolveColor(params.color, '#3DDC84');
  const strokeWidth = params.strokeWidth || 2.5;
  const isArrow = params.type === 'arrow';

  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-line-arrow';

  const roughness = params.roughness !== undefined ? params.roughness : 1.2;

  // Main line / curve
  if (params.curve) {
    const midX = (x1 + x2) / 2 - (y2 - y1) * (params.curve || 0.2);
    const midY = (y1 + y2) / 2 + (x2 - x1) * (params.curve || 0.2);
    const curveNode = rc.curve([[x1, y1], [midX, midY], [x2, y2]], {
      stroke: color,
      strokeWidth: strokeWidth,
      roughness: roughness
    });
    g.appendChild(curveNode);
  } else {
    const lineNode = rc.line(x1, y1, x2, y2, {
      stroke: color,
      strokeWidth: strokeWidth,
      roughness: roughness
    });
    g.appendChild(lineNode);
  }

  // Arrowhead if requested
  if (isArrow) {
    const angle = Math.atan2(y2 - y1, x2 - x1);
    const headLen = 14;
    const a1 = angle - Math.PI / 6;
    const a2 = angle + Math.PI / 6;

    const hx1 = x2 - headLen * Math.cos(a1);
    const hy1 = y2 - headLen * Math.sin(a1);
    const hx2 = x2 - headLen * Math.cos(a2);
    const hy2 = y2 - headLen * Math.sin(a2);

    const headNode = rc.polygon([[x2, y2], [hx1, hy1], [hx2, hy2]], {
      stroke: color,
      fill: color,
      fillStyle: 'solid',
      strokeWidth: strokeWidth * 0.8,
      roughness: 0.8
    });
    g.appendChild(headNode);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
