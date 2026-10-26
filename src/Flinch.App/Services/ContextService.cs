using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Flinch.Core;
using Flinch.Core.Contracts;

namespace Flinch_App.Services
{
    /// <summary>
    /// Service responsible for gathering bounded screen context (UI Automation + OCR)
    /// for a captured window or display.
    /// Strictly bounds item counts and treats all screen text as untrusted.
    /// </summary>
    public sealed class ContextService
    {
        private const int MaxUiaItems = 40;
        private const int MaxOcrItems = 60;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public async Task<ScreenContext> GatherContextAsync(
            SoftwareBitmap? softwareBitmap,
            string targetTitle,
            int pixelWidth,
            int pixelHeight,
            IntPtr targetHwnd = default)
        {
            var captureId = Guid.NewGuid().ToString("N");
            var sanitizedTitle = CoordinateMapper.SanitizeScreenText(targetTitle, 100);

            var context = new ScreenContext
            {
                CaptureId = captureId,
                TargetTitle = string.IsNullOrEmpty(sanitizedTitle) ? "Active Screen" : sanitizedTitle,
                CaptureBounds = new NormalizedRect(0, 0, 1.0, 1.0),
                CoordinateSpace = new CoordinateMetadata
                {
                    PhysicalWidth = pixelWidth,
                    PhysicalHeight = pixelHeight,
                    OriginX = 0,
                    OriginY = 0,
                    MonitorIndex = 0,
                    Orientation = pixelHeight > pixelWidth ? "Portrait" : "Landscape",
                    DpiScale = ResolveDpiScale(targetHwnd)
                },
                TimestampUtc = DateTime.UtcNow
            };

            // 1. Extract bounded UI Automation items (Window hierarchy & controls)
            try
            {
                var uiaList = ExtractBoundedWindowControls(targetHwnd, pixelWidth, pixelHeight);
                context.UiaItems.AddRange(uiaList);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ContextService] UIA extraction non-fatal warning: {ex.Message}");
            }

            // 2. Extract bounded OCR items via Windows.Media.Ocr
            if (softwareBitmap != null)
            {
                try
                {
                    var ocrList = await ExtractBoundedOcrAsync(softwareBitmap, pixelWidth, pixelHeight);
                    context.OcrItems.AddRange(ocrList);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ContextService] OCR extraction non-fatal warning: {ex.Message}");
                }
            }

            return context;
        }

        private static double ResolveDpiScale(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi > 0)
                    {
                        return Math.Round(dpi / 96.0, 2);
                    }
                }
                catch
                {
                    // Fallback to 1.0 if GetDpiForWindow unavailable
                }
            }
            return 1.0;
        }

        private static List<UiaItemContext> ExtractBoundedWindowControls(IntPtr parentHwnd, int captureWidth, int captureHeight)
        {
            var results = new List<UiaItemContext>();
            if (parentHwnd == IntPtr.Zero || captureWidth <= 0 || captureHeight <= 0)
            {
                return results;
            }

            // Get target window physical bounds
            if (!GetWindowRect(parentHwnd, out var winRect))
            {
                return results;
            }

            int originX = winRect.Left;
            int originY = winRect.Top;

            EnumChildWindows(parentHwnd, (childHwnd, lParam) =>
            {
                if (results.Count >= MaxUiaItems)
                {
                    return false; // Stop enumeration
                }

                if (!GetWindowRect(childHwnd, out var cRect))
                {
                    return true; // Continue
                }

                int width = cRect.Right - cRect.Left;
                int height = cRect.Bottom - cRect.Top;

                // Ignore invisible or zero-area items
                if (width <= 2 || height <= 2)
                {
                    return true;
                }

                var textSb = new StringBuilder(256);
                GetWindowText(childHwnd, textSb, 256);

                var classSb = new StringBuilder(128);
                GetClassName(childHwnd, classSb, 128);

                string rawName = textSb.ToString();
                string rawClass = classSb.ToString();

                string sanitizedName = CoordinateMapper.SanitizeScreenText(rawName, 120);
                string sanitizedType = NormalizeControlTypeName(rawClass);

                var normBounds = CoordinateMapper.ToNormalized(
                    cRect.Left, cRect.Top, width, height,
                    originX, originY, captureWidth, captureHeight);

                results.Add(new UiaItemContext
                {
                    Name = sanitizedName,
                    ControlType = sanitizedType,
                    Bounds = normBounds,
                    Confidence = 1.0
                });

                return true;
            }, IntPtr.Zero);

            return results;
        }

        private static string NormalizeControlTypeName(string className)
        {
            if (string.IsNullOrWhiteSpace(className)) return "Element";
            string lower = className.ToLowerInvariant();
            if (lower.Contains("button")) return "Button";
            if (lower.Contains("edit") || lower.Contains("text")) return "EditBox";
            if (lower.Contains("list")) return "ListBox";
            if (lower.Contains("combo")) return "ComboBox";
            if (lower.Contains("scroll")) return "ScrollBar";
            if (lower.Contains("static")) return "Label";
            return className;
        }

        private static async Task<List<OcrItemContext>> ExtractBoundedOcrAsync(
            SoftwareBitmap bitmap,
            int pixelWidth,
            int pixelHeight)
        {
            var results = new List<OcrItemContext>();

            if (OcrEngine.AvailableRecognizerLanguages.Count == 0)
            {
                return results;
            }

            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);

            if (engine == null)
            {
                return results;
            }

            var ocrResult = await engine.RecognizeAsync(bitmap);
            if (ocrResult == null || ocrResult.Lines == null)
            {
                return results;
            }

            foreach (var line in ocrResult.Lines)
            {
                if (results.Count >= MaxOcrItems) break;

                // Bounded line text extraction
                string lineText = CoordinateMapper.SanitizeScreenText(line.Text, 150);
                if (string.IsNullOrWhiteSpace(lineText)) continue;

                // Find line bounding box from its constituent words or bounding rect
                if (line.Words.Count > 0)
                {
                    double minX = line.Words.Min(w => w.BoundingRect.Left);
                    double minY = line.Words.Min(w => w.BoundingRect.Top);
                    double maxX = line.Words.Max(w => w.BoundingRect.Right);
                    double maxY = line.Words.Max(w => w.BoundingRect.Bottom);

                    var normRect = CoordinateMapper.ToNormalized(
                        minX, minY, maxX - minX, maxY - minY,
                        0, 0, pixelWidth, pixelHeight);

                    results.Add(new OcrItemContext
                    {
                        Text = lineText,
                        Bounds = normRect,
                        Confidence = 0.95
                    });
                }
            }

            return results;
        }
    }
}
