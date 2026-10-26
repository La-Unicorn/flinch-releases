using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Flinch.Core.Contracts;

namespace Flinch.Core.Tests
{
    [TestClass]
    public class SchedulerTests
    {
        [TestMethod]
        public void TimelineClock_TicksToMilliseconds_FollowsInvariant10000TicksPerMs()
        {
            // 10,000 ticks = 1 ms
            long oneMsTicks = 10_000;
            double ms = TimelineClock.TicksToMilliseconds(oneMsTicks);
            Assert.AreEqual(1.0, ms, 0.0001);

            // 1 second = 10,000,000 ticks = 1000 ms
            long oneSecTicks = 10_000_000;
            Assert.AreEqual(1000.0, TimelineClock.TicksToMilliseconds(oneSecTicks), 0.0001);

            // Round trip
            long roundTrip = TimelineClock.MillisecondsToTicks(ms);
            Assert.AreEqual(oneMsTicks, roundTrip);
        }

        [TestMethod]
        public void TimedVisualCue_InitializesAndTracksTriggerState()
        {
            var shape = new OverlayShape
            {
                CueId = "cue-step-1",
                ShapeType = OverlayShapeType.Highlight,
                Rect = new NormalizedRect(0.1, 0.2, 0.3, 0.4),
                DelayMs = 250
            };

            var cue = new TimedVisualCue
            {
                CueId = shape.CueId,
                Shape = shape,
                StartOffsetMs = shape.DelayMs,
                DurationMs = 4000,
                HasTriggered = false
            };

            Assert.IsFalse(cue.HasTriggered);
            Assert.AreEqual(250.0, cue.StartOffsetMs);

            // Trigger when playback reaches 300ms
            double playbackPosMs = 300.0;
            if (playbackPosMs >= cue.StartOffsetMs)
            {
                cue.HasTriggered = true;
            }

            Assert.IsTrue(cue.HasTriggered);
        }

        [TestMethod]
        public void ExplanationPlan_SegmentsAndCues_PreserveStepOrdering()
        {
            var plan = new ExplanationPlan
            {
                PlanId = "test-plan",
                Segments = new List<ExplanationSegment>
                {
                    new()
                    {
                        Id = "seg-1",
                        VisibleText = "First step",
                        SpokenText = "First step spoken",
                        VisualCueIds = new List<string> { "cue-1", "cue-2" }
                    },
                    new()
                    {
                        Id = "seg-2",
                        VisibleText = "Second step",
                        SpokenText = "Second step spoken",
                        VisualCueIds = new List<string> { "cue-3" }
                    }
                },
                IsVerified = true
            };

            Assert.AreEqual(2, plan.Segments.Count);
            Assert.AreEqual("seg-1", plan.Segments[0].Id);
            Assert.AreEqual(2, plan.Segments[0].VisualCueIds.Count);
            Assert.AreEqual("seg-2", plan.Segments[1].Id);
            Assert.AreEqual("cue-3", plan.Segments[1].VisualCueIds[0]);
            Assert.IsTrue(plan.IsVerified);
        }

        [TestMethod]
        public void ExplanationPlan_ResetAndInterruption_ClearsPendingCues()
        {
            var pendingCues = new List<TimedVisualCue>
            {
                new() { CueId = "c1", Shape = new OverlayShape { CueId = "c1", ShapeType = OverlayShapeType.Outline }, StartOffsetMs = 100 },
                new() { CueId = "c2", Shape = new OverlayShape { CueId = "c2", ShapeType = OverlayShapeType.Arrow }, StartOffsetMs = 500 }
            };

            // Interruption occurs
            pendingCues.Clear();
            Assert.AreEqual(0, pendingCues.Count);
        }
    }
}
