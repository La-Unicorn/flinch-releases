using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Flinch.Core;
using Flinch.Core.Contracts;

namespace Flinch.Core.Tests
{
    [TestClass]
    public class OverlaySceneTests
    {
        [TestMethod]
        public void OverlayScene_DurationClamping_EnforcesSafeBounds()
        {
            var shapes = new List<OverlayShape>
            {
                new() { CueId = "c1", ShapeType = OverlayShapeType.Outline, Rect = new NormalizedRect(0.1, 0.1, 0.5, 0.5) }
            };

            var sceneTooShort = OverlayScene.CreateValidated("t1", shapes, durationMs: 200);
            Assert.AreEqual(1000, sceneTooShort.DurationMs);

            var sceneTooLong = OverlayScene.CreateValidated("t2", shapes, durationMs: 120000);
            Assert.AreEqual(30000, sceneTooLong.DurationMs);

            var sceneNormal = OverlayScene.CreateValidated("t3", shapes, durationMs: 8000);
            Assert.AreEqual(8000, sceneNormal.DurationMs);
        }

        [TestMethod]
        public void OverlayScene_EnforcesMax16ShapesBudget()
        {
            var shapes = new List<OverlayShape>();
            for (int i = 0; i < 25; i++)
            {
                shapes.Add(new OverlayShape
                {
                    CueId = $"cue-{i}",
                    ShapeType = OverlayShapeType.Highlight,
                    Rect = new NormalizedRect(0.01 * i, 0.01 * i, 0.05, 0.05)
                });
            }

            var scene = OverlayScene.CreateValidated("target-budget", shapes);
            Assert.AreEqual(16, scene.Shapes.Count);
        }

        [TestMethod]
        public void OverlayShape_PhysicalProjection_AcrossResolutions()
        {
            var shape = new OverlayShape
            {
                CueId = "arrow-cue",
                ShapeType = OverlayShapeType.Arrow,
                Rect = new NormalizedRect(0.2, 0.3, 0.4, 0.2),
                From = new NormalizedPoint(0.2, 0.3),
                To = new NormalizedPoint(0.6, 0.5)
            };

            // Test 1080p viewport (1920x1080)
            double w1080 = 1920;
            double h1080 = 1080;
            double physFromX = shape.From!.Value.X * w1080;
            double physFromY = shape.From!.Value.Y * h1080;
            double physToX = shape.To!.Value.X * w1080;
            double physToY = shape.To!.Value.Y * h1080;

            Assert.AreEqual(384.0, physFromX, 0.1);
            Assert.AreEqual(324.0, physFromY, 0.1);
            Assert.AreEqual(1152.0, physToX, 0.1);
            Assert.AreEqual(540.0, physToY, 0.1);

            // Test 4K viewport (3840x2160)
            double w4k = 3840;
            double h4k = 2160;
            Assert.AreEqual(768.0, shape.From.Value.X * w4k, 0.1);
            Assert.AreEqual(648.0, shape.From.Value.Y * h4k, 0.1);
        }

        [TestMethod]
        public void OverlayScene_AllApprovedShapeTypes_AreRepresentable()
        {
            var approvedTypes = new[]
            {
                OverlayShapeType.Outline,
                OverlayShapeType.Highlight,
                OverlayShapeType.Arrow,
                OverlayShapeType.Callout,
                OverlayShapeType.Equation,
                OverlayShapeType.Circle
            };

            var shapes = new List<OverlayShape>();
            foreach (var type in approvedTypes)
            {
                shapes.Add(new OverlayShape
                {
                    CueId = $"cue-{type}",
                    ShapeType = type,
                    Rect = new NormalizedRect(0.1, 0.1, 0.2, 0.2),
                    Label = $"{type} test"
                });
            }

            var scene = OverlayScene.CreateValidated("all-shapes", shapes);
            Assert.AreEqual(6, scene.Shapes.Count);
        }
    }
}
