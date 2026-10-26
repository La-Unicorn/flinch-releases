/**
 * Primitive: axes, number_line
 * Renders coordinate axes and number lines with tick marks.
 * Params:
 *  - axes: { type: "axes", originX: 200, originY: 200, lengthX: 180, lengthY: 150, step: 30, color: "#94A3B8" }
 *  - number_line: { type: "number_line", x: 100, y: 300, length: 400, min: -3, max: 3, step: 1, color: "#38BDF8" }
 */
window.drawAxesNumberLine = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-axes';

  const color = window.WhiteboardUtils.resolveColor(params.color, '#94A3B8');
  const strokeWidth = params.strokeWidth || 2.0;
  const isNumberLine = params.type === 'number_line';

  if (isNumberLine) {
    const x = params.x || 100;
    const y = params.y || 250;
    const len = params.length || 400;
    const minVal = params.min !== undefined ? params.min : 0;
    const maxVal = params.max !== undefined ? params.max : 5;
    const count = Math.max(1, maxVal - minVal);
    const stepPx = len / count;

    // Main line with arrows on both ends
    g.appendChild(rc.line(x - 15, y, x + len + 15, y, { stroke: color, strokeWidth, roughness: 1.1 }));
    // Left arrow
    g.appendChild(rc.line(x - 15, y, x - 5, y - 6, { stroke: color, strokeWidth, roughness: 0.9 }));
    g.appendChild(rc.line(x - 15, y, x - 5, y + 6, { stroke: color, strokeWidth, roughness: 0.9 }));
    // Right arrow
    g.appendChild(rc.line(x + len + 15, y, x + len + 5, y - 6, { stroke: color, strokeWidth, roughness: 0.9 }));
    g.appendChild(rc.line(x + len + 15, y, x + len + 5, y + 6, { stroke: color, strokeWidth, roughness: 0.9 }));

    // Ticks & labels
    for (let i = 0; i <= count; i++) {
      const tx = x + i * stepPx;
      g.appendChild(rc.line(tx, y - 8, tx, y + 8, { stroke: color, strokeWidth: strokeWidth * 0.9, roughness: 0.8 }));

      const val = minVal + i;
      const numTxt = document.createElementNS('http://www.w3.org/2000/svg', 'text');
      numTxt.setAttribute('x', tx);
      numTxt.setAttribute('y', y + 26);
      numTxt.setAttribute('fill', color);
      numTxt.setAttribute('font-size', '14');
      numTxt.setAttribute('font-family', "'Kalam', monospace, sans-serif");
      numTxt.setAttribute('text-anchor', 'middle');
      numTxt.textContent = String(val);
      g.appendChild(numTxt);
    }
  } else {
    // Coordinate axes
    const ox = params.originX !== undefined ? params.originX : (params.x || 200);
    const oy = params.originY !== undefined ? params.originY : (params.y || 200);
    const lx = params.lengthX || params.w || 220;
    const ly = params.lengthY || params.h || 180;

    // X Axis
    g.appendChild(rc.line(ox - 20, oy, ox + lx, oy, { stroke: color, strokeWidth, roughness: 1.1 }));
    g.appendChild(rc.line(ox + lx, oy, ox + lx - 10, oy - 6, { stroke: color, strokeWidth, roughness: 0.9 }));
    g.appendChild(rc.line(ox + lx, oy, ox + lx - 10, oy + 6, { stroke: color, strokeWidth, roughness: 0.9 }));

    // Y Axis
    g.appendChild(rc.line(ox, oy + 20, ox, oy - ly, { stroke: color, strokeWidth, roughness: 1.1 }));
    g.appendChild(rc.line(ox, oy - ly, ox - 6, oy - ly + 10, { stroke: color, strokeWidth, roughness: 0.9 }));
    g.appendChild(rc.line(ox, oy - ly, ox + 6, oy - ly + 10, { stroke: color, strokeWidth, roughness: 0.9 }));

    // Labels: x and y
    const xLabel = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    xLabel.setAttribute('x', ox + lx + 12);
    xLabel.setAttribute('y', oy + 4);
    xLabel.setAttribute('fill', color);
    xLabel.setAttribute('font-size', '16');
    xLabel.setAttribute('font-family', "'Kalam', cursive, sans-serif");
    xLabel.textContent = 'x';
    g.appendChild(xLabel);

    const yLabel = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    yLabel.setAttribute('x', ox);
    yLabel.setAttribute('y', oy - ly - 12);
    yLabel.setAttribute('fill', color);
    yLabel.setAttribute('font-size', '16');
    yLabel.setAttribute('font-family', "'Kalam', cursive, sans-serif");
    yLabel.setAttribute('text-anchor', 'middle');
    yLabel.textContent = 'y';
    g.appendChild(yLabel);
  }

  container.appendChild(g);
  window.WhiteboardUtils.animateGroup(g, durationMs);
  return g;
};
