using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Flinch.Core.Contracts;

namespace Flinch_App.Services
{
    /// <summary>
    /// Audio service responsible for text-to-speech synthesis, synchronized position tracking,
    /// and cancellation. Invariant: Converts 100-ns ticks to milliseconds and cancels old audio immediately.
    /// </summary>
    public sealed class AudioService : IDisposable
    {
        private readonly SpeechSynthesizer? _synthesizer;
        private MediaPlayer? _mediaPlayer;
        private CancellationTokenSource? _currentPlaybackCts;
        private bool _isMuted = false;

        public event EventHandler<double>? PlaybackPositionUpdated; // offset in ms
        public event EventHandler? PlaybackCompleted;

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.IsMuted = value;
                }
            }
        }

        public AudioService()
        {
            try
            {
                _synthesizer = new SpeechSynthesizer();
                _mediaPlayer = new MediaPlayer
                {
                    AutoPlay = true,
                    IsMuted = _isMuted
                };

                _mediaPlayer.MediaEnded += (_, _) =>
                {
                    PlaybackCompleted?.Invoke(this, EventArgs.Empty);
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioService] Native audio device init warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Speaks a segment text, tracking position in milliseconds, or runs silent timer if muted / no audio.
        /// </summary>
        public async Task SpeakTextAsync(string spokenText, CancellationToken cancellationToken)
        {
            StopAndCancelAll();

            if (string.IsNullOrWhiteSpace(spokenText))
            {
                PlaybackCompleted?.Invoke(this, EventArgs.Empty);
                return;
            }

            _currentPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _currentPlaybackCts.Token;

            // Check if native synthesizer is available
            if (_synthesizer != null && _mediaPlayer != null && !_isMuted)
            {
                try
                {
                    var stream = await _synthesizer.SynthesizeTextToStreamAsync(spokenText);
                    token.ThrowIfCancellationRequested();

                    var mediaSource = MediaSource.CreateFromStream(stream, stream.ContentType);
                    _mediaPlayer.Source = mediaSource;

                    var playbackTcs = new TaskCompletionSource<bool>();
                    using var reg = token.Register(() =>
                    {
                        try { _mediaPlayer.Pause(); } catch { }
                        playbackTcs.TrySetCanceled(token);
                    });

                    void OnMediaEnded(MediaPlayer mp, object args) => playbackTcs.TrySetResult(true);
                    void OnMediaFailed(MediaPlayer mp, MediaPlayerFailedEventArgs args) => playbackTcs.TrySetResult(false);

                    _mediaPlayer.MediaEnded += OnMediaEnded;
                    _mediaPlayer.MediaFailed += OnMediaFailed;

                    _mediaPlayer.Play();

                    // Track playback position in real-time
                    while (!playbackTcs.Task.IsCompleted && !token.IsCancellationRequested)
                    {
                        var pos = _mediaPlayer.PlaybackSession?.Position ?? TimeSpan.Zero;
                        double ms = TimelineClock.TicksToMilliseconds(pos.Ticks);
                        PlaybackPositionUpdated?.Invoke(this, ms);

                        await Task.Delay(30, token);
                    }

                    _mediaPlayer.MediaEnded -= OnMediaEnded;
                    _mediaPlayer.MediaFailed -= OnMediaFailed;

                    await playbackTcs.Task;
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioService] Playback warning, using silent timer: {ex.Message}");
                }
            }

            // Fallback: Silent clock progression (e.g. for muted or VM environment with no sound hardware)
            // Estimated duration: ~65ms per word + 350ms lead-in
            int wordCount = spokenText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            double durationMs = Math.Max(1200, (wordCount * 65.0) + 400.0);

            var timerStart = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                double elapsedMs = (DateTime.UtcNow - timerStart).TotalMilliseconds;
                PlaybackPositionUpdated?.Invoke(this, elapsedMs);

                if (elapsedMs >= durationMs) break;
                await Task.Delay(30, token);
            }

            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        }

        public void StopAndCancelAll()
        {
            try
            {
                _currentPlaybackCts?.Cancel();
                _currentPlaybackCts?.Dispose();
                _currentPlaybackCts = null;

                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Pause();
                    _mediaPlayer.Source = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            StopAndCancelAll();
            _synthesizer?.Dispose();
            _mediaPlayer?.Dispose();
        }
    }
}
