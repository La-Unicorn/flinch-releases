/**
 * Primitive: text
 * Renders LaTeX via KaTeX or plain handwriting text with a left-to-right wipe reveal.
 * Calibrated vertical alignment: (x, y) is the exact visual center of the text line.
 * Params: { type: "text", text: "\\frac{a}{b}", x: 100, y: 150, size: 24, color: "#FFFFFF" }
 */
window.drawText = function (container, rc, params, durationMs = 800) {
  const x = params.x !== undefined ? params.x : 50;
  const y = params.y !== undefined ? params.y : 50;
  const size = params.size || 22;
  const rawText = String(params.text || params.latex || params.content || '');
  const color = window.WhiteboardUtils.resolveColor(params.color, '#F8FAFC');

  // Exact vertical centering: font ascent is ~72% of font size
  const ascentOffset = size * 0.72;
  const availW = Math.max(120, 990 - x);

  const foreignObject = document.createElementNS('http://www.w3.org/2000/svg', 'foreignObject');
  foreignObject.setAttribute('x', x);
  foreignObject.setAttribute('y', y - ascentOffset);
  foreignObject.setAttribute('width', availW);
  foreignObject.setAttribute('height', size * 2.5);
  foreignObject.style.overflow = 'visible';
  foreignObject.style.pointerEvents = 'none';

  const wrapper = document.createElement('div');
  wrapper.className = 'wb-text-wrapper wb-wipe-reveal';
  wrapper.style.color = color;
  wrapper.style.fontSize = `${size}px`;
  wrapper.style.lineHeight = '1';
  wrapper.style.margin = '0';
  wrapper.style.padding = '0';
  wrapper.style.textAlign = 'left';
  wrapper.style.display = 'inline-block';
  wrapper.style.animationDuration = `${durationMs}ms`;

  const isMath = rawText.includes('\\') || rawText.includes('^') || rawText.includes('_') || rawText.includes('=') || rawText.includes('÷') || rawText.includes('+');

  if (window.katex && isMath) {
    try {
      window.katex.render(rawText, wrapper, {
        throwOnError: false,
        displayMode: params.displayMode === true // default to false (inline) to preserve exact (x, y) anchoring
      });
    } catch (err) {
      wrapper.textContent = rawText;
    }
  } else {
    wrapper.textContent = rawText;
    wrapper.style.fontFamily = "'Kalam', 'Caveat', cursive, sans-serif";
  }

  foreignObject.appendChild(wrapper);
  container.appendChild(foreignObject);

  return foreignObject;
};
