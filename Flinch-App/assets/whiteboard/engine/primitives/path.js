/**
 * Primitive: path
 * Renders arbitrary curves, graphs, and point arrays with animated stroke drawing.
 * Params: { type: "path", points: [[0, 200], [100, 150], [200, 50]], color: "#3DDC84", strokeWidth: 2.5 }
 */
window.drawPath = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-path';

  const color = window.WhiteboardUtils.resolveColor(params.color, '#3DDC84');
  const strokeWidth = params.strokeWidth || 2.5;

  if (Array.isArray(params.points) && params.points.length >= 2) {
    const rawPts = params.points.map(pt => Array.isArray(pt) ? pt : [pt.x, pt.y]);
    const curveNode = rc.curve(rawPts, {
      stroke: color,
      strokeWidth: strokeWidth,
      roughness: params.roughness || 1.1
    });
    g.appendChild(curveNode);
  } else if (params.d) {
    const pathNode = rc.path(params.d, {
      stroke: color,
      strokeWidth: strokeWidth,
      roughness: params.roughness || 1.1
    });
    g.appendChild(pathNode);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
