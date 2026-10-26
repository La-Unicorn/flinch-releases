/**
 * Primitive: triangle, right_angle_marker
 * Params:
 *  - triangle: { type: "triangle", points: [[x1,y1],[x2,y2],[x3,y3]], color: "#FACC15" }
 *  - right_angle_marker: { type: "right_angle_marker", x: 100, y: 100, size: 20, dirX: 1, dirY: -1, color: "#38BDF8" }
 */
window.drawTriangle = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-triangle';

  const color = window.WhiteboardUtils.resolveColor(params.color, '#FACC15');
  const strokeWidth = params.strokeWidth || 2.4;
  const options = {
    stroke: color,
    strokeWidth: strokeWidth,
    roughness: 1.1
  };

  if (params.type === 'right_angle_marker') {
    const x = params.x !== undefined ? params.x : 100;
    const y = params.y !== undefined ? params.y : 100;
    const s = params.size || 20;
    const dirX = params.dirX !== undefined ? params.dirX : 1;
    const dirY = params.dirY !== undefined ? params.dirY : -1;

    // L-shaped square corner mark
    const p1 = [x + dirX * s, y];
    const p2 = [x + dirX * s, y + dirY * s];
    const p3 = [x, y + dirY * s];

    const marker = rc.linearPath([p1, p2, p3], options);
    g.appendChild(marker);
  } else {
    // triangle
    let pts = params.points;
    if (!pts || pts.length < 3) {
      const x = params.x || 100;
      const y = params.y || 100;
      const w = params.w || 250;
      const h = params.h || 200;
      pts = [[x, y + h], [x + w, y + h], [x, y]]; // Default right triangle
    }

    const triNode = rc.polygon(pts, options);
    g.appendChild(triNode);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
