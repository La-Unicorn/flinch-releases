/**
 * Primitive: highlight_on_screen
 * Bounding box (0-1000 normalized relative to the screenshot) indicating where on the user's real screen the problem is.
 * Dispatches an event to the host C# window or screen overlay window.
 * Params: { type: "highlight_on_screen", x: 250, y: 180, w: 500, h: 220, label: "Problem Location" }
 */
window.drawScreenHighlight = function (container, rc, params, durationMs = 800) {
  // Normalize 0-1000 coordinate bounds to 0.0 - 1.0 for the host system
  const normBbox = {
    x: (params.x || 0) / 1000.0,
    y: (params.y || 0) / 1000.0,
    w: (params.w || 100) / 1000.0,
    h: (params.h || 50) / 1000.0,
    label: params.label || 'Problem Location'
  };

  // Dispatch custom event for C# / host environment
  window.dispatchEvent(new CustomEvent('flinch-screen-highlight', { detail: normBbox }));

  // Notify C# via WebView2 or window.chrome.webview or window.external if available
  try {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({
        action: 'highlight_on_screen',
        bbox: normBbox
      });
    } else if (window.external && window.external.OnScreenHighlight) {
      window.external.OnScreenHighlight(JSON.stringify(normBbox));
    }
  } catch (e) {
    // ignore
  }

  return null;
};
