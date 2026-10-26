/**
 * Helper utilities for whiteboard stroke animations and geometry
 */
window.WhiteboardUtils = {
  // Animate SVG path with stroke-dashoffset
  animatePath: function (pathElement, durationMs) {
    if (!pathElement) return;
    try {
      const len = pathElement.getTotalLength ? pathElement.getTotalLength() : 100;
      pathElement.style.strokeDasharray = len + ' ' + len;
      pathElement.style.strokeDashoffset = len;
      pathElement.style.transition = 'none';

      // Force reflow
      pathElement.getBoundingClientRect();

      pathElement.style.transition = `stroke-dashoffset ${durationMs}ms cubic-bezier(0.25, 1, 0.5, 1)`;
      pathElement.style.strokeDashoffset = '0';
    } catch (e) {
      // Fallback
      pathElement.style.opacity = '0';
      pathElement.style.transition = `opacity ${durationMs}ms ease-out`;
      pathElement.style.opacity = '1';
    }
  },

  // Animate a group of paths sequentially or simultaneously
  animateGroup: function (svgGroup, durationMs) {
    const paths = svgGroup.querySelectorAll('path, line, circle, rect, polygon, polyline');
    paths.forEach(p => {
      WhiteboardUtils.animatePath(p, durationMs);
    });
  },

  // Color normalization with fallback palette
  resolveColor: function (color, defaultColor = '#3DDC84') {
    if (!color) return defaultColor;
    const c = String(color).toLowerCase().trim();
    if (c === 'green' || c === 'mint') return '#3DDC84';
    if (c === 'cyan' || c === 'blue') return '#38BDF8';
    if (c === 'yellow' || c === 'gold') return '#FACC15';
    if (c === 'pink' || c === 'red') return '#F472B6';
    if (c === 'purple') return '#C084FC';
    if (c === 'white') return '#F8FAFC';
    return color;
  }
};
