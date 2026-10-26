using System;
using System.Text.RegularExpressions;
using Flinch.Api.Models;
using Flinch.Core;
using Flinch.Core.Contracts;

namespace Flinch.Api.Middleware
{
    public static class ValidationHelper
    {
        private static readonly Regex HexColorRegex = new(@"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

        public static AnswerStreamChunk ValidateAndSanitizeChunk(AnswerStreamChunk chunk, ref int currentShapeCount)
        {
            if (chunk.Segment != null)
            {
                chunk.Segment.VisibleText = CoordinateMapper.SanitizeScreenText(chunk.Segment.VisibleText, 2500);
                chunk.Segment.SpokenText = CoordinateMapper.SanitizeScreenText(chunk.Segment.SpokenText, 1500);
            }

            if (chunk.Shape != null)
            {
                if (currentShapeCount >= 16)
                {
                    // Exceeded max shape budget (16 shapes max)
                    chunk.Shape = null;
                    return chunk;
                }
                currentShapeCount++;

                // Validate & clamp normalized rect
                var r = chunk.Shape.Rect;
                chunk.Shape.Rect = new NormalizedRect(r.X, r.Y, r.Width, r.Height);

                // Validate points if present
                if (chunk.Shape.From.HasValue)
                {
                    var f = chunk.Shape.From.Value;
                    chunk.Shape.From = new NormalizedPoint(f.X, f.Y);
                }
                if (chunk.Shape.To.HasValue)
                {
                    var t = chunk.Shape.To.Value;
                    chunk.Shape.To = new NormalizedPoint(t.X, t.Y);
                }

                // Sanitize labels
                chunk.Shape.Label = CoordinateMapper.SanitizeScreenText(chunk.Shape.Label, 100);
                chunk.Shape.Latex = CoordinateMapper.SanitizeScreenText(chunk.Shape.Latex, 200);

                // Validate color
                if (string.IsNullOrWhiteSpace(chunk.Shape.ColorHex) || !HexColorRegex.IsMatch(chunk.Shape.ColorHex))
                {
                    chunk.Shape.ColorHex = "#3DDC84"; // Safe fallback green
                }
            }

            return chunk;
        }
    }
}
