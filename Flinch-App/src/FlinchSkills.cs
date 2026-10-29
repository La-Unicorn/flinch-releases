using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FlinchApp
{
    // =========================================================================
    // Flinch Skills: local voice-command skills that run on the desktop.
    //   1. Clipboard actions         ("summarize what I copied", "reply to this email")
    //   2. Daily push-to-talk memory ("what did we talk about yesterday?", "what did I ask earlier?")
    //   3. Smart context reminders   ("remind me when I open Chrome to...", "remind me at 5 to call mom")
    //   4. Pomodoro and focus mode   ("start pomodoro", "focus for 25 minutes", "stop focus")
    //   5. Voice-driven file work    ("find my Sri Lanka essay", "zip my Flinch folder", "rename latest download")
    //   6. Proactive nudges          (error dialog detection, long idle/work stretch breaks)
    //   7. Quick screen translate    ("translate this page to Sinhala", "translate this screen to French")
    //   8. Voice & emotion picker    ("switch voice to Autumn", "change voice to Daniel", "set voice to auto")
    // NOTE: compiled with the .NET Framework 4 C# compiler (C# 5): no ?., no $"", no out var.
    // =========================================================================
    internal static class FlinchSkills
    {
        // Hooks set by the main window
        public static Action<string> Speak;                         // speak when the pill is idle
        public static Action<int, string> StartReminderInSeconds;   // seconds, reminder text
        public static Action<string> FireContextReminder;           // reminder text -> alert + speech
        public static Action<int, string> StartFocusTimer;          // seconds, focus label
        public static Action StopFocusTimer;                        // cancel focus mode
        public static Action<string, string> ShowNudgeAlert;        // title, message
        public static Action<string> SetPreferredVoice;             // update voice name

        public static bool IsFocusModeActive { get; set; }
        public static DateTime FocusModeEndUtc { get; set; }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };

        // ---------------------------------------------------------------------
        // Entry point. Returns null when the phrase is not a skill command,
        // otherwise the sentence Flinch should speak right now (may be "").
        // ---------------------------------------------------------------------
        public static string TryHandle(string transcript, string low)
        {
            if (string.IsNullOrWhiteSpace(low)) return null;
            low = low.Trim().TrimEnd('.', '!', '?', ' ');

            string r;
            r = TryPomodoro(transcript, low);        if (r != null) return r;
            r = TryVoicePicker(transcript, low);     if (r != null) return r;
            r = TryMemory(transcript, low);          if (r != null) return r;
            r = TryScreenTranslate(transcript, low); if (r != null) return r;
            r = TryReminder(transcript, low);        if (r != null) return r;
            r = TryClipboard(transcript, low);       if (r != null) return r;
            r = TryFiles(transcript, low);           if (r != null) return r;
            return null;
        }

        public static void Start()
        {
            LoadContextReminders();
            _watchTimer = new Timer(delegate { WatchContextReminders(); }, null, 4000, 3000);
            _nudgeTimer = new Timer(delegate { WatchNudges(); }, null, 15000, 10000);
        }

        // =====================================================================
        // 1. POMODORO AND FOCUS MODE
        // =====================================================================
        private static string TryPomodoro(string transcript, string low)
        {
            bool isStop = Regex.IsMatch(low, @"\b(stop|cancel|end|quit|turn off)\b.*\b(pomodoro|focus|focus mode)\b");
            if (isStop)
            {
                if (!IsFocusModeActive) return "Focus mode is not currently running.";
                IsFocusModeActive = false;
                if (StopFocusTimer != null) StopFocusTimer();
                return "Focus mode ended. Notifications are unmuted.";
            }

            bool isStart = Regex.IsMatch(low, @"\b(start|begin|set|put me in|turn on)\b.*\b(pomodoro|focus|focus mode)\b")
                           || low == "pomodoro" || low == "focus mode" || low.StartsWith("pomodoro ") || low.StartsWith("focus for ");
            if (!isStart) return null;

            int minutes = 25;
            Match m = Regex.Match(low, @"(\d+)\s*(minute|min|hr|hour)s?");
            if (m.Success)
            {
                int val;
                if (int.TryParse(m.Groups[1].Value, out val) && val > 0)
                {
                    minutes = m.Groups[2].Value.StartsWith("hr") || m.Groups[2].Value.StartsWith("hour") ? val * 60 : val;
                }
            }

            int totalSec = minutes * 60;
            IsFocusModeActive = true;
            FocusModeEndUtc = DateTime.UtcNow.AddSeconds(totalSec);

            if (StartFocusTimer != null)
            {
                StartFocusTimer(totalSec, "Focus Mode (" + minutes + "m)");
            }

            return "Focus mode started for " + minutes + " minutes. Distractions and notifications are muted. Let's get to work.";
        }

        // =====================================================================
        // 2. DAILY PUSH-TO-TALK MEMORY
        // =====================================================================
        private sealed class HistoryTurn
        {
            public string t { get; set; } // ISO 8601 timestamp
            public string u { get; set; } // user transcript
            public string a { get; set; } // assistant answer
        }

        private static string HistoryFilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flinch");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "history.jsonl");
            }
        }

        public static void RecordTurn(string transcript, string reply)
        {
            if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(reply)) return;
            try
            {
                var entry = new HistoryTurn
                {
                    t = DateTime.UtcNow.ToString("o"),
                    u = transcript.Trim(),
                    a = reply.Trim()
                };
                string line = Json.Serialize(entry);
                lock (_ctxLock)
                {
                    File.AppendAllText(HistoryFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        private static List<HistoryTurn> LoadRecentHistory()
        {
            var list = new List<HistoryTurn>();
            try
            {
                string file = HistoryFilePath;
                if (!File.Exists(file)) return list;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = Math.Max(0, lines.Length - 100); i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (string.IsNullOrEmpty(ln)) continue;
                    try
                    {
                        var t = Json.Deserialize<HistoryTurn>(ln);
                        if (t != null) list.Add(t);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static string TryMemory(string transcript, string low)
        {
            bool isMemoryQuery = low.Contains("what did we talk about") || low.Contains("what did we discuss")
                                 || low.Contains("what did i ask") || low.Contains("what was my previous")
                                 || low.Contains("what was the last thing") || low.Contains("recap our")
                                 || low.Contains("recap yesterday") || low.Contains("what did i just say");
            if (!isMemoryQuery) return null;

            var history = LoadRecentHistory();
            if (history.Count == 0)
            {
                return "I don't have any past conversations recorded in your local memory yet.";
            }

            // A. "What did we talk about yesterday?"
            if (low.Contains("yesterday"))
            {
                DateTime yesterday = DateTime.UtcNow.Date.AddDays(-1);
                var yesterdayTurns = new List<HistoryTurn>();
                foreach (var turn in history)
                {
                    DateTime parsed;
                    if (DateTime.TryParse(turn.t, out parsed))
                    {
                        if (parsed.ToUniversalTime().Date == yesterday) yesterdayTurns.Add(turn);
                    }
                }

                if (yesterdayTurns.Count == 0)
                {
                    return "We didn't have any recorded conversations yesterday. But I'm ready to help you today.";
                }

                var topics = new List<string>();
                foreach (var y in yesterdayTurns)
                {
                    string q = y.u.TrimEnd('.', '?', '!');
                    if (q.Length > 40) q = q.Substring(0, 40) + "...";
                    if (!topics.Contains(q)) topics.Add(q);
                    if (topics.Count >= 3) break;
                }

                if (topics.Count == 1)
                {
                    return "Yesterday, we talked about " + topics[0] + ".";
                }
                return "Yesterday, we discussed " + string.Join(", ", topics.Take(topics.Count - 1).ToArray()) + ", and " + topics.Last() + ".";
            }

            // B. "What did I just say?" / "What was my previous question?"
            if (low.Contains("just say") || low.Contains("previous question") || low.Contains("last thing") || low.Contains("earlier"))
            {
                // Grab the second to last or last entry
                HistoryTurn prev = history.Count > 1 ? history[history.Count - 2] : history[history.Count - 1];
                return "Earlier, you asked: \"" + prev.u + "\", and I answered: \"" + prev.a + "\".";
            }

            // C. General recap
            int count = Math.Min(3, history.Count);
            var recents = history.Skip(history.Count - count).Select(h => h.u).ToArray();
            return "Recently, we talked about: " + string.Join(", and ", recents) + ".";
        }

        // =====================================================================
        // 3. VOICE AND EMOTION PICKER PER MOOD
        // =====================================================================
        private static string TryVoicePicker(string transcript, string low)
        {
            Match vMatch = Regex.Match(low, @"\b(?:switch|change|set|use)\s+(?:the\s+)?voice\s+(?:to|as)\s+([a-z]+)");
            if (!vMatch.Success)
            {
                if (low == "auto voice" || low == "use auto voice" || low == "enable auto voice")
                {
                    vMatch = Regex.Match("switch voice to auto", @"switch voice to ([a-z]+)");
                }
            }

            if (!vMatch.Success) return null;

            string target = vMatch.Groups[1].Value.ToLowerInvariant().Trim();
            string[] validVoices = new[] { "troy", "daniel", "autumn", "zeus", "orion", "arcas", "asteria", "luna", "athena", "hera", "auto" };

            if (!validVoices.Contains(target))
            {
                return "I support Troy, Daniel, Autumn, Zeus, Orion, Asteria, Luna, and Auto mood switching. Which one would you prefer?";
            }

            if (SetPreferredVoice != null) SetPreferredVoice(target);

            if (target == "auto")
            {
                return "Automatic voice mode enabled. I'll switch between expressive Orpheus for casual chat and crisp Deepgram for technical reading.";
            }

            return "Voice switched to " + Capitalize(target) + ". How does this sound?";
        }

        // =====================================================================
        // 4. QUICK TRANSLATE AND READ-ALOUD OF SCREEN
        // =====================================================================
        private static string TryScreenTranslate(string transcript, string low)
        {
            Match m = Regex.Match(low, @"translate\s+(?:this\s+)?(?:page|screen|window|document|article|text\s+on\s+screen)\s+(?:in)?to\s+([a-z]+)");
            if (!m.Success)
            {
                m = Regex.Match(low, @"read\s+(?:this\s+)?(?:page|screen)\s+in\s+([a-z]+)");
            }
            if (!m.Success) return null;

            string targetLang = Capitalize(m.Groups[1].Value.Trim());
            Task.Run(delegate
            {
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
                    CapturedFrame frame = cap.CaptureBounds(pBounds, "Display");
                    if (frame == null || frame.JpegBytes == null || frame.JpegBytes.Length == 0)
                    {
                        SpeakSafe("I could not capture your screen to translate.");
                        return;
                    }

                    string base64 = Convert.ToBase64String(frame.JpegBytes);
                    frame.Dispose();

                    string endpoint = FlinchEnv.SupabaseUrl.TrimEnd('/') + "/functions/v1/flinch-voice";
                    var payload = new Dictionary<string, object>
                    {
                        { "action", "translate_screen" },
                        { "screen", base64 },
                        { "target_language", targetLang }
                    };

                    using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                    {
                        if (SupabaseAuthClient.CurrentSession != null)
                        {
                            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.AccessToken);
                        }
                        req.Headers.TryAddWithoutValidation("apikey", FlinchEnv.SupabaseAnonKey);
                        req.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");

                        using (var resp = Http.SendAsync(req).GetAwaiter().GetResult())
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                SpeakSafe("Sorry, I could not complete the translation right now.");
                                return;
                            }
                            string respBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            var dict = Json.Deserialize<Dictionary<string, object>>(respBody);
                            object resObj;
                            if (dict != null && dict.TryGetValue("result", out resObj) && resObj != null)
                            {
                                string translated = resObj.ToString();
                                SpeakSafe(translated);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("Screen translate error: " + ex.Message);
                    SpeakSafe("Sorry, screen translation encountered an error.");
                }
            });

            return "Translating your screen into " + targetLang + " right now.";
        }

        // =====================================================================
        // 5. PROACTIVE NUDGES: ERROR DIALOGS
        // =====================================================================
        private static DateTime _lastNudgeUtc = DateTime.UtcNow;
        private static string _lastNudgedErrorTitle = "";
        private static Timer _nudgeTimer;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private static void WatchNudges()
        {
            try
            {
                if (IsFocusModeActive) return; // Muted during Pomodoro focus mode!
                if ((DateTime.UtcNow - _lastNudgeUtc).TotalSeconds < 300) return; // At most one nudge per 5 minutes

                // Check for error dialogs on screen
                string errorFound = null;
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    var sb = new StringBuilder(256);
                    GetWindowText(hWnd, sb, 256);
                    string title = sb.ToString().Trim();
                    if (title.Length > 2)
                    {
                        string lowT = title.ToLowerInvariant();
                        if (lowT.Contains("error") || lowT.Contains("exception") || lowT.Contains("crash")
                            || lowT.Contains("failed to") || lowT.Contains("fatal error") || lowT.Contains("unhandled exception"))
                        {
                            errorFound = title;
                            return false; // Stop enumeration
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (!string.IsNullOrEmpty(errorFound) && errorFound != _lastNudgedErrorTitle)
                {
                    _lastNudgeUtc = DateTime.UtcNow;
                    _lastNudgedErrorTitle = errorFound;
                    if (ShowNudgeAlert != null)
                    {
                        ShowNudgeAlert("Error Dialog Detected", "I noticed: \"" + errorFound + "\". Want me to help analyze it?");
                    }
                    return;
                }
            }
            catch { }
        }

        // =====================================================================
        // 6. CLIPBOARD ACTIONS
        // =====================================================================
        private static string TryClipboard(string transcript, string low)
        {
            bool mentionsClip = low.Contains("copied") || low.Contains("clipboard") || low.Contains("what i just copied")
                                || low.Contains("the text i copied") || low.Contains("this text i copied");
            if (!mentionsClip) return null;

            string instruction = null;
            bool speakResult = false;

            Match tr = Regex.Match(low, @"translate\s+(?:it\s+|this\s+|that\s+|what\s+i\s+copied\s+|my\s+clipboard\s+)?(?:in)?to\s+([a-z]+)");
            if (tr.Success)
            {
                instruction = "Translate the text into " + Capitalize(tr.Groups[1].Value) + ".";
            }
            else if (Regex.IsMatch(low, @"\b(summari[sz]e|summary|tl;?dr|sum up)\b"))
            {
                instruction = "Summarize this in 2 to 3 short sentences.";
                speakResult = true;
            }
            else if (Regex.IsMatch(low, @"\b(fix|proofread|correct)\b.*\b(grammar|spelling|typos|mistakes)\b") || low.Contains("proofread"))
            {
                instruction = "Fix all spelling and grammar mistakes. Keep the same tone and meaning.";
            }
            else if (Regex.IsMatch(low, @"\b(shorten|shorter|make it short|condense)\b"))
            {
                instruction = "Make this much shorter while keeping the key meaning.";
            }
            else if (Regex.IsMatch(low, @"\b(bullet|bullets|bullet points)\b"))
            {
                instruction = "Convert this into concise bullet points using the '-' character.";
            }
            else if (Regex.IsMatch(low, @"\breply\b") || low.Contains("respond to") || low.Contains("write back"))
            {
                instruction = "Write a polite, concise reply to this message. Output only the reply text.";
            }
            else if (Regex.IsMatch(low, @"\b(rewrite|rephrase|reword|make it)\b"))
            {
                string tone = "clearer and more natural";
                if (low.Contains("professional") || low.Contains("formal")) tone = "professional and formal";
                else if (low.Contains("casual") || low.Contains("friendly")) tone = "casual and friendly";
                else if (low.Contains("polite")) tone = "polite";
                else if (low.Contains("confident")) tone = "confident";
                instruction = "Rewrite this to be " + tone + ". Keep the meaning.";
            }
            else if (Regex.IsMatch(low, @"\b(explain|simplify|what does this mean)\b"))
            {
                instruction = "Explain this in simple words in 2 to 3 sentences.";
                speakResult = true;
            }

            if (instruction == null) return null;

            string clip = GetClipboardText();
            if (string.IsNullOrWhiteSpace(clip))
            {
                return "Your clipboard is empty. Copy some text first, then ask me again.";
            }

            string instr = instruction;
            bool say = speakResult;
            Task.Run(delegate
            {
                string result = TransformText(clip, instr);
                if (string.IsNullOrWhiteSpace(result))
                {
                    SpeakSafe("Sorry, I couldn't process your clipboard right now.");
                    return;
                }
                SetClipboardText(result);
                if (say)
                {
                    string preview = result.Length > 260 ? result.Substring(0, 260) + "..." : result;
                    SpeakSafe(preview.Replace("\r", " ").Replace("\n", " ") + " It's on your clipboard too.");
                }
                else
                {
                    SpeakSafe("Done. The new version is on your clipboard, just paste it.");
                }
            });

            return "On it.";
        }

        private static string TransformText(string text, string instruction)
        {
            try
            {
                if (SupabaseAuthClient.CurrentSession == null) return null;
                string endpoint = FlinchEnv.SupabaseUrl.TrimEnd('/') + "/functions/v1/flinch-voice";
                var payload = new Dictionary<string, object>
                {
                    { "action", "transform_text" },
                    { "text_input", text },
                    { "instruction", instruction }
                };
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.AccessToken);
                    req.Headers.TryAddWithoutValidation("apikey", FlinchEnv.SupabaseAnonKey);
                    req.Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json");
                    using (var resp = Http.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (!resp.IsSuccessStatusCode) return null;
                        var dict = Json.Deserialize<Dictionary<string, object>>(body);
                        object v;
                        if (dict != null && dict.TryGetValue("result", out v) && v != null) return v.ToString();
                    }
                }
            }
            catch (Exception ex) { FlinchEnv.Log("TransformText error: " + ex.Message); }
            return null;
        }

        private static string GetClipboardText()
        {
            string text = "";
            RunSta(delegate
            {
                try { if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText(); }
                catch { }
            });
            return text;
        }

        private static void SetClipboardText(string text)
        {
            RunSta(delegate
            {
                for (int i = 0; i < 5; i++)
                {
                    try { System.Windows.Clipboard.SetText(text); return; }
                    catch { Thread.Sleep(80); }
                }
            });
        }

        private static void RunSta(Action a)
        {
            var t = new Thread(new ThreadStart(delegate { a(); }));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(2500);
        }

        // =====================================================================
        // 7. SMART REMINDERS
        // =====================================================================
        private sealed class ContextReminder
        {
            public string App { get; set; }
            public string Process { get; set; }
            public string Text { get; set; }
            public bool WasRunning { get; set; }
        }

        private static readonly List<ContextReminder> _contextReminders = new List<ContextReminder>();
        private static readonly object _ctxLock = new object();
        private static Timer _watchTimer;

        private static string TryReminder(string transcript, string low)
        {
            if (!(low.StartsWith("remind me") || low.Contains("remind me "))) return null;

            // a) "remind me when I open <app> to <text>"
            Match ctx = Regex.Match(low, @"remind me\s+(?:when|once|as soon as|the next time)\s+(?:i\s+)?(?:open|launch|start|opened|use)\s+(?:up\s+)?(.+?)\s+(?:to|that|about)\s+(.+)$");
            if (ctx.Success)
            {
                string app = ctx.Groups[1].Value.Trim();
                string what = ToSecondPerson(ctx.Groups[2].Value.Trim());
                string proc = MapAppToProcess(app);
                bool running = IsProcessRunning(proc);
                lock (_ctxLock)
                {
                    _contextReminders.Add(new ContextReminder { App = app, Process = proc, Text = what, WasRunning = running });
                }
                SaveContextReminders();
                return "Got it. I'll remind you to " + what + " the next time you open " + Capitalize(app) + ".";
            }

            // b) "remind me in 10 minutes to <text>"
            Match rel = Regex.Match(low, @"remind me\s+in\s+(\d+)\s*(second|sec|minute|min|hour|hr)s?\s+(?:to|that|about)\s+(.+)$");
            if (rel.Success)
            {
                int n = int.Parse(rel.Groups[1].Value);
                string unit = rel.Groups[2].Value;
                int secs = unit.StartsWith("sec") ? n : (unit.StartsWith("min") ? n * 60 : n * 3600);
                string what = ToSecondPerson(rel.Groups[3].Value.Trim());
                if (StartReminderInSeconds != null) StartReminderInSeconds(secs, what);
                return "Okay, I'll remind you in " + n + " " + (unit.StartsWith("sec") ? "second" : (unit.StartsWith("min") ? "minute" : "hour")) + (n == 1 ? "" : "s") + " to " + what + ".";
            }

            // c) "remind me at 5:30 pm to <text>"
            Match abs = Regex.Match(low, @"remind me\s+at\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\s+(?:to|that|about)\s+(.+)$");
            if (abs.Success)
            {
                int hr = int.Parse(abs.Groups[1].Value);
                int min = abs.Groups[2].Success ? int.Parse(abs.Groups[2].Value) : 0;
                string ampm = abs.Groups[3].Success ? abs.Groups[3].Value.ToLowerInvariant() : "";

                if (ampm == "pm" && hr < 12) hr += 12;
                if (ampm == "am" && hr == 12) hr = 0;
                if (ampm == "" && hr < 8) hr += 12; // "at 5" -> 5 pm

                DateTime now = DateTime.Now;
                DateTime target = new DateTime(now.Year, now.Month, now.Day, hr, min, 0);
                if (target <= now) target = target.AddDays(1);

                int secs = (int)Math.Max(5, (target - now).TotalSeconds);
                string what = ToSecondPerson(abs.Groups[4].Value.Trim());
                if (StartReminderInSeconds != null) StartReminderInSeconds(secs, what);
                return "Scheduled. I'll remind you at " + target.ToString("h:mm tt") + " to " + what + ".";
            }

            return null;
        }

        private static string ToSecondPerson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Regex.Replace(s, @"\bmy\b", "your", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bme\b", "you", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmyself\b", "yourself", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bi\b", "you", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        private static string MapAppToProcess(string app)
        {
            string low = (app ?? "").ToLowerInvariant().Trim();
            if (low.Contains("chrome")) return "chrome";
            if (low.Contains("edge")) return "msedge";
            if (low.Contains("firefox")) return "firefox";
            if (low.Contains("code") || low.Contains("vs code") || low.Contains("vscode")) return "Code";
            if (low.Contains("visual studio")) return "devenv";
            if (low.Contains("notepad")) return "notepad";
            if (low.Contains("spotify")) return "Spotify";
            if (low.Contains("discord")) return "Discord";
            if (low.Contains("slack")) return "slack";
            if (low.Contains("terminal") || low.Contains("powershell")) return "powershell";
            if (low.Contains("cmd") || low.Contains("command prompt")) return "cmd";
            if (low.Contains("word")) return "WINWORD";
            if (low.Contains("excel")) return "EXCEL";
            if (low.Contains("powerpoint")) return "POWERPNT";
            if (low.Contains("steam")) return "steam";
            if (low.Contains("outlook")) return "OUTLOOK";
            if (low.Contains("explorer") || low.Contains("files") || low.Contains("file manager")) return "explorer";
            return low.Replace(" ", "");
        }

        private static bool IsProcessRunning(string proc)
        {
            try { return Process.GetProcessesByName(proc).Length > 0; }
            catch { return false; }
        }

        private static void WatchContextReminders()
        {
            List<ContextReminder> toFire = new List<ContextReminder>();
            lock (_ctxLock)
            {
                for (int i = _contextReminders.Count - 1; i >= 0; i--)
                {
                    var r = _contextReminders[i];
                    bool running = IsProcessRunning(r.Process);
                    if (running && !r.WasRunning)
                    {
                        toFire.Add(r);
                        _contextReminders.RemoveAt(i);
                    }
                    else
                    {
                        r.WasRunning = running;
                    }
                }
            }

            if (toFire.Count > 0)
            {
                SaveContextReminders();
                foreach (var item in toFire)
                {
                    string msg = "You just opened " + Capitalize(item.App) + ". Don't forget to " + item.Text + ".";
                    if (FireContextReminder != null) FireContextReminder(msg);
                }
            }
        }

        private static string ReminderFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flinch");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "context_reminders.json");
            }
        }

        private static void SaveContextReminders()
        {
            try
            {
                lock (_ctxLock)
                {
                    string json = Json.Serialize(_contextReminders);
                    File.WriteAllText(ReminderFile, json, Encoding.UTF8);
                }
            }
            catch (Exception ex) { FlinchEnv.Log("SaveContextReminders error: " + ex.Message); }
        }

        private static void LoadContextReminders()
        {
            try
            {
                string f = ReminderFile;
                if (!File.Exists(f)) return;
                string json = File.ReadAllText(f, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                var list = Json.Deserialize<List<ContextReminder>>(json);
                if (list != null)
                {
                    lock (_ctxLock)
                    {
                        _contextReminders.Clear();
                        foreach (var it in list)
                        {
                            it.WasRunning = IsProcessRunning(it.Process);
                            _contextReminders.Add(it);
                        }
                    }
                }
            }
            catch (Exception ex) { FlinchEnv.Log("LoadContextReminders error: " + ex.Message); }
        }

        // =====================================================================
        // 8. VOICE FILE MANAGEMENT
        // =====================================================================
        private static string TryFiles(string transcript, string low)
        {
            // a) "zip my <folder>"
            Match zip = Regex.Match(low, @"\b(zip|compress|archive)\b\s+(?:my\s+)?(.+?)(?:\s+folder|\s+directory)?$");
            if (zip.Success)
            {
                string target = zip.Groups[2].Value.Trim();
                string resolvedFolder;
                string folderLabel;
                if (ResolveKnownFolder(target, out resolvedFolder, out folderLabel))
                {
                    Task.Run(delegate { ZipFolder(resolvedFolder, folderLabel); });
                    return "Zipping your " + folderLabel + " folder now. I'll save it to your Desktop.";
                }
            }

            // b) "open latest download" / "open my last screenshot"
            if (low.Contains("open") && (low.Contains("latest download") || low.Contains("last download") || low.Contains("recent download")))
            {
                string newest = NewestFile(false);
                if (newest == null) return "You don't have any recent downloads.";
                RevealInExplorer(newest);
                return "Here is your latest download: " + Path.GetFileName(newest) + ".";
            }
            if (low.Contains("open") && (low.Contains("latest screenshot") || low.Contains("last screenshot") || low.Contains("recent screenshot")))
            {
                string newest = NewestFile(true);
                if (newest == null) return "No recent screenshots found.";
                RevealInExplorer(newest);
                return "Here is your latest screenshot: " + Path.GetFileName(newest) + ".";
            }

            // c) "rename latest download to <new name>"
            Match ren = Regex.Match(low, @"rename\s+(?:my\s+)?(?:latest|last|recent)?\s*(?:download|file)\s+(?:in)?to\s+(.+)$");
            if (ren.Success)
            {
                string newName = ren.Groups[1].Value.Trim().Trim('"', '\'', '.');
                string target = NewestFile(false);
                if (target == null) return "No recent download found to rename.";
                try
                {
                    string ext = Path.GetExtension(target);
                    if (!newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
                    string dest = Path.Combine(Path.GetDirectoryName(target), newName);
                    if (File.Exists(dest)) return "A file with that name already exists in your Downloads.";
                    File.Move(target, dest);
                    RevealInExplorer(dest);
                    return "Renamed your latest download to " + newName + ".";
                }
                catch (Exception ex)
                {
                    return "Sorry, I couldn't rename the file. " + ex.Message;
                }
            }

            // d) "find my <name> essay / file / document"
            Match find = Regex.Match(low, @"\b(find|locate|search for|where is|where's)\b\s+(?:my\s+)?(.+?)(?:\s+file|\s+document|\s+essay|\s+notes)?$");
            if (find.Success)
            {
                string query = find.Groups[2].Value.Trim();
                if (query.Length < 3 || query == "file" || query == "document" || query == "it" || query == "folder") return null;
                Task.Run(delegate { FindAndReveal(query); });
                return "Searching for your " + query + " file now.";
            }

            return null;
        }

        private static IEnumerable<string> SearchRoots()
        {
            var roots = new List<string>();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(profile, "Downloads"));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flinch"));
            return roots.Where(Directory.Exists);
        }

        private static void FindAndReveal(string query)
        {
            try
            {
                string bestMatch = null;
                DateTime bestDate = DateTime.MinValue;
                string qClean = query.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");

                foreach (string root in SearchRoots())
                {
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                string name = Path.GetFileName(file);
                                if (name.StartsWith(".") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                                string nClean = Path.GetFileNameWithoutExtension(file).ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
                                if (nClean.Contains(qClean))
                                {
                                    DateTime dt = File.GetLastWriteTime(file);
                                    if (dt > bestDate)
                                    {
                                        bestDate = dt;
                                        bestMatch = file;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (bestMatch != null)
                {
                    RevealInExplorer(bestMatch);
                    SpeakSafe("Found it. Here is " + Path.GetFileName(bestMatch) + ".");
                }
                else
                {
                    SpeakSafe("I couldn't find any file matching " + query + " in your Documents, Desktop, or Downloads.");
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("FindAndReveal error: " + ex.Message);
                SpeakSafe("Sorry, I ran into an error while searching for your file.");
            }
        }

        private static bool ResolveKnownFolder(string low, out string folder, out string label)
        {
            folder = null;
            label = null;
            low = (low ?? "").ToLowerInvariant().Trim();

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (low.Contains("flinch"))
            {
                string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flinch");
                if (Directory.Exists(candidate)) { folder = candidate; label = "Flinch"; return true; }
            }
            if (low.Contains("desktop"))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                label = "Desktop";
                return true;
            }
            if (low.Contains("download"))
            {
                folder = Path.Combine(profile, "Downloads");
                label = "Downloads";
                return true;
            }
            if (low.Contains("document"))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                label = "Documents";
                return true;
            }
            if (low.Contains("picture") || low.Contains("screenshot"))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                label = "Pictures";
                return true;
            }
            return false;
        }

        private static void ZipFolder(string folder, string label)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    SpeakSafe("I couldn't find the " + label + " folder.");
                    return;
                }

                long total = 0;
                foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                    if (total > 2L * 1024 * 1024 * 1024)
                    {
                        SpeakSafe("The " + label + " folder is over 2 gigabytes, which is too large for me to zip.");
                        return;
                    }
                }

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string destName = label + " " + DateTime.Now.ToString("yyyy-MM-dd") + ".zip";
                string dest = Path.Combine(desktop, destName);
                int n = 2;
                while (File.Exists(dest)) { dest = Path.Combine(desktop, label + " " + DateTime.Now.ToString("yyyy-MM-dd") + " (" + n + ").zip"); n++; }

                string tmp = Path.Combine(Path.GetTempPath(), "flinch_zip_" + Guid.NewGuid().ToString("N") + ".zip");
                ZipFile.CreateFromDirectory(folder, tmp, CompressionLevel.Optimal, false);
                File.Move(tmp, dest);

                RevealInExplorer(dest);
                SpeakSafe("Done. I zipped your " + label + " folder to the Desktop.");
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("ZipFolder error: " + ex.Message);
                SpeakSafe("Sorry, I couldn't zip that folder. " + ex.Message);
            }
        }

        private static string NewestFile(bool screenshot)
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dir = screenshot
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots")
                    : Path.Combine(profile, "Downloads");
                if (!Directory.Exists(dir)) return null;
                return new DirectoryInfo(dir).GetFiles()
                    .Where(f => !f.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                                && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                                && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                && !f.Attributes.HasFlag(FileAttributes.Hidden))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static void RevealInExplorer(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false }); }
            catch { }
        }

        // ---------------------------------------------------------------------
        private static void SpeakSafe(string text)
        {
            try { if (Speak != null) Speak(text); } catch { }
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }
    }

    // =========================================================================
    // Offline Fallback Brain: Handles local timers, clock, date, pomodoro,
    // and offline system tasks via System.Speech.Recognition & SpeechSynthesizer
    // when network is down or cloud brain is unreachable.
    // =========================================================================
    internal static class OfflineBrain
    {
        public static bool TryProcess(byte[] wavBytes, out string transcript, out string answer)
        {
            transcript = "";
            answer = "";
            if (wavBytes == null || wavBytes.Length <= 44) return false;

            try
            {
                using (var sre = new System.Speech.Recognition.SpeechRecognitionEngine())
                using (var ms = new MemoryStream(wavBytes))
                {
                    var choices = new System.Speech.Recognition.Choices();
                    choices.Add(new string[] {
                        "what time is it", "tell me the time", "what is the time", "time",
                        "what is today's date", "what is the date", "what day is it", "today's date",
                        "start pomodoro", "start focus mode", "focus for 25 minutes", "stop focus", "stop pomodoro",
                        "battery", "battery status", "system status",
                        "lock my pc", "lock the computer", "lock pc",
                        "set a timer for 1 minute", "set a timer for 5 minutes", "set a timer for 10 minutes",
                        "set a timer for 15 minutes", "set a timer for 25 minutes", "set a timer for 30 minutes"
                    });
                    var gb = new System.Speech.Recognition.GrammarBuilder(choices);
                    var g = new System.Speech.Recognition.Grammar(gb);
                    sre.LoadGrammar(g);

                    try
                    {
                        var dict = new System.Speech.Recognition.DictationGrammar();
                        sre.LoadGrammar(dict);
                    }
                    catch { }

                    sre.SetInputToWaveStream(ms);
                    var reco = sre.Recognize(TimeSpan.FromSeconds(3));
                    if (reco != null && !string.IsNullOrWhiteSpace(reco.Text) && reco.Confidence >= 0.15f)
                    {
                        transcript = reco.Text;
                        string low = transcript.ToLowerInvariant().Trim();

                        string skillResp = FlinchSkills.TryHandle(transcript, low);
                        if (skillResp != null)
                        {
                            answer = skillResp;
                            return true;
                        }

                        if (low.Contains("time"))
                        {
                            answer = "The time is " + DateTime.Now.ToString("h:mm tt") + ".";
                            return true;
                        }
                        if (low.Contains("date") || low.Contains("day"))
                        {
                            answer = "Today is " + DateTime.Now.ToString("dddd, MMMM d, yyyy") + ".";
                            return true;
                        }
                        if (low.Contains("battery"))
                        {
                            var p = System.Windows.Forms.SystemInformation.PowerStatus;
                            answer = string.Format("Battery is at {0} percent.", (int)(p.BatteryLifePercent * 100));
                            return true;
                        }
                        if (low.Contains("lock"))
                        {
                            Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true });
                            answer = "Locking your computer.";
                            return true;
                        }

                        answer = "You're currently offline. Your local timers, clock, and focus mode are active.";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("OfflineBrain error: " + ex.Message);
            }
            return false;
        }
    }
}
