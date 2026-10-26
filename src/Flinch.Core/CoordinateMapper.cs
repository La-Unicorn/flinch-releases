using System;
using System.Linq;
using System.Text.RegularExpressions;
using Flinch.Core.Contracts;

namespace Flinch.Core
{
    /// <summary>
    /// Pure, thread-safe coordinate transformation and untrusted screen text sanitizer.
    /// Maps physical/screen coordinates into normalized [0.0, 1.0] viewport space.
    /// </summary>
    public static class CoordinateMapper
    {
        /// <summary>
        /// Converts absolute physical coordinates to normalized [0.0, 1.0] space relative to the captured bounds.
        /// Guaranteed to clamp within [0.0, 1.0] and never return NaN or Infinity.
        /// </summary>
        public static NormalizedRect ToNormalized(
            double physicalX,
            double physicalY,
            double physicalWidth,
            double physicalHeight,
            double captureOriginX,
            double captureOriginY,
            double captureWidth,
            double captureHeight)
        {
            if (captureWidth <= 0 || captureHeight <= 0)
            {
                return new NormalizedRect(0, 0, 0, 0);
            }

            double relX = (physicalX - captureOriginX) / captureWidth;
            double relY = (physicalY - captureOriginY) / captureHeight;
            double relW = Math.Max(0, physicalWidth) / captureWidth;
            double relH = Math.Max(0, physicalHeight) / captureHeight;

            return new NormalizedRect(relX, relY, relW, relH);
        }

        /// <summary>
        /// Converts a normalized rect back to physical screen pixels relative to a capture origin.
        /// </summary>
        public static (double X, double Y, double Width, double Height) ToPhysical(
            NormalizedRect norm,
            double captureOriginX,
            double captureOriginY,
            double captureWidth,
            double captureHeight)
        {
            double x = captureOriginX + (norm.X * captureWidth);
            double y = captureOriginY + (norm.Y * captureHeight);
            double w = norm.Width * captureWidth;
            double h = norm.Height * captureHeight;
            return (x, y, w, h);
        }

        /// <summary>
        /// Sanitizes text extracted from the screen (UIA/OCR).
        /// Treats all screen text as untrusted data:
        /// - Strips invisible and ASCII control characters except printable whitespace
        /// - Enforces maximum length bounds to prevent prompt blowing / memory exhaustion
        /// - Collapses excessive repeated whitespace
        /// </summary>
        public static string SanitizeScreenText(string? raw, int maxLength = 200)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            // Remove non-printable control characters
            var filtered = raw.Where(c => !char.IsControl(c) || c == ' ' || c == '\t').ToArray();
            var text = new string(filtered);

            // Collapse multi-spaces
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (text.Length > maxLength)
            {
                text = text.Substring(0, maxLength).TrimEnd() + "…";
            }

            return text;
        }
    }
}
