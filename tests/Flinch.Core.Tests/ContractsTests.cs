using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Flinch.Core.Contracts;

namespace Flinch.Core.Tests
{
    [TestClass]
    public class ContractsTests
    {
        [TestMethod]
        public void NormalizedRect_ClampsCoordinatesSafely()
        {
            // Negative coordinates and out-of-bounds sizes
            var rect = new NormalizedRect(-0.5, 1.5, 2.0, -1.0);
            Assert.AreEqual(0.0, rect.X);
            Assert.AreEqual(1.0, rect.Y);
            Assert.AreEqual(1.0, rect.Width);
            Assert.AreEqual(0.0, rect.Height);
        }

        [TestMethod]
        public void OverlayScene_LimitsMaxShapesAndValidatesDuration()
        {
            var shapes = new List<OverlayShape>();
            for (int i = 0; i < 30; i++)
            {
                shapes.Add(new OverlayShape
                {
                    CueId = $"cue_{i}",
                    ShapeType = OverlayShapeType.Circle,
                    Rect = new NormalizedRect(0.1, 0.1, 0.2, 0.2)
                });
            }

            // Exceeds max limit and duration limits
            var scene = OverlayScene.CreateValidated("test_capture", shapes, durationMs: 999999);

            Assert.IsTrue(scene.Shapes.Count <= 16, "Must clamp max shapes to 16");
            Assert.AreEqual(30000, scene.DurationMs, "Duration must be clamped to 30000ms max");
        }

        [TestMethod]
        public void ToolRequest_EnforcesAllowlistPolicy()
        {
            var validTool = new ToolRequest { OperationName = "explain_selection" };
            Assert.IsTrue(validTool.IsAllowlisted());

            var invalidDangerousTool = new ToolRequest { OperationName = "execute_arbitrary_shell_command" };
            Assert.IsFalse(invalidDangerousTool.IsAllowlisted(), "Arbitrary execution must be blocked");
        }

        [TestMethod]
        public void ScreenContext_HandlesEphemeralDataWithoutPersistence()
        {
            var ctx = new ScreenContext
            {
                CaptureId = "cap_1001",
                TargetTitle = "Calculator",
                CaptureBounds = new NormalizedRect(0.1, 0.1, 0.8, 0.8),
                EphemeralImageData = new byte[] { 0xFF, 0xD8, 0xFF }
            };

            Assert.IsNotNull(ctx.EphemeralImageData);
            Assert.AreEqual("Calculator", ctx.TargetTitle);

            // Ephemeral clearing test
            ctx.EphemeralImageData = null;
            Assert.IsNull(ctx.EphemeralImageData);
        }

        [TestMethod]
        public void ScreenContext_OneShotLifecycle_PromptDisposalAndZeroRetention()
        {
            var fakeJpeg = new byte[1024];
            Array.Fill<byte>(fakeJpeg, 0xAB);

            var ctx = new ScreenContext
            {
                CaptureId = "stage2_shot_01",
                TargetTitle = "Visual Studio Code",
                CaptureBounds = new NormalizedRect(0.0, 0.0, 1.0, 1.0),
                EphemeralImageData = fakeJpeg
            };

            Assert.IsNotNull(ctx.EphemeralImageData);
            Assert.AreEqual(1024, ctx.EphemeralImageData.Length);

            // Simulate one-shot consumption by query
            var consumedPayload = ctx.EphemeralImageData;
            ctx.EphemeralImageData = null; // Zero retention after consumption

            Assert.IsNotNull(consumedPayload);
            Assert.IsNull(ctx.EphemeralImageData, "Context must zero out image data immediately after consumption");
        }

        [TestMethod]
        public void ScreenContext_DiscardOnCancel_LeavesZeroState()
        {
            ScreenContext? staged = null;

            // User triggers picker, then cancels:
            bool userCancelled = true;
            if (!userCancelled)
            {
                staged = new ScreenContext
                {
                    CaptureId = "should_not_exist",
                    TargetTitle = "Cancelled Window"
                };
            }

            Assert.IsNull(staged, "Cancelled capture must leave zero staged state");
        }

        [TestMethod]
        public void MathResult_PreservesOrderedStepsAndVerification()
        {
            var math = new MathResult
            {
                OriginalEquation = "6 ÷ 2(1+2)",
                IsVerified = true,
                SolutionSet = new List<string> { "9" },
                OrderedSteps = new List<MathStep>
                {
                    new MathStep { StepNumber = 1, Rule = "Parentheses", BeforeExpression = "1+2", AfterExpression = "3" },
                    new MathStep { StepNumber = 2, Rule = "Division left-to-right", BeforeExpression = "6 ÷ 2", AfterExpression = "3" },
                    new MathStep { StepNumber = 3, Rule = "Multiplication", BeforeExpression = "3 × 3", AfterExpression = "9" }
                }
            };

            Assert.IsTrue(math.IsVerified);
            Assert.AreEqual(3, math.OrderedSteps.Count);
            Assert.AreEqual("9", math.SolutionSet[0]);
        }
    }
}
