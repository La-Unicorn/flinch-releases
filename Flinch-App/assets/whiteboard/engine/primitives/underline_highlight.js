/**
 * Primitive: underline, highlight
 * Renders hand-drawn underlines (straight or wavy) and translucent marker highlights.
 * Params:
 *  - underline: { type: "underline", x: 100, y: 150, w: 120, wavy: true, color: "#FACC15" }
 *  - highlight: { type: "highlight", x: 100, y: 140, w: 120, h: 32, color: "#FACC15" }
 */
window.drawUnderlineHighlight = function (container, rc, params, durationMs = 800) {
  const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  g.className = 'wb-primitive wb-underline-highlight';

  const x = params.x !== undefined ? params.x : (params.x1 !== undefined ? params.x1 : 50);
  const y = params.y !== undefined ? params.y : (params.y1 !== undefined ? params.y1 : 100);
  const w = params.w !== undefined ? params.w : (params.width || (params.x2 ? params.x2 - x : 100));
  const isHighlight = params.type === 'highlight';

  if (isHighlight) {
    const h = params.h || params.height || 34;
    const color = window.WhiteboardUtils.resolveColor(params.color, '#FACC15');

    // Translucent smooth marker stroke with soft blend
    const marker = rc.rectangle(x - 4, y - 4, w + 8, h + 8, {
      stroke: 'none',
      fill: color,
      fillStyle: 'solid',
      roughness: 1.1
    });
    marker.style.opacity = '0.25';
    marker.style.mixBlendMode = 'screen';
    g.appendChild(marker);

    // Left-to-right wipe for the highlight
    g.style.clipPath = 'polygon(0 0, 0 0, 0 100%, 0 100%)';
    g.style.transition = `clip-path ${durationMs}ms cubic-bezier(0.25, 1, 0.5, 1)`;
    container.appendChild(g);
    setTimeout(() => {
      g.style.clipPath = 'polygon(0 0, 100% 0, 100% 100%, 0 100%)';
    }, 40);
    return g;
  } else {
    // Underline
    const color = window.WhiteboardUtils.resolveColor(params.color, '#FACC15');
    const isWavy = params.wavy === true || params.style === 'wavy';

    if (isWavy) {
      const step = 14;
      const pts = [];
      for (let curX = x; curX <= x + w; curX += step) {
        const waveY = (pts.length % 2 === 0) ? y - 3 : y + 3;
        pts.push([curX, waveY]);
      }
      const wavyNode = rc.curve(pts, {
        stroke: color,
        strokeWidth: params.strokeWidth || 2.4,
        roughness: 1.2
      });
      g.appendChild(wavyNode);
    } else {
      const lineNode = rc.line(x, y, x + w, y, {
        stroke: color,
        strokeWidth: params.strokeWidth || 2.6,
        roughness: 1.3
      });
      g.appendChild(lineNode);
    }

    container.appendChild(g);
    window.WhiteboardUtils.animateGroup(g, durationMs);
    return g;
  }
};
