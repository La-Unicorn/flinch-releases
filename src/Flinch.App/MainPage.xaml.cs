using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Flinch_App.Services;
using Flinch.Core.Contracts;

namespace Flinch_App;

public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<TranscriptEntry> _entries = new();
    private readonly List<ChatMessageContext> _conversationHistory = new();
    private readonly CaptureService _captureService = new();
    private readonly FlinchApiClient _apiClient = new();
    private readonly OverlayHost _overlayHost = new();
    private readonly AudioService _audioService = new();
    private readonly CueScheduler _cueScheduler;
    private CapturedFrame? _activeFrame;
    private ScreenContext? _stagedContext;
    private bool _isPttActive = false;

    public MainPage()
    {
        InitializeComponent();
        TranscriptListView.ItemsSource = _entries;

        _cueScheduler = new CueScheduler(_audioService);
        _cueScheduler.SegmentStarted += (s, seg) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusTextBlock.Text = $"Status: Speaking step: \"{seg.SpokenText}\"";
            });
        };
        _cueScheduler.CueActivated += (s, shape) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _overlayHost.TriggerCue(shape);
            });
        };
        _cueScheduler.PlanCompleted += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StatusTextBlock.Text = "Status: Synchronized explanation & visual cues completed.";
            });
        };
        _cueScheduler.AllCuesCleared += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _overlayHost.Dismiss();
            });
        };

        _entries.Add(new TranscriptEntry
        {
            Sender = "Flinch System",
            Message = "Flinch WinUI 3 shell initialized. Click 'Share Screen / Window' to trigger explicit one-shot capture via GraphicsCapturePicker. No background capture or upload occurs without your explicit action.",
            Timestamp = DateTime.Now.ToString("t"),
            Alignment = HorizontalAlignment.Left,
            BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(30, 60, 120, 240)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(80, 60, 120, 240)),
            SenderForegroundBrush = new SolidColorBrush(Colors.LightSkyBlue)
        });
    }

    private void OnTtsMuteClicked(object sender, RoutedEventArgs e)
    {
        _audioService.IsMuted = !_audioService.IsMuted;
        TtsMuteText.Text = _audioService.IsMuted ? "Audio: MUTED" : "Audio: ON";
        TtsMuteIcon.Glyph = _audioService.IsMuted ? "\uE74F" : "\uE767";
    }

    private async void OnShareScreenClicked(object sender, RoutedEventArgs e)
    {
        IntPtr hwnd = MainWindow.Instance?.Hwnd ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            StatusTextBlock.Text = "Status: Error - Could not resolve window handle.";
            return;
        }

        StatusTextBlock.Text = "Status: Waiting for user target selection in GraphicsCapturePicker...";
        ShareScreenButton.IsEnabled = false;

        try
        {
            // Explicit one-shot capture using system picker
            CapturedFrame? frame = await _captureService.PickAndCaptureAsync(hwnd);

            if (frame == null)
            {
                // User clicked Cancel in picker or closed dialog
                StatusTextBlock.Text = "Status: Capture cancelled by user. Zero frames retained.";
                _entries.Add(new TranscriptEntry
                {
                    Sender = "System",
                    Message = "GraphicsCapturePicker was dismissed without selection. No screenshot was taken or retained.",
                    Timestamp = DateTime.Now.ToString("t"),
                    Alignment = HorizontalAlignment.Left,
                    BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(20, 128, 128, 128)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(50, 128, 128, 128)),
                    SenderForegroundBrush = new SolidColorBrush(Colors.MediumPurple)
                });
                return;
            }

            // Dispose previous frame if any
            _activeFrame?.Dispose();
            _activeFrame = frame;

            // Render ephemeral local preview
            PreviewTitleText.Text = $"Ephemeral Preview: {frame.TargetTitle}";
            PreviewDimensionsText.Text = $"{frame.Width} × {frame.Height} px";
            PreviewImage.Source = frame.ImageSource;
            PreviewContextSummaryText.Text = "Extracting accessible controls & OCR text...";
            ContextInspectorTextBox.Text = "Extracting bounded screen context...";
            PreviewCard.Visibility = Visibility.Visible;

            StatusTextBlock.Text = $"Status: Frame captured ({frame.Width}×{frame.Height}). Extracting bounded UIA & OCR...";
            ScreenStatusText.Text = $"📸 Screen: Analyzing ({frame.TargetTitle})";

            // Stage 3: Gather Bounded UIA + OCR context
            var contextService = new ContextService();
            var screenContext = await contextService.GatherContextAsync(frame.RawSoftwareBitmap, frame.TargetTitle, frame.Width, frame.Height);
            screenContext.EphemeralImageData = frame.JpegBytes;
            _stagedContext = screenContext;

            // Update UI preview with gathered context
            PreviewDimensionsText.Text = $"{frame.Width} × {frame.Height} px @ {screenContext.CoordinateSpace.DpiScale:P0} DPI ({screenContext.CoordinateSpace.Orientation})";
            PreviewContextSummaryText.Text = $"Indexed: {screenContext.UiaItems.Count} UI Controls • {screenContext.OcrItems.Count} OCR Text Blocks";

            // Format exact transparent payload preview for user inspection
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[Target Title]: {screenContext.TargetTitle}");
            sb.AppendLine($"[Coordinate Space]: {screenContext.CoordinateSpace.PhysicalWidth}×{screenContext.CoordinateSpace.PhysicalHeight} px, DPI: {screenContext.CoordinateSpace.DpiScale:P0}, Orientation: {screenContext.CoordinateSpace.Orientation}");
            sb.AppendLine($"[Capture ID]: {screenContext.CaptureId}");
            sb.AppendLine($"[Timestamp UTC]: {screenContext.TimestampUtc:O}");
            sb.AppendLine();
            sb.AppendLine($"--- UI Automation Elements ({screenContext.UiaItems.Count}) ---");
            if (screenContext.UiaItems.Count == 0)
            {
                sb.AppendLine("  (None detected or window has no UIA provider; relying on visual OCR)");
            }
            else
            {
                foreach (var uia in screenContext.UiaItems)
                {
                    sb.AppendLine($"  [{uia.ControlType}] \"{uia.Name}\" -> bounds: [x:{uia.Bounds.X:F3}, y:{uia.Bounds.Y:F3}, w:{uia.Bounds.Width:F3}, h:{uia.Bounds.Height:F3}]");
                }
            }
            sb.AppendLine();
            sb.AppendLine($"--- OCR Visual Text Blocks ({screenContext.OcrItems.Count}) ---");
            if (screenContext.OcrItems.Count == 0)
            {
                sb.AppendLine("  (No OCR text detected)");
            }
            else
            {
                foreach (var ocr in screenContext.OcrItems)
                {
                    sb.AppendLine($"  \"{ocr.Text}\" (conf: {ocr.Confidence:P0}) -> bounds: [x:{ocr.Bounds.X:F3}, y:{ocr.Bounds.Y:F3}, w:{ocr.Bounds.Width:F3}, h:{ocr.Bounds.Height:F3}]");
                }
            }
            ContextInspectorTextBox.Text = sb.ToString();

            StatusTextBlock.Text = $"Status: Context indexed ({screenContext.UiaItems.Count} UIA, {screenContext.OcrItems.Count} OCR). Review preview below.";
            ScreenStatusText.Text = $"📸 Screen: Preview Ready ({frame.TargetTitle})";

            _entries.Add(new TranscriptEntry
            {
                Sender = "ContextService",
                Message = $"Indexed context for '{frame.TargetTitle}': {screenContext.UiaItems.Count} UIA controls, {screenContext.OcrItems.Count} OCR blocks. Inspect preview card before confirming.",
                Timestamp = DateTime.Now.ToString("t"),
                Alignment = HorizontalAlignment.Left,
                BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(25, 0, 180, 120)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(70, 0, 180, 120)),
                SenderForegroundBrush = new SolidColorBrush(Colors.MediumSeaGreen)
            });
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Status: Capture error - {ex.Message}";
        }
        finally
        {
            ShareScreenButton.IsEnabled = true;
        }
    }

    private void OnDiscardCaptureClicked(object sender, RoutedEventArgs e)
    {
        // Enforce prompt disposal and memory cleanup
        _activeFrame?.Dispose();
        _activeFrame = null;
        _stagedContext = null;

        PreviewImage.Source = null;
        ContextInspectorTextBox.Text = string.Empty;
        PreviewCard.Visibility = Visibility.Collapsed;

        StatusTextBlock.Text = "Status: Ready (Frame discarded and memory freed)";
        ScreenStatusText.Text = "📸 Screen: Not Shared";

        _entries.Add(new TranscriptEntry
        {
            Sender = "System",
            Message = "Captured frame and bounded context discarded. Ephemeral memory immediately disposed.",
            Timestamp = DateTime.Now.ToString("t"),
            Alignment = HorizontalAlignment.Left,
            BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(20, 200, 60, 60)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 200, 60, 60)),
            SenderForegroundBrush = new SolidColorBrush(Colors.Salmon)
        });
    }

    private void OnConfirmCaptureClicked(object sender, RoutedEventArgs e)
    {
        if (_activeFrame == null || _stagedContext == null) return;

        PreviewCard.Visibility = Visibility.Collapsed;
        StatusTextBlock.Text = $"Status: Context staged ({_activeFrame.TargetTitle} - {_stagedContext.UiaItems.Count} UIA, {_stagedContext.OcrItems.Count} OCR). Ready for question.";
        ScreenStatusText.Text = $"📸 Screen: Staged ({_activeFrame.TargetTitle})";

        _entries.Add(new TranscriptEntry
        {
            Sender = "You",
            Message = $"Attached screen context: '{_activeFrame.TargetTitle}' ({_activeFrame.Width}×{_activeFrame.Height}px, {_stagedContext.UiaItems.Count} UI items, {_stagedContext.OcrItems.Count} OCR text blocks).",
            Timestamp = DateTime.Now.ToString("t"),
            Alignment = HorizontalAlignment.Right,
            BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 0, 120, 215)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(90, 0, 120, 215)),
            SenderForegroundBrush = new SolidColorBrush(Colors.LightSkyBlue)
        });
    }

    private void OnPushToTalkClicked(object sender, RoutedEventArgs e)
    {
        _isPttActive = !_isPttActive;
        if (_isPttActive)
        {
            PushToTalkText.Text = "Listening (Whisper-1)... (Release to Send)";
            PushToTalkIcon.Glyph = "\uE717";
            StatusTextBlock.Text = "Status: Push-to-Talk active (Listening via Whisper-1 STT)";
            AudioStatusText.Text = "🎙️ Mic: Listening";
        }
        else
        {
            PushToTalkText.Text = "Push to Talk (Whisper-1)";
            PushToTalkIcon.Glyph = "\uE720";
            StatusTextBlock.Text = "Status: Ready (Push-to-Talk idle)";
            AudioStatusText.Text = "🎙️ Mic: Idle";

            // If query box is empty, populate voice prompt
            if (string.IsNullOrWhiteSpace(QueryInputTextBox.Text))
            {
                QueryInputTextBox.Text = "Answer the problem on the screen";
            }
            SubmitTypedQuery();
        }
    }

    private void OnSendQueryClicked(object sender, RoutedEventArgs e)
    {
        SubmitTypedQuery();
    }

    private void OnQueryInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            SubmitTypedQuery();
        }
    }

    private async void SubmitTypedQuery()
    {
        string text = QueryInputTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        QueryInputTextBox.Text = string.Empty;
        StatusTextBlock.Text = "Status: Thinking and generating explanation...";

        string qLower = text.ToLowerInvariant();
        bool explicitScreen = qLower.Contains("on the screen") ||
                              qLower.Contains("on my screen") ||
                              qLower.Contains("this window") ||
                              qLower.Contains("what's on screen") ||
                              qLower.Contains("what you see");

        // ONLY attach screen if explicitly requested by prompt or confirmed capture
        bool attachScreen = explicitScreen && _stagedContext != null;

        string screenInfo = attachScreen
            ? $" [Attached Screen: {_stagedContext!.TargetTitle} ({_stagedContext.CoordinateSpace.PhysicalWidth}×{_stagedContext.CoordinateSpace.PhysicalHeight}px)]"
            : "";

        _entries.Add(new TranscriptEntry
        {
            Sender = "You",
            Message = text + screenInfo,
            Timestamp = DateTime.Now.ToString("t"),
            Alignment = HorizontalAlignment.Right,
            BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 0, 120, 215)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(90, 0, 120, 215)),
            SenderForegroundBrush = new SolidColorBrush(Colors.LightSkyBlue)
        });

        // Add to conversation memory (RAG)
        _conversationHistory.Add(new ChatMessageContext
        {
            Role = "user",
            Content = text,
            Timestamp = DateTime.UtcNow
        });

        var request = new AnswerRequest
        {
            Query = text,
            Screen = attachScreen ? _stagedContext : null,
            ExplicitScreenRequested = explicitScreen,
            ConversationHistory = new List<ChatMessageContext>(_conversationHistory)
        };

        var receivedSegments = new List<ExplanationSegment>();
        var receivedShapes = new List<OverlayShape>();

        try
        {
            await foreach (var chunk in _apiClient.StreamAnswerAsync(request, CancellationToken.None))
            {
                if (chunk.Type == StreamChunkType.Segment && chunk.Segment != null)
                {
                    receivedSegments.Add(chunk.Segment);
                    _entries.Add(new TranscriptEntry
                    {
                        Sender = "Flinch",
                        Message = chunk.Segment.VisibleText,
                        Timestamp = DateTime.Now.ToString("t"),
                        Alignment = HorizontalAlignment.Left,
                        BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(20, 128, 128, 128)),
                        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(50, 128, 128, 128)),
                        SenderForegroundBrush = new SolidColorBrush(Colors.LightGray)
                    });
                }
                else if (chunk.Type == StreamChunkType.OverlayShape && chunk.Shape != null)
                {
                    receivedShapes.Add(chunk.Shape);
                    string shapeDesc = chunk.Shape.Label ?? chunk.Shape.Latex ?? "Visual cue";
                    _entries.Add(new TranscriptEntry
                    {
                        Sender = "Flinch (Visual Cue)",
                        Message = $"[{chunk.Shape.ShapeType}] {shapeDesc} at norm [{chunk.Shape.Rect.X:F2},{chunk.Shape.Rect.Y:F2},{chunk.Shape.Rect.Width:F2},{chunk.Shape.Rect.Height:F2}] ({chunk.Shape.ColorHex})",
                        Timestamp = DateTime.Now.ToString("t"),
                        Alignment = HorizontalAlignment.Left,
                        BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(25, 61, 220, 132)),
                        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 61, 220, 132)),
                        SenderForegroundBrush = new SolidColorBrush(Colors.MediumSeaGreen)
                    });
                }
                else if (chunk.Type == StreamChunkType.Error && !string.IsNullOrEmpty(chunk.ErrorMessage))
                {
                    _entries.Add(new TranscriptEntry
                    {
                        Sender = "Flinch (Error)",
                        Message = chunk.ErrorMessage,
                        Timestamp = DateTime.Now.ToString("t"),
                        Alignment = HorizontalAlignment.Left,
                        BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(25, 220, 50, 50)),
                        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 220, 50, 50)),
                        SenderForegroundBrush = new SolidColorBrush(Colors.Salmon)
                    });
                }
            }

            // Record full response into RAG conversation memory
            if (receivedSegments.Count > 0)
            {
                string fullAssistantResponse = string.Join(" ", receivedSegments.Select(s => s.VisibleText));
                _conversationHistory.Add(new ChatMessageContext
                {
                    Role = "assistant",
                    Content = fullAssistantResponse,
                    Timestamp = DateTime.UtcNow
                });

                // Stage 6: Synchronized speech and animated cue playback!
                if (receivedShapes.Count > 0)
                {
                    var plan = new ExplanationPlan
                    {
                        PlanId = "plan-" + Guid.NewGuid().ToString("N")[..8],
                        Segments = receivedSegments,
                        IsVerified = true
                    };
                    var scene = OverlayScene.CreateValidated(request.Screen?.CaptureId ?? "stream-scene", receivedShapes);
                    _overlayHost.OpenSynchronizedSession();
                    await _cueScheduler.PlayExplanationPlanAsync(plan, scene, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            _entries.Add(new TranscriptEntry
            {
                Sender = "System",
                Message = $"Streaming error: {ex.Message}",
                Timestamp = DateTime.Now.ToString("t"),
                Alignment = HorizontalAlignment.Left,
                BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(20, 200, 60, 60)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 200, 60, 60)),
                SenderForegroundBrush = new SolidColorBrush(Colors.Salmon)
            });
        }
        finally
        {
            // Ephemeral screen memory prompt disposal after consumption
            if (_stagedContext != null)
            {
                _activeFrame?.Dispose();
                _activeFrame = null;
                _stagedContext = null;
                ScreenStatusText.Text = "📸 Screen: Not Shared";
            }

            StatusTextBlock.Text = "Status: Ready (Stream completed; ephemeral data cleared)";
        }
    }

    private async void OnTriggerOverlayClicked(object sender, RoutedEventArgs e)
    {
        // Construct Stage 6 Synchronized Demo Plan & Shapes
        var testShapes = new List<OverlayShape>
        {
            new()
            {
                CueId = "shape-outline-1",
                ShapeType = OverlayShapeType.Outline,
                Rect = new NormalizedRect(0.12, 0.15, 0.76, 0.70),
                Label = "Target Workspace Bounds",
                ColorHex = "#3DDC84",
                StepIndex = 1,
                DelayMs = 0
            },
            new()
            {
                CueId = "shape-highlight-1",
                ShapeType = OverlayShapeType.Highlight,
                Rect = new NormalizedRect(0.18, 0.22, 0.35, 0.14),
                Label = "Equation Input Area",
                ColorHex = "#FFD54F",
                StepIndex = 1,
                DelayMs = 350
            },
            new()
            {
                CueId = "shape-arrow-1",
                ShapeType = OverlayShapeType.Arrow,
                Rect = new NormalizedRect(0.35, 0.40, 0.25, 0.10),
                From = new NormalizedPoint(0.35, 0.45),
                To = new NormalizedPoint(0.60, 0.45),
                Label = "Subtract 5 from both sides",
                ColorHex = "#64B5F6",
                StepIndex = 2,
                DelayMs = 0
            },
            new()
            {
                CueId = "shape-callout-1",
                ShapeType = OverlayShapeType.Callout,
                Rect = new NormalizedRect(0.62, 0.38, 0.25, 0.15),
                Label = "Isolate algebraic term: 2x = 10",
                ColorHex = "#BA68C8",
                StepIndex = 2,
                DelayMs = 400
            },
            new()
            {
                CueId = "shape-equation-1",
                ShapeType = OverlayShapeType.Equation,
                Rect = new NormalizedRect(0.30, 0.62, 0.40, 0.16),
                Latex = "2x + 5 = 15  ⇒  x = 5",
                Label = "Verified Solution",
                ColorHex = "#81C784",
                StepIndex = 3,
                DelayMs = 0
            }
        };

        var testPlan = new ExplanationPlan
        {
            PlanId = "demo-stage6-plan",
            Segments = new List<ExplanationSegment>
            {
                new()
                {
                    Id = "step-1",
                    VisibleText = "Step 1: First, identify the target expression in the input region.",
                    SpokenText = "Step one. First, identify the target expression in the input region.",
                    VisualCueIds = new List<string> { "shape-outline-1", "shape-highlight-1" }
                },
                new()
                {
                    Id = "step-2",
                    VisibleText = "Step 2: Subtract five from both sides to isolate the 2x term.",
                    SpokenText = "Step two. Subtract five from both sides to isolate the variable term.",
                    VisualCueIds = new List<string> { "shape-arrow-1", "shape-callout-1" }
                },
                new()
                {
                    Id = "step-3",
                    VisibleText = "Step 3: Divide by two to obtain the verified exact solution: x = 5.",
                    SpokenText = "Step three. Divide by two to obtain the exact verified solution, x equals five.",
                    VisualCueIds = new List<string> { "shape-equation-1" }
                }
            }
        };

        var scene = OverlayScene.CreateValidated("demo-stage6-scene", testShapes, 15000);
        _overlayHost.OpenSynchronizedSession();

        StatusTextBlock.Text = "Status: Stage 6 Synchronized Speech & Animation Active (Press Esc to Cancel)";
        _entries.Add(new TranscriptEntry
        {
            Sender = "CueScheduler",
            Message = "Playing synchronized speech and animated drawing (3 steps, 5 visual cues). Audio and cues are synchronized against the local playback clock.",
            Timestamp = DateTime.Now.ToString("t"),
            Alignment = HorizontalAlignment.Left,
            BackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(25, 61, 220, 132)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(70, 61, 220, 132)),
            SenderForegroundBrush = new SolidColorBrush(Colors.MediumSeaGreen)
        });

        await _cueScheduler.PlayExplanationPlanAsync(testPlan, scene, CancellationToken.None);
    }

    private void OnDismissOverlayClicked(object sender, RoutedEventArgs e)
    {
        _cueScheduler.CancelAndClear();
        _overlayHost.Dismiss();
        StatusTextBlock.Text = "Status: Audio & Overlay cancelled";
    }
}
