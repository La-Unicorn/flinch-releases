/**
 * Whiteboard Layout Engine
 * Ensures clean math presentation, line stacking, and coordinate normalization.
 * Model coordinates are normalized 0-1000 and mapped into the SVG viewBox.
 */
window.WhiteboardLayout = {
  viewBoxWidth: 1000,
  viewBoxHeight: 700,
  currentY: 60,
  lineHeight: 65,

  reset: function () {
    this.currentY = 60;
  },

  // Map 0-1000 normalized coordinate to SVG viewBox
  mapX: function (val) {
    if (val === undefined || val === null) return 80;
    return Math.max(10, Math.min(990, (val / 1000) * this.viewBoxWidth));
  },

  mapY: function (val) {
    if (val === undefined || val === null) {
      const y = this.currentY;
      this.currentY += this.lineHeight;
      return y;
    }
    return Math.max(10, Math.min(690, (val / 1000) * this.viewBoxHeight));
  },

  // Normalize any primitive's coordinates before rendering
  resolveParams: function (primitive) {
    const copy = Object.assign({}, primitive);

    // Map primary single coordinates
    if (copy.x !== undefined) copy.x = this.mapX(copy.x);
    if (copy.y !== undefined) copy.y = this.mapY(copy.y);
    if (copy.cx !== undefined) copy.cx = this.mapX(copy.cx);
    if (copy.cy !== undefined) copy.cy = this.mapY(copy.cy);
    if (copy.x1 !== undefined) copy.x1 = this.mapX(copy.x1);
    if (copy.y1 !== undefined) copy.y1 = this.mapY(copy.y1);
    if (copy.x2 !== undefined) copy.x2 = this.mapX(copy.x2);
    if (copy.y2 !== undefined) copy.y2 = this.mapY(copy.y2);

    // Map axes & number lines
    if (copy.originX !== undefined) copy.originX = this.mapX(copy.originX);
    if (copy.originY !== undefined) copy.originY = this.mapY(copy.originY);
    if (copy.lengthX !== undefined) copy.lengthX = (copy.lengthX / 1000) * this.viewBoxWidth;
    if (copy.lengthY !== undefined) copy.lengthY = (copy.lengthY / 1000) * this.viewBoxHeight;
    if (copy.length !== undefined && copy.type === 'number_line') {
      copy.length = (copy.length / 1000) * this.viewBoxWidth;
    }

    // Scale widths/heights/radii
    if (copy.w !== undefined) copy.w = (copy.w / 1000) * this.viewBoxWidth;
    if (copy.h !== undefined) copy.h = (copy.h / 1000) * this.viewBoxHeight;
    if (copy.r !== undefined) copy.r = (copy.r / 1000) * this.viewBoxWidth;
    if (copy.radius !== undefined) copy.radius = (copy.radius / 1000) * this.viewBoxWidth;

    // Map points array (triangles, curves, polygons)
    if (Array.isArray(copy.points)) {
      copy.points = copy.points.map(pt => {
        if (Array.isArray(pt)) {
          return [this.mapX(pt[0]), this.mapY(pt[1])];
        } else if (pt && typeof pt === 'object') {
          return {
            x: this.mapX(pt.x),
            y: this.mapY(pt.y)
          };
        }
        return pt;
      });
    }

    return copy;
  }
};
