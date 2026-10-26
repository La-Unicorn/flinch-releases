/**
 * Primitive: circle, rect
 * Renders hand-drawn circles and rectangles with Rough.js.
 * Params:
 *  - circle: { type: "circle", x: 200, y: 200, r: 50, color: "#3DDC84", fill: "none" }
 *  - rect: { type: "rect", x: 100, y: 100, w: 200, h: 80, rounded: 8, color: "#38BDF8" }
 */
window.drawCircleRect = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-circle-rect';

  const color = window.WhiteboardUtils.resolveColor(params.color, '#3DDC84');
  const strokeWidth = params.strokeWidth || 2.4;
  const roughness = params.roughness !== undefined ? params.roughness : 1.3;
  const options = {
    stroke: color,
    strokeWidth: strokeWidth,
    roughness: roughness
  };

  if (params.fill && params.fill !== 'none') {
    options.fill = window.WhiteboardUtils.resolveColor(params.fill, color);
    options.fillStyle = params.fillStyle || 'hachure';
  }

  if (params.type === 'circle') {
    const cx = params.cx !== undefined ? params.cx : (params.x !== undefined ? params.x : 100);
    const cy = params.cy !== undefined ? params.cy : (params.y !== undefined ? params.y : 100);
    const diameter = (params.r ? params.r * 2 : (params.radius ? params.radius * 2 : (params.d || 80)));
    const node = rc.circle(cx, cy, diameter, options);
    g.appendChild(node);
  } else {
    // rect
    const x = params.x !== undefined ? params.x : 50;
    const y = params.y !== undefined ? params.y : 50;
    const w = params.w !== undefined ? params.w : (params.width || 120);
    const h = params.h !== undefined ? params.h : (params.height || 60);

    const node = rc.rectangle(x, y, w, h, options);
    g.appendChild(node);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
