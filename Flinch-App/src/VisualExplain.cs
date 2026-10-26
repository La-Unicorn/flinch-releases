// ============================================================================
// FLINCH AI: VISUAL EXPLAIN CLIENT & WHITEBOARD CONTROLLER
// Step-by-Step Whiteboard Engine with Voice Sync & Transparent Screen Overlay
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace FlinchApp
{
    // =========================================================================
    // 1. Voice Synthesizer Interface (Swappable for Local or Server TTS)
    // =========================================================================
    public interface IVoiceSynthesizer
    {
        Task<int> SynthesizeToFileAsync(string text, string outputPath, CancellationToken cancellationToken = default(CancellationToken));
        Task<int> SpeakTextAsync(string text, CancellationToken cancellationToken = default(CancellationToken));
        void Stop();
    }

    public sealed class WindowsSpeechVoiceSynthesizer : IVoiceSynthesizer
    {
        private SpeechSynthesizer _synth;
        private readonly object _lock = new object();

        public WindowsSpeechVoiceSynthesizer()
        {
            _synth = new SpeechSynthesizer();
            _synth.Rate = 1;
            SelectPreferredVoice();
        }

        private void SelectPreferredVoice()
        {
            try
            {
                var installed = _synth.GetInstalledVoices();
                foreach (var v in installed)
                {
                    if (v == null || !v.Enabled || v.VoiceInfo == null) continue;
                    string name = (v.VoiceInfo.Name ?? "").ToLowerInvariant();
                    // Prefer natural Windows voices
                    if (name.Contains("zira") || name.Contains("eva") || name.Contains("david") || name.Contains("mark"))
                    {
                        _synth.SelectVoice(v.VoiceInfo.Name);
                        break;
                    }
                }
            }
            catch { }
        }

        public Task<int> SynthesizeToFileAsync(string text, string outputPath, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(text)) return 2000;

                lock (_lock)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        using (var ms = new MemoryStream())
                        {
                            _synth.SetOutputToWaveStream(ms);
                            _synth.Speak(text);
                            _synth.SetOutputToNull();

                            byte[] wav = ms.ToArray();
                            if (wav.Length > 44)
                            {
                                File.WriteAllBytes(outputPath, wav);
                                int byteRate = BitConverter.ToInt32(wav, 28);
                                if (byteRate > 0)
                                {
                                    int durationMs = (int)(((long)(wav.Length - 44) * 1000) / byteRate);
                                    return Math.Max(1200, durationMs);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FlinchEnv.Log("SynthesizeToFileAsync error: " + ex.Message);
                    }
                }
                return 3200;
            }, cancellationToken);
        }

        public Task<int> SpeakTextAsync(string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            var tcs = new TaskCompletionSource<int>();
            if (string.IsNullOrWhiteSpace(text))
            {
                tcs.SetResult(0);
                return tcs.Task;
            }

            var sw = Stopwatch.StartNew();
            EventHandler<SpeakCompletedEventArgs> handler = null;
            handler = (s, e) =>
            {
                sw.Stop();
                _synth.SpeakCompleted -= handler;
                tcs.TrySetResult((int)sw.ElapsedMilliseconds);
            };

            _synth.SpeakCompleted += handler;
            _synth.SpeakAsync(text);
            return tcs.Task;
        }

        public void Stop()
        {
            lock (_lock)
            {
                try
                {
                    _synth.SpeakAsyncCancelAll();
                }
                catch { }
            }
        }
    }

    // =========================================================================
    // 2. Data Models for Visual Explain JSON Schema
    // =========================================================================
    public sealed class VisualExplainStep
    {
        public string narration { get; set; }
        public int durationMs { get; set; }
        public string audioUrl { get; set; }
        public List<Dictionary<string, object>> draw { get; set; }
    }

    public sealed class VisualExplainPayload
    {
        public List<VisualExplainStep> steps { get; set; }
        public int count { get; set; }
        public int limit { get; set; }
        public int remaining { get; set; }
    }

    // =========================================================================
    // 3. Client for /functions/v1/visual-explain (Atomic 4 Quota Units)
    // =========================================================================
    internal static class VisualExplainClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

        public static bool RequestVisualExplanation(
            string userQuestion,
            out VisualExplainPayload payload,
            out string errorMessage)
        {
            payload = null;
            errorMessage = "";

            if (SupabaseAuthClient.CurrentSession == null || string.IsNullOrWhiteSpace(SupabaseAuthClient.CurrentSession.AccessToken))
            {
                errorMessage = "Please sign in to use Visual Explain.";
                return false;
            }

            // 1. Capture screen context
            byte[] screenBytes = null;
            try
            {
                var cap = new WindowsScreenCaptureService();
                var primary = System.Windows.Forms.Screen.PrimaryScreen;
                var pBounds = new NativeMethods.RECT
                {
                    Left = primary.Bounds.Left,
                    Top = primary.Bounds.Top,
                    Right = primary.Bounds.Right,
                    Bottom = primary.Bounds.Bottom
                };
                CapturedFrame f = cap.CaptureBounds(pBounds, "Display");
                if (f != null && f.JpegBytes != null)
                {
                    screenBytes = f.JpegBytes;
                }
            }
            catch (Exception capEx)
            {
                FlinchEnv.Log("Screen capture error in VisualExplainClient: " + capEx.Message);
            }

            if (screenBytes == null || screenBytes.Length == 0)
            {
                errorMessage = "Could not capture screen context.";
                return false;
            }

            string endpoint = FlinchEnv.SupabaseUrl.TrimEnd('/') + "/functions/v1/visual-explain";

            // 2. Send request with 1-retry fallback on validation or network failure
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                    using (var mp = new MultipartFormDataContent())
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.AccessToken);
                        req.Headers.TryAddWithoutValidation("apikey", FlinchEnv.SupabaseAnonKey);

                        var screenContent = new ByteArrayContent(screenBytes);
                        screenContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
                        mp.Add(screenContent, "screen", "screen.jpg");
                        mp.Add(new StringContent(string.IsNullOrWhiteSpace(userQuestion) ? "Explain this math problem" : userQuestion), "prompt");

                        req.Content = mp;

                        using (var resp = Http.SendAsync(req).GetAwaiter().GetResult())
                        {
                            int statusCode = (int)resp.StatusCode;
                            string respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                            if (statusCode == 401 && attempt == 0 && !string.IsNullOrWhiteSpace(SupabaseAuthClient.CurrentSession.RefreshToken))
                            {
                                string refreshErr;
                                if (SupabaseAuthClient.RefreshSession(SupabaseAuthClient.CurrentSession.RefreshToken, out refreshErr))
                                {
                                    continue; // Retry with fresh token
                                }
                            }

                            if (statusCode == 429)
                            {
                                errorMessage = "Daily limit reached. Resets at midnight.";
                                return false;
                            }

                            if (statusCode >= 200 && statusCode < 300 && !string.IsNullOrWhiteSpace(respBody))
                            {
                                var parsed = ParseAndValidate(respBody, out errorMessage);
                                if (parsed != null)
                                {
                                    payload = parsed;
                                    if (payload.remaining >= 0)
                                    {
                                        SupabaseAuthClient.VoiceRemaining = payload.remaining;
                                    }
                                    return true;
                                }
                                // If validation failed, loop to retry once
                            }
                            else
                            {
                                errorMessage = "Visual explanation service unavailable (" + statusCode + ").";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = "Network error: " + ex.Message;
                }

                Thread.Sleep(300);
            }

            return false;
        }

        private static VisualExplainPayload ParseAndValidate(string json, out string validationError)
        {
            validationError = "";
            try
            {
                var jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var dict = jss.Deserialize<Dictionary<string, object>>(json);
                if (dict == null || !dict.ContainsKey("steps"))
                {
                    validationError = "Missing 'steps' array in response.";
                    return null;
                }

                var stepsList = new List<VisualExplainStep>();
                var rawSteps = dict["steps"] as System.Collections.ArrayList;
                if (rawSteps == null || rawSteps.Count == 0)
                {
                    validationError = "Response has zero steps.";
                    return null;
                }

                foreach (var stObj in rawSteps)
                {
                    var stDict = stObj as Dictionary<string, object>;
                    if (stDict == null) continue;

                    string narr = stDict.ContainsKey("narration") && stDict["narration"] != null
                        ? stDict["narration"].ToString().Trim()
                        : "Observe this step.";

                    var drawList = new List<Dictionary<string, object>>();
                    if (stDict.ContainsKey("draw") && stDict["draw"] is System.Collections.ArrayList)
                    {
                        var arr = (System.Collections.ArrayList)stDict["draw"];
                        foreach (var d in arr)
                        {
                            if (d is Dictionary<string, object>)
                            {
                                drawList.Add((Dictionary<string, object>)d);
                            }
                        }
                    }

                    stepsList.Add(new VisualExplainStep
                    {
                        narration = narr,
                        durationMs = 3200,
                        draw = drawList
                    });
                }

                if (stepsList.Count == 0)
                {
                    validationError = "No valid steps could be parsed.";
                    return null;
                }

                int rem = dict.ContainsKey("remaining") && dict["remaining"] != null
                    ? Convert.ToInt32(dict["remaining"])
                    : -1;

                return new VisualExplainPayload
                {
                    steps = stepsList,
                    remaining = rem
                };
            }
            catch (Exception ex)
            {
                validationError = "JSON schema validation failed: " + ex.Message;
                return null;
            }
        }
    }

    // =========================================================================
    // 4. Whiteboard & Overlay Presentation Manager
    // =========================================================================
    internal static class WhiteboardOverlayManager
    {
        private static Process _whiteboardProcess = null;
        private static readonly IVoiceSynthesizer Synthesizer = new WindowsSpeechVoiceSynthesizer();
        private static readonly object _wbLock = new object();

        public static bool IsOpen
        {
            get
            {
                lock (_wbLock)
                {
                    return _whiteboardProcess != null && !_whiteboardProcess.HasExited;
                }
            }
        }

        public static void LaunchSolution(VisualExplainPayload payload, Action onClosed = null)
        {
            if (payload == null || payload.steps == null || payload.steps.Count == 0) return;

            Task.Run(async () =>
            {
                try
                {
                    Close();

                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string[] candidates = new[]
                    {
                        Path.Combine(appDir, "assets", "whiteboard"),
                        Path.Combine(appDir, "Flinch-App", "assets", "whiteboard"),
                        Path.Combine(Directory.GetParent(appDir).FullName, "assets", "whiteboard"),
                        Path.Combine(Directory.GetParent(appDir).FullName, "Flinch-App", "assets", "whiteboard"),
                        Path.Combine(Directory.GetParent(Directory.GetParent(appDir).FullName).FullName, "Flinch-App", "assets", "whiteboard")
                    };
                    string whiteboardDir = candidates[0];
                    foreach (var c in candidates)
                    {
                        if (Directory.Exists(c)) { whiteboardDir = c; break; }
                    }
                    string audioDir = Path.Combine(whiteboardDir, "audio");
                    Directory.CreateDirectory(audioDir);

                    // 1. Synthesize audio per step and calibrate exact speech duration
                    for (int i = 0; i < payload.steps.Count; i++)
                    {
                        var step = payload.steps[i];
                        string audioFile = Path.Combine(audioDir, "step_" + i + ".wav");
                        int dur = await Synthesizer.SynthesizeToFileAsync(step.narration, audioFile);
                        step.durationMs = dur;
                        step.audioUrl = "audio/step_" + i + ".wav";
                    }

                    // 2. Check for highlight_on_screen and trigger transparent screen overlay
                    foreach (var step in payload.steps)
                    {
                        if (step.draw != null)
                        {
                            foreach (var d in step.draw)
                            {
                                string type = d.ContainsKey("type") ? (d["type"] ?? "").ToString().ToLowerInvariant() : "";
                                if (type == "highlight_on_screen")
                                {
                                    double hx = d.ContainsKey("x") ? Convert.ToDouble(d["x"]) : 0;
                                    double hy = d.ContainsKey("y") ? Convert.ToDouble(d["y"]) : 0;
                                    double hw = d.ContainsKey("w") ? Convert.ToDouble(d["w"]) : 100;
                                    double hh = d.ContainsKey("h") ? Convert.ToDouble(d["h"]) : 100;
                                    string lbl = d.ContainsKey("label") ? d["label"].ToString() : "Target Problem";

                                    var primary = System.Windows.Forms.Screen.PrimaryScreen;
                                    var scene = new ScenePayload
                                    {
                                        durationMs = 24000,
                                        objects = new List<SceneObjectPayload>
                                        {
                                            new SceneObjectPayload
                                            {
                                                type = "outline",
                                                rect = new SceneRect
                                                {
                                                    x = Math.Max(0, Math.Min(1.0, hx / 1000.0)),
                                                    y = Math.Max(0, Math.Min(1.0, hy / 1000.0)),
                                                    w = Math.Max(0.05, Math.Min(1.0, hw / 1000.0)),
                                                    h = Math.Max(0.05, Math.Min(1.0, hh / 1000.0))
                                                },
                                                label = lbl,
                                                color = "#3DDC84",
                                                step = 1,
                                                delayMs = 150
                                            }
                                        }
                                    };
                                    OverlayWindow.ShowScene(new NativeMethods.RECT
                                    {
                                        Left = primary.Bounds.Left,
                                        Top = primary.Bounds.Top,
                                        Right = primary.Bounds.Right,
                                        Bottom = primary.Bounds.Bottom
                                    }, scene);
                                    break;
                                }
                            }
                        }
                    }

                    // 3. Write payload.js for instant, reliable loading
                    var jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    string json = jss.Serialize(payload);
                    string payloadJs = Path.Combine(whiteboardDir, "payload.js");
                    File.WriteAllText(payloadJs, "window.FLINCH_PAYLOAD = " + json + ";", Encoding.UTF8);

                    // 4. Position whiteboard window directly expanding beneath the Dynamic Island notch
                    var scr = System.Windows.Forms.Screen.PrimaryScreen;
                    int winW = Math.Min(960, scr.Bounds.Width - 80);
                    int winH = Math.Min(680, scr.Bounds.Height - 120);
                    int winX = (scr.Bounds.Width - winW) / 2;
                    int winY = 48; // Expands immediately beneath the top notch

                    string htmlPath = Path.Combine(whiteboardDir, "index.html");
                    string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
                    if (!File.Exists(edgePath))
                    {
                        edgePath = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
                    }

                    ProcessStartInfo psi;
                    if (File.Exists(edgePath))
                    {
                        psi = new ProcessStartInfo
                        {
                            FileName = edgePath,
                            Arguments = string.Format("--app=\"file:///{0}\" --window-size={1},{2} --window-position={3},{4}", htmlPath.Replace('\\', '/'), winW, winH, winX, winY),
                            UseShellExecute = true
                        };
                    }
                    else
                    {
                        psi = new ProcessStartInfo
                        {
                            FileName = htmlPath,
                            UseShellExecute = true
                        };
                    }

                    lock (_wbLock)
                    {
                        _whiteboardProcess = Process.Start(psi);
                    }

                    if (_whiteboardProcess != null)
                    {
                        _whiteboardProcess.EnableRaisingEvents = true;
                        _whiteboardProcess.Exited += (s, e) =>
                        {
                            Close();
                            if (onClosed != null) onClosed();
                        };
                    }
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("Whiteboard launch error: " + ex.Message);
                    Close();
                    if (onClosed != null) onClosed();
                }
            });
        }

        public static void Close()
        {
            lock (_wbLock)
            {
                try
                {
                    Synthesizer.Stop();
                    OverlayWindow.CloseCurrent();
                    if (_whiteboardProcess != null && !_whiteboardProcess.HasExited)
                    {
                        _whiteboardProcess.Kill();
                    }
                }
                catch { }
                finally
                {
                    _whiteboardProcess = null;
                }
            }
        }
    }
}
