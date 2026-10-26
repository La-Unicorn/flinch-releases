using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flinch.Core.Contracts;

namespace Flinch_App.Services
{
    /// <summary>
    /// Playback-clock cue scheduler.
    /// Synchronizes visual overlay cues against the audio/speech playback position.
    /// Invariant: Cancels audio and old cues together; schedules against playback position, not network arrival.
    /// </summary>
    public sealed class CueScheduler
    {
        private readonly AudioService _audioService;
        private CancellationTokenSource? _activePlanCts;

        public event EventHandler<ExplanationSegment>? SegmentStarted;
        public event EventHandler<OverlayShape>? CueActivated;
        public event EventHandler? AllCuesCleared;
        public event EventHandler? PlanCompleted;

        public CueScheduler(AudioService audioService)
        {
            _audioService = audioService;
        }

        public async Task PlayExplanationPlanAsync(
            ExplanationPlan plan,
            OverlayScene scene,
            CancellationToken cancellationToken)
        {
            CancelAndClear();

            _activePlanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _activePlanCts.Token;

            // Map cues by cue ID for fast lookup
            var shapesByCueId = scene.Shapes.ToDictionary(s => s.CueId, s => s, StringComparer.OrdinalIgnoreCase);

            try
            {
                for (int i = 0; i < plan.Segments.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var segment = plan.Segments[i];
                    SegmentStarted?.Invoke(this, segment);

                    // Collect cues associated with this segment
                    var segmentCues = new List<OverlayShape>();
                    foreach (var cueId in segment.VisualCueIds)
                    {
                        if (shapesByCueId.TryGetValue(cueId, out var shape))
                        {
                            segmentCues.Add(shape);
                        }
                    }

                    // Track cue triggering against speech position
                    var pendingCues = segmentCues.Select(s => new TimedVisualCue
                    {
                        CueId = s.CueId,
                        Shape = s,
                        StartOffsetMs = s.DelayMs,
                        HasTriggered = false
                    }).ToList();

                    // If a cue has 0 delay, activate immediately with the start of the segment
                    foreach (var cue in pendingCues.Where(c => c.StartOffsetMs <= 0))
                    {
                        cue.HasTriggered = true;
                        CueActivated?.Invoke(this, cue.Shape);
                    }

                    // Listen to playback position updates to trigger delayed cues
                    void OnPlaybackPositionUpdated(object? sender, double offsetMs)
                    {
                        foreach (var cue in pendingCues.Where(c => !c.HasTriggered))
                        {
                            if (offsetMs >= cue.StartOffsetMs)
                            {
                                cue.HasTriggered = true;
                                CueActivated?.Invoke(this, cue.Shape);
                            }
                        }
                    }

                    _audioService.PlaybackPositionUpdated += OnPlaybackPositionUpdated;

                    try
                    {
                        await _audioService.SpeakTextAsync(segment.SpokenText, token);
                    }
                    finally
                    {
                        _audioService.PlaybackPositionUpdated -= OnPlaybackPositionUpdated;
                    }

                    // Ensure all segment cues triggered before moving to next segment
                    foreach (var cue in pendingCues.Where(c => !c.HasTriggered))
                    {
                        cue.HasTriggered = true;
                        CueActivated?.Invoke(this, cue.Shape);
                    }

                    // Small pause between segments
                    await Task.Delay(250, token);
                }

                PlanCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                // Clean cancellation
                CancelAndClear();
            }
        }

        public void CancelAndClear()
        {
            _audioService.StopAndCancelAll();

            try
            {
                _activePlanCts?.Cancel();
                _activePlanCts?.Dispose();
                _activePlanCts = null;
            }
            catch { }

            AllCuesCleared?.Invoke(this, EventArgs.Empty);
        }
    }
}
