using System;
using System.Collections.Generic;

namespace Flinch.Core.Contracts
{
    // =========================================================================
    // Contract 1: ScreenContext
    // =========================================================================
    public sealed class ScreenContext : IDisposable
    {
        public required string CaptureId { get; set; }
        public required string TargetTitle { get; set; }
        public NormalizedRect CaptureBounds { get; set; }
        public CoordinateMetadata CoordinateSpace { get; set; } = new();
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public byte[]? EphemeralImageData { get; set; }
        public List<UiaItemContext> UiaItems { get; set; } = new();
        public List<OcrItemContext> OcrItems { get; set; } = new();

        public void Dispose()
        {
            if (EphemeralImageData != null)
            {
                Array.Clear(EphemeralImageData, 0, EphemeralImageData.Length);
                EphemeralImageData = null;
            }
        }
    }

    public struct NormalizedRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public NormalizedRect(double x, double y, double width, double height)
        {
            X = Math.Clamp(x, 0.0, 1.0);
            Y = Math.Clamp(y, 0.0, 1.0);
            Width = Math.Clamp(width, 0.0, 1.0 - X);
            Height = Math.Clamp(height, 0.0, 1.0 - Y);
        }
    }

    public sealed class CoordinateMetadata
    {
        public double DpiScale { get; set; } = 1.0;
        public double PhysicalWidth { get; set; }
        public double PhysicalHeight { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public int MonitorIndex { get; set; }
        public string Orientation { get; set; } = "Landscape";
    }

    public sealed class UiaItemContext
    {
        public string? Name { get; set; }
        public string? ControlType { get; set; }
        public NormalizedRect Bounds { get; set; }
        public double Confidence { get; set; } = 1.0;
    }

    public sealed class OcrItemContext
    {
        public string Text { get; set; } = string.Empty;
        public NormalizedRect Bounds { get; set; }
        public double Confidence { get; set; } = 1.0;
    }

    // =========================================================================
    // Contract 2: ExplanationPlan
    // =========================================================================
    public sealed class ExplanationPlan
    {
        public required string PlanId { get; set; }
        public List<ExplanationSegment> Segments { get; set; } = new();
        public bool IsVerified { get; set; } = true;
    }

    public sealed class ExplanationSegment
    {
        public required string Id { get; set; }
        public required string VisibleText { get; set; }
        public required string SpokenText { get; set; }
        public int? MathStepId { get; set; }
        public List<string> VisualCueIds { get; set; } = new();
    }

    // =========================================================================
    // Contract 3: OverlayScene (Strict schema, clamped bounds, approved shapes)
    // =========================================================================
    public sealed class OverlayScene
    {
        public int Version { get; set; } = 1;
        public required string TargetCaptureId { get; set; }
        public List<OverlayShape> Shapes { get; set; } = new();
        public int DurationMs { get; set; } = 8000;

        public static OverlayScene CreateValidated(string targetId, IEnumerable<OverlayShape> shapes, int durationMs = 8000)
        {
            var scene = new OverlayScene
            {
                TargetCaptureId = targetId,
                DurationMs = Math.Clamp(durationMs, 1000, 30000)
            };

            int count = 0;
            foreach (var s in shapes)
            {
                if (count++ >= 16) break; // Hard limit 16 shapes
                scene.Shapes.Add(s);
            }
            return scene;
        }
    }

    public enum OverlayShapeType
    {
        Outline,
        Highlight,
        Arrow,
        Callout,
        Equation,
        Circle
    }

    public sealed class OverlayShape
    {
        public required string CueId { get; set; }
        public OverlayShapeType ShapeType { get; set; }
        public NormalizedRect Rect { get; set; }
        public NormalizedPoint? From { get; set; }
        public NormalizedPoint? To { get; set; }
        public string? Label { get; set; }
        public string? Latex { get; set; }
        public string ColorHex { get; set; } = "#3DDC84";
        public int StepIndex { get; set; } = 1;
        public int DelayMs { get; set; } = 0;
    }

    public struct NormalizedPoint
    {
        public double X { get; set; }
        public double Y { get; set; }

        public NormalizedPoint(double x, double y)
        {
            X = Math.Clamp(x, 0.0, 1.0);
            Y = Math.Clamp(y, 0.0, 1.0);
        }
    }

    // =========================================================================
    // Contract 4: MathResult
    // =========================================================================
    public sealed class MathResult
    {
        public required string OriginalEquation { get; set; }
        public string? ParsedAstJson { get; set; }
        public string? DomainAssumptions { get; set; }
        public List<string> SolutionSet { get; set; } = new();
        public List<MathStep> OrderedSteps { get; set; } = new();
        public bool IsVerified { get; set; }
        public string? VerificationResidual { get; set; }
    }

    public sealed class MathSolveRequest
    {
        public string Expression { get; set; } = string.Empty;
    }

    public sealed class ExaSearchRequest
    {
        public string Query { get; set; } = string.Empty;
        public int NumResults { get; set; } = 3;
    }

    public sealed class MathStep
    {
        public int StepNumber { get; set; }
        public required string Rule { get; set; }
        public required string BeforeExpression { get; set; }
        public required string AfterExpression { get; set; }
        public string? Condition { get; set; }
    }

    // =========================================================================
    // Contract 5: ToolRequest (Policy & allowlisted operations)
    // =========================================================================
    public sealed class ToolRequest
    {
        public required string OperationName { get; set; }
        public Dictionary<string, string> Arguments { get; set; } = new();
        public bool RequiresConfirmation { get; set; } = true;
        public string PermissionScope { get; set; } = "ReadOnly";

        private static readonly HashSet<string> AllowlistedOperations = new(StringComparer.OrdinalIgnoreCase)
        {
            "capture_target",
            "explain_selection",
            "verify_math",
            "draw_overlay",
            "dismiss_overlay"
        };

        public bool IsAllowlisted() => AllowlistedOperations.Contains(OperationName);
    }

    // =========================================================================
    // Contract 6: Streaming Answer API Contracts & Multi-Turn History
    // =========================================================================
    public sealed class ChatMessageContext
    {
        public string Role { get; set; } = "user"; // "user", "assistant"
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public sealed class AnswerRequest
    {
        public required string Query { get; set; }
        public ScreenContext? Screen { get; set; }
        public bool ExplicitScreenRequested { get; set; } = false;
        public List<ChatMessageContext> ConversationHistory { get; set; } = new();
        public bool Stream { get; set; } = true;
    }

    public enum StreamChunkType
    {
        PlanStarted,
        Segment,
        OverlayShape,
        PlanCompleted,
        Error
    }

    public sealed class AnswerStreamChunk
    {
        public StreamChunkType Type { get; set; }
        public string? PlanId { get; set; }
        public ExplanationSegment? Segment { get; set; }
        public OverlayShape? Shape { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsVerified { get; set; } = true;
    }

    // =========================================================================
    // Contract 7: Timeline & Playback Clock Synchronization
    // =========================================================================
    public static class TimelineClock
    {
        public const long TicksPerMillisecond = 10_000; // 100-ns ticks to ms (10,000 ticks = 1 ms)

        public static double TicksToMilliseconds(long ticks100Ns)
        {
            return (double)ticks100Ns / TicksPerMillisecond;
        }

        public static long MillisecondsToTicks(double milliseconds)
        {
            return (long)(milliseconds * TicksPerMillisecond);
        }
    }

    public sealed class TimedVisualCue
    {
        public required string CueId { get; set; }
        public required OverlayShape Shape { get; set; }
        public double StartOffsetMs { get; set; }
        public double DurationMs { get; set; } = 4000;
        public bool HasTriggered { get; set; }
    }
}
