using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FlinchApp
{
    // =========================================================================
    // Flinch Skills: local voice-command skills that run on the desktop.
    //   1. Clipboard actions   ("summarize what I copied", "translate what I copied to French")
    //   2. Smart reminders     ("remind me at 5 pm to call mom", "remind me when I open Chrome to ...")
    //   3. Voice file work     ("find my sri lanka essay", "zip my flinch folder", "rename my latest download to ...")
    // NOTE: compiled with the .NET Framework 4 C# compiler (C# 5): no ?., no $"", no out var.
    // =========================================================================
    internal static class FlinchSkills
    {
        // Hooks set by the main window
        public static Action<string> Speak;                         // speak when the pill is idle
        public static Action<int, string> StartReminderInSeconds;   // seconds, reminder text
        public static Action<string> FireContextReminder;           // reminder text -> alert + speech

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
            r = TryReminder(transcript, low);       if (r != null) return r;
            r = TryClipboard(transcript, low);      if (r != null) return r;
            r = TryFiles(transcript, low);          if (r != null) return r;
            return null;
        }

        public static void Start()
        {
            LoadContextReminders();
            _watchTimer = new Timer(delegate { WatchContextReminders(); }, null, 4000, 3000);
        }

        // =====================================================================
        // 1. CLIPBOARD ACTIONS
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
        // 2. SMART REMINDERS
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
            Match at = Regex.Match(low, @"remind me\s+(?:at|by)\s+(\d{1,2})(?::(\d{2}))?\s*(a\.?m\.?|p\.?m\.?)?\s*(?:to|that|about)\s+(.+)$");
            if (at.Success)
            {
                int hour = int.Parse(at.Groups[1].Value);
                int min = at.Groups[2].Success ? int.Parse(at.Groups[2].Value) : 0;
                string mer = at.Groups[3].Success ? at.Groups[3].Value.Replace(".", "") : "";
                string what = ToSecondPerson(at.Groups[4].Value.Trim());
                if (hour > 23 || min > 59) return null;

                DateTime now = DateTime.Now;
                DateTime target = DateTime.MinValue;
                if (mer == "pm" && hour < 12) hour += 12;
                else if (mer == "am" && hour == 12) hour = 0;

                if (mer.Length > 0 || hour > 12)
                {
                    target = new DateTime(now.Year, now.Month, now.Day, hour, min, 0);
                    if (target <= now) target = target.AddDays(1);
                }
                else
                {
                    // No am/pm: pick the soonest upcoming of the two readings
                    int h12 = hour % 12;
                    DateTime amT = new DateTime(now.Year, now.Month, now.Day, h12, min, 0);
                    DateTime pmT = new DateTime(now.Year, now.Month, now.Day, h12 + 12, min, 0);
                    if (amT <= now) amT = amT.AddDays(1);
                    if (pmT <= now) pmT = pmT.AddDays(1);
                    target = amT < pmT ? amT : pmT;
                }

                int secs = (int)Math.Max(5, (target - now).TotalSeconds);
                if (StartReminderInSeconds != null) StartReminderInSeconds(secs, what);
                string when = target.ToString("h:mm tt").Replace(":00 ", " ");
                string day = target.Date == now.Date ? "" : " tomorrow";
                return "Okay, I'll remind you at " + when + day + " to " + what + ".";
            }

            return null;
        }

        // "save my work" -> "save your work" so reminders read naturally when spoken back
        private static string ToSecondPerson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Regex.Replace(s, @"\bmyself\b", "yourself", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmy\b", "your", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bi'm\b", "you're", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bi am\b", "you are", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bme\b", "you", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bi\b", "you", RegexOptions.IgnoreCase);
            return s;
        }

        private static string MapAppToProcess(string app)
        {
            string a = (app ?? "").ToLowerInvariant().Trim();
            switch (a)
            {
                case "chrome": case "google chrome": return "chrome";
                case "edge": case "microsoft edge": return "msedge";
                case "brave": case "brave browser": return "brave";
                case "firefox": return "firefox";
                case "spotify": return "Spotify";
                case "discord": return "Discord";
                case "code": case "vs code": case "vscode": case "visual studio code": return "Code";
                case "notepad": return "notepad";
                case "word": case "microsoft word": return "WINWORD";
                case "excel": case "microsoft excel": return "EXCEL";
                case "powerpoint": return "POWERPNT";
                case "whatsapp": return "WhatsApp";
                case "telegram": return "Telegram";
                case "steam": return "steam";
                case "terminal": case "windows terminal": return "WindowsTerminal";
                case "calculator": return "CalculatorApp";
                case "explorer": case "file explorer": return "explorer";
                case "teams": case "microsoft teams": return "ms-teams";
                case "slack": return "slack";
                case "zoom": return "Zoom";
                case "obs": case "obs studio": return "obs64";
            }
            return Regex.Replace(a, @"[^a-z0-9]", "");
        }

        private static bool IsProcessRunning(string proc)
        {
            try { return Process.GetProcessesByName(proc).Length > 0; }
            catch { return false; }
        }

        private static void WatchContextReminders()
        {
            List<ContextReminder> fire = new List<ContextReminder>();
            try
            {
                lock (_ctxLock)
                {
                    for (int i = _contextReminders.Count - 1; i >= 0; i--)
                    {
                        var cr = _contextReminders[i];
                        bool running = IsProcessRunning(cr.Process);
                        if (running && !cr.WasRunning)
                        {
                            fire.Add(cr);
                            _contextReminders.RemoveAt(i);
                        }
                        else
                        {
                            cr.WasRunning = running;
                        }
                    }
                }
            }
            catch { }

            if (fire.Count > 0)
            {
                SaveContextReminders();
                foreach (var cr in fire)
                {
                    if (FireContextReminder != null) FireContextReminder(cr.Text);
                }
            }
        }

        private static string ReminderFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flinch");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "context_reminders.json");
            }
        }

        private static void SaveContextReminders()
        {
            try
            {
                List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
                lock (_ctxLock)
                {
                    foreach (var cr in _contextReminders)
                    {
                        rows.Add(new Dictionary<string, string> { { "app", cr.App }, { "process", cr.Process }, { "text", cr.Text } });
                    }
                }
                File.WriteAllText(ReminderFile, Json.Serialize(rows), Encoding.UTF8);
            }
            catch { }
        }

        private static void LoadContextReminders()
        {
            try
            {
                if (!File.Exists(ReminderFile)) return;
                var rows = Json.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(ReminderFile, Encoding.UTF8));
                if (rows == null) return;
                lock (_ctxLock)
                {
                    foreach (var row in rows)
                    {
                        string proc;
                        if (!row.TryGetValue("process", out proc) || string.IsNullOrWhiteSpace(proc)) continue;
                        string app; row.TryGetValue("app", out app);
                        string text; row.TryGetValue("text", out text);
                        _contextReminders.Add(new ContextReminder { App = app, Process = proc, Text = text, WasRunning = IsProcessRunning(proc) });
                    }
                }
            }
            catch { }
        }

        // =====================================================================
        // 3. VOICE FILE WORK
        // =====================================================================
        private static string TryFiles(string transcript, string low)
        {
            // --- open / show latest download or screenshot ---
            Match latest = Regex.Match(low, @"\b(?:open|show|reveal)\s+(?:me\s+)?(?:my\s+|the\s+)?(?:latest|last|most recent|newest)\s+(download|downloaded file|screenshot)");
            if (latest.Success)
            {
                bool shot = latest.Groups[1].Value.StartsWith("screen");
                string path = NewestFile(shot);
                if (path == null) return shot ? "I couldn't find any screenshots." : "Your Downloads folder is empty.";
                RevealInExplorer(path);
                return "Here's your latest " + (shot ? "screenshot" : "download") + ": " + Path.GetFileName(path) + ".";
            }

            // --- rename latest download ---
            Match ren = Regex.Match(low, @"\brename\s+(?:my\s+|the\s+)?(?:latest|last|most recent|newest)\s+(download|downloaded file|file|screenshot)\s+(?:in\s+(?:my\s+)?downloads\s+)?to\s+(.+)$");
            if (ren.Success)
            {
                bool shot = ren.Groups[1].Value.StartsWith("screen");
                string path = NewestFile(shot);
                if (path == null) return "I couldn't find a file to rename.";
                string newBase = Regex.Replace(ren.Groups[2].Value.Trim(), @"[\\/:*?""<>|]", "").Trim();
                if (newBase.Length == 0) return "What should I rename it to?";
                string ext = Path.GetExtension(path);
                string dest = Path.Combine(Path.GetDirectoryName(path), newBase + ext);
                if (File.Exists(dest)) return "A file called " + newBase + " already exists there, so I didn't rename anything.";
                try { File.Move(path, dest); }
                catch (Exception ex) { return "I couldn't rename it: " + ex.Message; }
                RevealInExplorer(dest);
                return "Renamed it to " + newBase + ext + ".";
            }

            // --- zip a known folder ---
            if (Regex.IsMatch(low, @"\b(zip|compress|archive)\b"))
            {
                string folder, label;
                if (ResolveKnownFolder(low, out folder, out label))
                {
                    Task.Run(delegate { ZipFolder(folder, label); });
                    return "Zipping your " + label + " folder now. I'll tell you when it's done.";
                }
                if (low.Contains("folder")) return "I can zip your Flinch, Desktop, Documents, or Downloads folder. Which one?";
                return null;
            }

            // --- find a file ---
            Match find = Regex.Match(low, @"^(?:please\s+)?(?:can you\s+)?(?:find|locate|search for|look for|where(?:'s| is))\s+(?:me\s+)?(?:my\s+|the\s+)?(.+)$");
            if (find.Success)
            {
                string q = find.Groups[1].Value.Trim();
                bool fileish = Regex.IsMatch(low, @"\b(file|essay|document|report|pdf|folder|download|screenshot|photo|image|picture|spreadsheet|presentation|doc|docx|txt|story|paper)\b")
                               || Regex.IsMatch(low, @"\bon my (pc|computer|laptop|desktop)\b");
                if (!fileish) return null;

                q = Regex.Replace(q, @"\b(on|in|from)\s+my\s+(pc|computer|laptop|desktop|documents|downloads)\b", "").Trim();
                q = Regex.Replace(q, @"\b(file|files|document|folder|please)\b", "").Trim();
                q = Regex.Replace(q, @"\s{2,}", " ");
                if (q.Length < 2) return "What file should I look for?";

                string query = q;
                Task.Run(delegate { FindAndReveal(query); });
                return "Searching for " + query + ".";
            }

            return null;
        }

        private static IEnumerable<string> SearchRoots()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flinch");
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Path.Combine(profile, "Downloads");
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        private static void FindAndReveal(string query)
        {
            try
            {
                string[] words = query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                List<FileInfo> hits = new List<FileInfo>();
                int scanned = 0;
                HashSet<string> seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string root in SearchRoots())
                {
                    if (!Directory.Exists(root) || !seenRoots.Add(root)) continue;
                    Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
                    stack.Push(new KeyValuePair<string, int>(root, 0));
                    while (stack.Count > 0 && scanned < 30000)
                    {
                        var cur = stack.Pop();
                        try
                        {
                            foreach (string f in Directory.GetFiles(cur.Key))
                            {
                                scanned++;
                                string n = Path.GetFileName(f).ToLowerInvariant();
                                bool all = true;
                                foreach (string w in words) { if (!n.Contains(w)) { all = false; break; } }
                                if (all) hits.Add(new FileInfo(f));
                            }
                            if (cur.Value < 3)
                            {
                                foreach (string d in Directory.GetDirectories(cur.Key))
                                {
                                    string dn = Path.GetFileName(d);
                                    if (dn.StartsWith(".") || dn.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                                    stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (hits.Count == 0)
                {
                    SpeakSafe("I couldn't find anything matching " + query + ".");
                    return;
                }

                hits = hits.OrderByDescending(h => h.LastWriteTime).ToList();
                FileInfo best = hits[0];
                RevealInExplorer(best.FullName);
                string where = Path.GetFileName(best.DirectoryName);
                if (hits.Count == 1)
                    SpeakSafe("Found it: " + best.Name + ", in your " + where + " folder. I've opened it in File Explorer.");
                else
                    SpeakSafe("I found " + hits.Count + " matches. Showing the newest one, " + best.Name + ", in your " + where + " folder.");
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("FindAndReveal error: " + ex.Message);
                SpeakSafe("Sorry, the file search failed.");
            }
        }

        private static bool ResolveKnownFolder(string low, out string folder, out string label)
        {
            folder = null; label = null;
            if (low.Contains("flinch"))
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flinch");
                label = "Flinch";
            }
            else if (low.Contains("desktop"))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                label = "Desktop";
            }
            else if (low.Contains("document"))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                label = "Documents";
            }
            else if (low.Contains("download"))
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                label = "Downloads";
            }
            return folder != null;
        }

        private static void ZipFolder(string folder, string label)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    SpeakSafe("Your " + label + " folder doesn't exist yet.");
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
}
