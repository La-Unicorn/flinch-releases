using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Flinch.Api.Middleware;
using Flinch.Api.Services;
using Flinch.Core.Contracts;

namespace Flinch.Core.Tests
{
    [TestClass]
    public class ApiProviderTests
    {
        [TestMethod]
        public async Task FakeAiProvider_MathQuery_YieldsOrderedSegmentsAndOverlayCues()
        {
            var provider = new FakeAiProvider();
            var request = new AnswerRequest
            {
                Query = "Please solve 2x + 5 = 15 step by step."
            };

            var chunks = new List<AnswerStreamChunk>();
            await foreach (var chunk in provider.GenerateAnswerStreamAsync(request, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            Assert.IsTrue(chunks.Count >= 5);
            Assert.AreEqual(StreamChunkType.PlanStarted, chunks[0].Type);
            Assert.AreEqual(StreamChunkType.PlanCompleted, chunks.Last().Type);

            // Verify visual cues exist in the stream
            var shapes = chunks.Where(c => c.Type == StreamChunkType.OverlayShape).Select(c => c.Shape).ToList();
            Assert.IsTrue(shapes.Count >= 3);
            Assert.IsTrue(shapes.Any(s => s?.ShapeType == OverlayShapeType.Highlight));
            Assert.IsTrue(shapes.Any(s => s?.ShapeType == OverlayShapeType.Arrow));
            Assert.IsTrue(shapes.Any(s => s?.ShapeType == OverlayShapeType.Equation));
        }

        [TestMethod]
        public async Task FakeAiProvider_ScreenContext_YieldsScreenWindowOutlineAndCallout()
        {
            var provider = new FakeAiProvider();
            var screen = new ScreenContext
            {
                CaptureId = "cap-123",
                TargetTitle = "Calculator App",
                CaptureBounds = new NormalizedRect(0, 0, 1, 1),
                UiaItems = new List<UiaItemContext>
                {
                    new()
                    {
                        Name = "Equals Button",
                        ControlType = "Button",
                        Bounds = new NormalizedRect(0.8, 0.8, 0.15, 0.10)
                    }
                }
            };

            var request = new AnswerRequest
            {
                Query = "What does this button do?",
                Screen = screen
            };

            var chunks = new List<AnswerStreamChunk>();
            await foreach (var chunk in provider.GenerateAnswerStreamAsync(request, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            var shapes = chunks.Where(c => c.Type == StreamChunkType.OverlayShape).Select(c => c.Shape).ToList();
            Assert.IsTrue(shapes.Any(s => s?.ShapeType == OverlayShapeType.Outline));
            Assert.IsTrue(shapes.Any(s => s?.ShapeType == OverlayShapeType.Callout && s.Label!.Contains("Button")));
        }

        [TestMethod]
        public async Task FakeAiProvider_Cancellation_AbortsPromptly()
        {
            var provider = new FakeAiProvider();
            var request = new AnswerRequest { Query = "Solve this equation" };

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancelled token

            bool wasCancelled = false;
            try
            {
                await foreach (var _ in provider.GenerateAnswerStreamAsync(request, cts.Token))
                {
                }
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }

            Assert.IsTrue(wasCancelled);
        }

        [TestMethod]
        public void ValidationHelper_ClampsCoordinatesAndEnforcesHexColors()
        {
            int shapeCount = 0;
            var chunk = new AnswerStreamChunk
            {
                Type = StreamChunkType.OverlayShape,
                Shape = new OverlayShape
                {
                    CueId = "cue-test",
                    ShapeType = OverlayShapeType.Highlight,
                    Rect = new NormalizedRect(-0.5, 1.5, 2.0, 3.0),
                    From = new NormalizedPoint(-1.0, 2.0),
                    To = new NormalizedPoint(0.5, 0.5),
                    ColorHex = "invalid_not_hex",
                    Label = "Test Label \u0000 with control chars"
                }
            };

            var validated = ValidationHelper.ValidateAndSanitizeChunk(chunk, ref shapeCount);

            Assert.IsNotNull(validated.Shape);
            Assert.AreEqual(1, shapeCount);
            // Verify clamped coordinates
            Assert.IsTrue(validated.Shape.Rect.X >= 0.0 && validated.Shape.Rect.X <= 1.0);
            Assert.IsTrue(validated.Shape.Rect.Y >= 0.0 && validated.Shape.Rect.Y <= 1.0);
            // Verify color fallback
            Assert.AreEqual("#3DDC84", validated.Shape.ColorHex);
            // Verify label sanitized
            Assert.IsFalse(validated.Shape!.Label!.Contains('\u0000'));
        }

        [TestMethod]
        public void ValidationHelper_CapsMaxShapesAt16()
        {
            int shapeCount = 16; // Already at maximum
            var chunk = new AnswerStreamChunk
            {
                Type = StreamChunkType.OverlayShape,
                Shape = new OverlayShape
                {
                    CueId = "shape-17",
                    ShapeType = OverlayShapeType.Highlight
                }
            };

            var validated = ValidationHelper.ValidateAndSanitizeChunk(chunk, ref shapeCount);
            Assert.IsNull(validated.Shape);
        }
    }
}
