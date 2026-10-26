/**
 * Whiteboard Step-by-Step Player
 * Coordinates narration timing and drawing animations.
 */
window.WhiteboardPlayer = class {
  constructor(svgElement, options = {}) {
    this.svg = svgElement;
    this.rc = window.rough ? window.rough.svg(svgElement) : null;
    this.steps = [];
    this.currentStepIndex = -1;
    this.isPlaying = false;
    this.stepTimeout = null;
    this.options = Object.assign({
      defaultStepDurationMs: 3200,
      onStepStart: null,
      onStepComplete: null,
      onAllComplete: null,
      onError: null
    }, options);

    this.primitiveMap = {
      'text': window.drawText,
      'line': window.drawLineArrow,
      'arrow': window.drawLineArrow,
      'circle': window.drawCircleRect,
      'rect': window.drawCircleRect,
      'triangle': window.drawTriangle,
      'right_angle_marker': window.drawTriangle,
      'angle_arc': window.drawAngleArc,
      'path': window.drawPath,
      'underline': window.drawUnderlineHighlight,
      'highlight': window.drawUnderlineHighlight,
      'brace': window.drawBrace,
      'point': window.drawPoint,
      'axes': window.drawAxesNumberLine,
      'number_line': window.drawAxesNumberLine,
      'check': window.drawCheckCross,
      'cross': window.drawCheckCross,
      'highlight_on_screen': window.drawScreenHighlight
    };
  }

  // Load and validate JSON payload
  load(payload) {
    this.stop();
    this.clear();

    if (!payload || !Array.isArray(payload.steps) || payload.steps.length === 0) {
      if (this.options.onError) this.options.onError('Invalid payload: steps array required.');
      return false;
    }

    this.steps = payload.steps;
    this.currentStepIndex = -1;
    window.WhiteboardLayout.reset();
    return true;
  }

  clear() {
    while (this.svg.firstChild) {
      this.svg.removeChild(this.svg.firstChild);
    }
    window.WhiteboardLayout.reset();
  }

  play() {
    if (this.steps.length === 0) return;
    this.isPlaying = true;
    if (this.currentStepIndex === -1 || this.currentStepIndex >= this.steps.length - 1) {
      this.clear();
      this.currentStepIndex = -1;
    }
    this.nextStep();
  }

  pause() {
    this.isPlaying = false;
    if (this.stepTimeout) {
      clearTimeout(this.stepTimeout);
      this.stepTimeout = null;
    }
  }

  stop() {
    this.pause();
    this.currentStepIndex = -1;
  }

  nextStep(durationMs = null) {
    if (this.currentStepIndex + 1 >= this.steps.length) {
      this.isPlaying = false;
      if (this.options.onAllComplete) this.options.onAllComplete();
      return;
    }

    this.currentStepIndex++;
    const step = this.steps[this.currentStepIndex];
    const dur = durationMs || step.durationMs || this.options.defaultStepDurationMs;

    if (this.options.onStepStart) {
      this.options.onStepStart(this.currentStepIndex, step, dur);
    }

    this.renderStep(step, dur);

    if (this.isPlaying) {
      this.stepTimeout = setTimeout(() => {
        if (this.options.onStepComplete) {
          this.options.onStepComplete(this.currentStepIndex, step);
        }
        this.nextStep();
      }, dur + 100);
    }
  }

  prevStep() {
    if (this.currentStepIndex <= 0) return;
    const targetIdx = this.currentStepIndex - 1;
    this.pause();
    this.clear();
    this.currentStepIndex = -1;
    for (let i = 0; i <= targetIdx; i++) {
      this.renderStep(this.steps[i], 100);
      this.currentStepIndex = i;
    }
    if (this.options.onStepStart) {
      this.options.onStepStart(targetIdx, this.steps[targetIdx], 0);
    }
  }

  renderStep(step, durationMs) {
    if (!step || !Array.isArray(step.draw)) return;

    const drawCount = step.draw.length;
    const itemDuration = Math.max(250, Math.floor(durationMs / Math.max(1, drawCount)));

    step.draw.forEach((rawPrimitive, idx) => {
      const type = (rawPrimitive.type || '').toLowerCase().trim();
      const handler = this.primitiveMap[type];
      if (!handler) {
        console.warn('Unknown primitive type:', type);
        return;
      }

      const params = window.WhiteboardLayout.resolveParams(rawPrimitive);
      const delay = idx * (itemDuration * 0.45);

      setTimeout(() => {
        handler(this.svg, this.rc, params, itemDuration);
      }, delay);
    });
  }
};
