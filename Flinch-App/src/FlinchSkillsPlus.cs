using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FlinchApp
{
    // =========================================================================
    // Flinch Skills Plus (partial): extra local voice skills.
    //   1. File verbs: rename / copy / move / zip / open / reveal / copy path
    //   2. Recycle Bin deletes + "undo delete"
    //   3. Search follow-ups ("open it", "delete it", "next match")
    //   4. Chained commands ("find my resume, zip it and put it on the desktop")
    //   5. Quiz mode (voice quiz on the open whiteboard)
    //   6. Whiteboard history, reopen and export (HTML -> Print to PDF)
    //   7. Clipboard history
    //   8. Window, volume and media control
    //   9. Download-finished alerts
    // NOTE: C# 5 only (no ?., no $"", no out var, no nameof).
    // =========================================================================
    internal static partial class FlinchSkills
    {
        // ------------------------------------------------------------------
        // Native helpers
        // ------------------------------------------------------------------
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        private const uint KEYUP = 0x0002;
        private const byte VK_LWIN = 0x5B, VK_MENU = 0x12, VK_TAB = 0x09, VK_SHIFT = 0x10;
        private const byte VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44, VK_S = 0x53;
        private const byte VK_VOL_MUTE = 0xAD, VK_VOL_DOWN = 0xAE, VK_VOL_UP = 0xAF;
        private const byte VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY = 0xB3;

        private static void Tap(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYUP, UIntPtr.Zero);
        }

        private static void Chord(byte mod, byte vk)
        {
            keybd_event(mod, 0, 0, UIntPtr.Zero);
            Tap(vk);
            keybd_event(mod, 0, KEYUP, UIntPtr.Zero);
        }

        private static void Chord3(byte mod1, byte mod2, byte vk)
        {
            keybd_event(mod1, 0, 0, UIntPtr.Zero);
            keybd_event(mod2, 0, 0, UIntPtr.Zero);
            Tap(vk);
            keybd_event(mod2, 0, KEYUP, UIntPtr.Zero);
            keybd_event(mod1, 0, KEYUP, UIntPtr.Zero);
        }

        private static void RunOnUi(Action a)
        {
            try
            {
                if (System.Windows.Application.Current != null && System.Windows.Application.Current.Dispatcher != null)
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(a);
                else
                    a();
            }
            catch { }
        }

        public static void StartPlus()
        {
            try { StartClipboardHistory(); } catch (Exception ex) { FlinchEnv.Log("StartClipboardHistory: " + ex.Message); }
            try { StartDownloadWatcher(); } catch (Exception ex) { FlinchEnv.Log("StartDownloadWatcher: " + ex.Message); }
        }

        public static void CancelSkillState()
        {
            lock (_quizLock) { _quiz = null; }
        }

        private static string TryPlus(string transcript, string low)
        {
            string r;
            r = TryQuiz(transcript, low);              if (r != null) return r;
            r = TryChainedFileCommand(transcript, low); if (r != null) return r;
            r = TryUndoDelete(transcript, low);        if (r != null) return r;
            r = TryFileVerbs(transcript, low);         if (r != null) return r;
            r = TryBoards(transcript, low);            if (r != null) return r;
            r = TryClipboardHistory(transcript, low);  if (r != null) return r;
            r = TrySystemControl(transcript, low);     if (r != null) return r;
            r = TryPlusToggles(transcript, low);       if (r != null) return r;
            return null;
        }

        // ==================================================================
        // Search session tracking (set by BackgroundTaskManager.StartFileSearchTask)
        // ==================================================================
        internal static string LastFoundPath;
        internal static DateTime LastFoundUtc = DateTime.MinValue;
        private static readonly object _matchLock = new object();
        private static List<KeyValuePair<int, string>> _sessionMatches = new List<KeyValuePair<int, string>>();
        private static List<string> _lastMatches = new List<string>();
        private static int _lastMatchIdx = 0;

        internal static void BeginSearchSession()
        {
            lock (_matchLock) { _sessionMatches = new List<KeyValuePair<int, string>>(); }
        }

        internal static void NoteSearchMatch(string path, int score)
        {
            if (score < 65 || string.IsNullOrEmpty(path)) return;
            lock (_matchLock)
            {
                if (_sessionMatches.Count < 400) _sessionMatches.Add(new KeyValuePair<int, string>(score, path));
            }
        }

        internal static void EndSearchSession(string best)
        {
            lock (_matchLock)
            {
                var list = new List<string>();
                if (!string.IsNullOrEmpty(best)) list.Add(best);
                foreach (var kv in _sessionMatches.OrderByDescending(k => k.Key).ThenByDescending(k => SafeWriteTime(k.Value)))
                {
                    if (!list.Contains(kv.Value, StringComparer.OrdinalIgnoreCase)) list.Add(kv.Value);
                    if (list.Count >= 8) break;
                }
                _lastMatches = list;
                _lastMatchIdx = 0;
                if (list.Count > 0)
                {
                    LastFoundPath = list[0];
                    LastFoundUtc = DateTime.UtcNow;
                }
            }
        }

        private static DateTime SafeWriteTime(string p)
        {
            try { return File.GetLastWriteTime(p); } catch { return DateTime.MinValue; }
        }

        // ==================================================================
        // Item resolver
        // ==================================================================
        private static bool IsPronoun(string n)
        {
            return Regex.IsMatch(n, @"^(?:it|that|this|them|that file|this file|the file|that folder|the folder|the one|that one)$");
        }

        private static string CleanName(string n)
        {
            n = (n ?? "").Trim().Trim('"', '\'', '.', ',', '?', '!', ' ');
            n = Regex.Replace(n, @"^(?:my|the|a|an)\s+", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"^(?:file|folder|document|doc)\s+(?:named\s+|called\s+)?", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"^(?:named|called)\s+", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\s+(?:file|folder|document|please|now)$", "", RegexOptions.IgnoreCase);
            return n.Trim();
        }

        private static string FreshLastFound()
        {
            if (!string.IsNullOrEmpty(LastFoundPath) && (DateTime.UtcNow - LastFoundUtc).TotalMinutes < 20 &&
                (File.Exists(LastFoundPath) || Directory.Exists(LastFoundPath)))
                return LastFoundPath;
            return null;
        }

        internal static string ResolveItem(string rawName, out bool isDir)
        {
            isDir = false;
            string name = CleanName(rawName);
            if (string.IsNullOrEmpty(name)) return null;

            if (IsPronoun(name.ToLowerInvariant()))
            {
                string p = FreshLastFound();
                if (p != null) isDir = Directory.Exists(p);
                return p;
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Path.Combine(profile, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flinch"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            string qLow = name.ToLowerInvariant();
            string qNorm = Regex.Replace(qLow, @"[\s_\-]+", "");
            string best = null; bool bestDir = false; int bestScore = 0; DateTime bestTime = DateTime.MinValue;

            foreach (string d in dirs)
            {
                if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) continue;
                string direct = Path.Combine(d, name);
                try
                {
                    if (File.Exists(direct)) { isDir = false; return direct; }
                    if (Directory.Exists(direct)) { isDir = true; return direct; }
                }
                catch { }

                try
                {
                    foreach (string f in Directory.GetFiles(d))
                    {
                        string ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                        if (ext == ".lnk" || ext == ".ini") continue;
                        int sc = ScoreName(Path.GetFileName(f), qLow, qNorm);
                        if (sc > 0)
                        {
                            DateTime dt = SafeWriteTime(f);
                            if (sc > bestScore || (sc == bestScore && dt > bestTime)) { bestScore = sc; bestTime = dt; best = f; bestDir = false; }
                        }
                    }
                    foreach (string sd in Directory.GetDirectories(d))
                    {
                        int sc = ScoreName(Path.GetFileName(sd), qLow, qNorm) - 4;
                        if (sc > 0)
                        {
                            DateTime dt = Directory.GetLastWriteTime(sd);
                            if (sc > bestScore || (sc == bestScore && dt > bestTime)) { bestScore = sc; bestTime = dt; best = sd; bestDir = true; }
                        }
                    }
                }
                catch { }
            }

            isDir = bestDir;
            return best;
        }

        private static int ScoreName(string fileName, string qLow, string qNorm)
        {
            string fLow = (fileName ?? "").ToLowerInvariant();
            string noExt = (Path.GetFileNameWithoutExtension(fileName) ?? "").ToLowerInvariant();
            string fNorm = Regex.Replace(noExt, @"[\s_\-]+", "");
            if (fLow == qLow || noExt == qLow || fNorm == qNorm) return 95;
            if (noExt.StartsWith(qLow) || fNorm.StartsWith(qNorm)) return 85;
            if (qNorm.Length >= 2 && (fNorm.Contains(qNorm) || fLow.Contains(qLow))) return 70;
            return 0;
        }

        private static bool ResolveDestFolder(string spec, out string folder, out string label)
        {
            folder = null; label = null;
            spec = (spec ?? "").Trim().Trim('.', ',', '?', '!').ToLowerInvariant();
            spec = Regex.Replace(spec, @"^(?:my|the)\s+", "");
            spec = Regex.Replace(spec, @"\s+(?:folder|directory)$", "");
            if (spec.Length == 0) return false;
            if (ResolveKnownFolder(spec, out folder, out label)) return true;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string baseDir in new[] { desktop, Path.Combine(profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            {
                try
                {
                    string cand = Path.Combine(baseDir, spec);
                    if (Directory.Exists(cand)) { folder = cand; label = Path.GetFileName(cand); return true; }
                    foreach (string sd in Directory.GetDirectories(baseDir))
                    {
                        if (string.Equals(Path.GetFileName(sd), spec, StringComparison.OrdinalIgnoreCase)) { folder = sd; label = Path.GetFileName(sd); return true; }
                    }
                }
                catch { }
            }
            return false;
        }

        private static string UniquePath(string dir, string fileName)
        {
            string dest = Path.Combine(dir, fileName);
            if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
            string b = Path.GetFileNameWithoutExtension(fileName);
            string e = Path.GetExtension(fileName);
            int n = 2;
            while (File.Exists(dest) || Directory.Exists(dest))
            {
                dest = Path.Combine(dir, string.Format("{0} ({1}){2}", b, n, e));
                n++;
            }
            return dest;
        }

        private static void AskApproval(string title, string desc, string cmd, Action onYes, string cancelSpeech)
        {
            var act = new ActionPayload { type = "move_files", name = title, requires_approval = true, description = desc, command = cmd };
            RunOnUi(delegate
            {
                ActionApprovalWindow.ShowApproval(act,
                    delegate { onYes(); },
                    delegate { SpeakSafe(cancelSpeech); });
            });
        }

        // ==================================================================
        // 2. RECYCLE BIN DELETE + UNDO
        // ==================================================================
        private static string _lastRecycledPath;
        private static bool _lastRecycledIsDir;
        private static DateTime _lastRecycledUtc = DateTime.MinValue;

        // Sends a file or folder to the Windows Recycle Bin (recoverable). Returns false on failure.
        internal static bool SafeDelete(string path, bool isDir)
        {
            try
            {
                Assembly vb = Assembly.Load("Microsoft.VisualBasic, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
                Type fs = vb.GetType("Microsoft.VisualBasic.FileIO.FileSystem");
                Type ui = vb.GetType("Microsoft.VisualBasic.FileIO.UIOption");
                Type rc = vb.GetType("Microsoft.VisualBasic.FileIO.RecycleOption");
                object uiOpt = Enum.ToObject(ui, 2);   // OnlyErrorDialogs
                object rcOpt = Enum.ToObject(rc, 2);   // SendToRecycleBin
                string method = isDir ? "DeleteDirectory" : "DeleteFile";
                MethodInfo mi = fs.GetMethod(method, new Type[] { typeof(string), ui, rc });
                mi.Invoke(null, new object[] { path, uiOpt, rcOpt });
                _lastRecycledPath = path;
                _lastRecycledIsDir = isDir;
                _lastRecycledUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("SafeDelete recycle failed: " + ex.Message);
                return false;
            }
        }

        private static string TryUndoDelete(string transcript, string low)
        {
            if (!Regex.IsMatch(low, @"^(?:please\s+)?(?:undo\s+(?:the\s+)?delet(?:e|ion)|undelete|restore\s+(?:that|the|my)\s+(?:file|folder|deleted\s+\w+)|bring\s+(?:it|that|the\s+file)\s+back|put\s+(?:it|that)\s+back\s+from\s+the\s+recycle\s+bin)$"))
                return null;
            if (_lastRecycledPath == null || (DateTime.UtcNow - _lastRecycledUtc).TotalMinutes > 60)
                return "I don't have a recent delete to undo. You can still find it in the Recycle Bin.";

            string path = _lastRecycledPath;
            string leaf = Path.GetFileName(path);
            Task.Run(delegate
            {
                try
                {
                    string dir = (Path.GetDirectoryName(path) ?? "").Replace("'", "''");
                    string name = leaf.Replace("'", "''");
                    string noExt = Path.GetFileNameWithoutExtension(leaf).Replace("'", "''");
                    string script =
                        "$sh=New-Object -ComObject Shell.Application;$bin=$sh.NameSpace(10);$done=$false;" +
                        "foreach($i in $bin.Items()){ $od=$bin.GetDetailsOf($i,1); " +
                        "if($od -eq '" + dir + "' -and ($i.Name -eq '" + name + "' -or $i.Name -eq '" + noExt + "')){ $i.InvokeVerb('undelete'); $done=$true; break } }";
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                    { UseShellExecute = false, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) { p.WaitForExit(15000); }
                    Thread.Sleep(600);
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        LastFoundPath = path; LastFoundUtc = DateTime.UtcNow;
                        _lastRecycledPath = null;
                        SpeakSafe("Restored " + leaf + ".");
                    }
                    else
                    {
                        SpeakSafe("I couldn't restore " + leaf + " automatically. It should be in your Recycle Bin.");
                    }
                }
                catch (Exception ex) { FlinchEnv.Log("Undo delete error: " + ex.Message); SpeakSafe("Sorry, I couldn't restore that."); }
            });
            return "Restoring " + leaf + " from the Recycle Bin.";
        }

        // ==================================================================
        // 1 & 3. FILE VERBS + SEARCH FOLLOW-UPS
        // ==================================================================
        private const string ItRx = @"(?:it|that|this|that file|this file|the file|that folder|the folder|the one)";

        private static string TryFileVerbs(string transcript, string low)
        {
            Match m;

            // next match from the last search
            if (Regex.IsMatch(low, @"^(?:show\s+(?:me\s+)?)?(?:the\s+)?(?:next|another)\s+(?:match|one|result|file)$|^next$|^what\s+else\s+did\s+you\s+find$"))
            {
                lock (_matchLock)
                {
                    if (_lastMatches.Count < 2) return "That was the only match I found.";
                    _lastMatchIdx = (_lastMatchIdx + 1) % _lastMatches.Count;
                    LastFoundPath = _lastMatches[_lastMatchIdx];
                    LastFoundUtc = DateTime.UtcNow;
                    RevealInExplorer(LastFoundPath);
                    return "Match " + (_lastMatchIdx + 1) + " of " + _lastMatches.Count + ": " + Path.GetFileName(LastFoundPath) + ".";
                }
            }

            // open / reveal / path
            m = Regex.Match(low, @"^(?:please\s+)?(?:open|launch)\s+(?:the\s+file\s+|my\s+file\s+)?(" + ItRx + @")$");
            if (m.Success) return DoVerb("open", m.Groups[1].Value, null);
            m = Regex.Match(low, @"^(?:please\s+)?open\s+(?:the\s+|my\s+)?(?:file|document|folder)\s+(?:named\s+|called\s+)?(.+)$");
            if (m.Success) return DoVerb("open", m.Groups[1].Value, null);
            m = Regex.Match(low, @"^(?:please\s+)?open\s+(?:the\s+|my\s+)?(.+?\.(?:pdf|docx?|xlsx?|pptx?|txt|png|jpe?g|zip|csv|mp[34]|md))$");
            if (m.Success) return DoVerb("open", m.Groups[1].Value, null);

            m = Regex.Match(low, @"^(?:please\s+)?(?:show|reveal|highlight)\s+(" + ItRx + @")(?:\s+in\s+(?:file\s+)?explorer)?$");
            if (m.Success) return DoVerb("reveal", m.Groups[1].Value, null);

            m = Regex.Match(low, @"^(?:please\s+)?copy\s+(?:the\s+|its\s+)?(?:file\s+)?path(?:\s+of\s+(" + ItRx + @"))?$");
            if (m.Success) return DoVerb("path", string.IsNullOrEmpty(m.Groups[1].Value) ? "it" : m.Groups[1].Value, null);

            // delete the last found item
            m = Regex.Match(low, @"^(?:please\s+)?(?:delete|remove|trash)\s+(" + ItRx + @")$");
            if (m.Success) return DoVerb("delete", m.Groups[1].Value, null);

            // rename X to Y
            m = Regex.Match(low, @"^(?:please\s+)?rename\s+(?:the\s+|my\s+)?(?:file\s+|folder\s+)?(.+?)\s+to\s+(.+)$");
            if (m.Success && !m.Groups[1].Value.Contains("latest download")) return DoVerb("rename", m.Groups[1].Value, m.Groups[2].Value);

            // copy / move X to FOLDER
            m = Regex.Match(low, @"^(?:please\s+)?(copy|move|put)\s+(?:the\s+|my\s+)?(?:file\s+)?(.+?)\s+(?:to|into|in|on)\s+(?:the\s+|my\s+)?(.+)$");
            if (m.Success)
            {
                string verb = m.Groups[1].Value == "copy" ? "copy" : "move";
                string what = m.Groups[2].Value;
                // let the bulk organizer handle plural/category phrasing ("pdfs", "screenshots")
                if (Regex.IsMatch(what, @"\b(?:all|pdfs|images|photos|screenshots|files|everything|documents)\b") && !IsPronoun(what)) return null;
                return DoVerb(verb, what, m.Groups[3].Value);
            }

            // zip X (single file/folder not handled by the known-folder zipper)
            m = Regex.Match(low, @"^(?:please\s+)?(?:zip|compress)\s+(?:the\s+|my\s+)?(.+)$");
            if (m.Success)
            {
                string t = m.Groups[1].Value;
                string kf, kl;
                if (!ResolveKnownFolder(t, out kf, out kl)) return DoVerb("zip", t, null);
            }

            return null;
        }

        private static string DoVerb(string verb, string rawName, string arg)
        {
            bool isDir;
            string path = ResolveItem(rawName, out isDir);
            if (path == null)
            {
                return IsPronoun(CleanName(rawName).ToLowerInvariant())
                    ? "I don't have a recent file in mind. Ask me to find one first."
                    : "I couldn't find " + CleanName(rawName) + " on your Desktop, Downloads or Documents.";
            }
            return ExecuteVerb(verb, path, isDir, arg);
        }

        private static string ExecuteVerb(string verb, string path, bool isDir, string arg)
        {
            string leaf = Path.GetFileName(path);
            LastFoundPath = path;
            LastFoundUtc = DateTime.UtcNow;

            try
            {
                switch (verb)
                {
                    case "open":
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        return "Opening " + leaf + ".";

                    case "reveal":
                        RevealInExplorer(path);
                        return "Here is " + leaf + ".";

                    case "path":
                        SetClipboardText(path);
                        return "Copied the path to " + leaf + " to your clipboard.";

                    case "delete":
                        return PromptApprovalForSingleDelete(path, isDir);

                    case "rename":
                    {
                        string newName = (arg ?? "").Trim().Trim('"', '\'', '.', ',', ' ');
                        if (newName.Length == 0) return "What should I rename it to?";
                        foreach (char c in Path.GetInvalidFileNameChars()) newName = newName.Replace(c.ToString(), "");
                        if (!isDir)
                        {
                            string ext = Path.GetExtension(path);
                            if (!string.IsNullOrEmpty(ext) && !newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
                        }
                        string dir = Path.GetDirectoryName(path);
                        string dest = Path.Combine(dir, newName);
                        if (File.Exists(dest) || Directory.Exists(dest)) return "Something named " + newName + " already exists there.";
                        if (isDir) Directory.Move(path, dest); else File.Move(path, dest);
                        LastFoundPath = dest;
                        RevealInExplorer(dest);
                        return "Renamed " + leaf + " to " + newName + ".";
                    }

                    case "copy":
                    case "move":
                    {
                        string folder, label;
                        if (!ResolveDestFolder(arg, out folder, out label)) return "I don't know which folder you mean by " + (arg ?? "that") + ".";
                        if (isDir && verb == "copy") return "I can only copy single files for now.";
                        string dest = UniquePath(folder, leaf);
                        if (verb == "copy")
                        {
                            File.Copy(path, dest);
                            LastFoundPath = dest;
                            RevealInExplorer(dest);
                            return "Copied " + leaf + " to " + label + ".";
                        }

                        string src = path;
                        bool dirFlag = isDir;
                        AskApproval("move " + leaf + " to " + label,
                            "Moves \"" + leaf + "\" into " + label + ".",
                            src + " -> " + folder,
                            delegate
                            {
                                try
                                {
                                    if (dirFlag) Directory.Move(src, dest); else File.Move(src, dest);
                                    LastFoundPath = dest; LastFoundUtc = DateTime.UtcNow;
                                    RevealInExplorer(dest);
                                    SpeakSafe("Moved " + leaf + " to " + label + ".");
                                }
                                catch (Exception ex) { SpeakSafe("Sorry, I couldn't move it. " + ex.Message); }
                            },
                            "Cancelled moving " + leaf + ".");
                        return "Please confirm on screen or say yes to move " + leaf + " to " + label + ".";
                    }

                    case "zip":
                    {
                        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        string label = "Desktop";
                        if (!string.IsNullOrWhiteSpace(arg))
                        {
                            string f2, l2;
                            if (ResolveDestFolder(arg, out f2, out l2)) { folder = f2; label = l2; }
                        }
                        string zipName = Path.GetFileNameWithoutExtension(leaf);
                        if (isDir) zipName = leaf;
                        string zipPath = UniquePath(folder, zipName + ".zip");
                        string srcPath = path;
                        bool dirFlag = isDir;
                        Task.Run(delegate
                        {
                            try
                            {
                                if (dirFlag)
                                {
                                    ZipFile.CreateFromDirectory(srcPath, zipPath, CompressionLevel.Optimal, false);
                                }
                                else
                                {
                                    using (ZipArchive za = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                                    {
                                        za.CreateEntryFromFile(srcPath, Path.GetFileName(srcPath), CompressionLevel.Optimal);
                                    }
                                }
                                LastFoundPath = zipPath; LastFoundUtc = DateTime.UtcNow;
                                RevealInExplorer(zipPath);
                                SpeakSafe("Done. " + Path.GetFileName(zipPath) + " is in your " + label + ".");
                            }
                            catch (Exception ex) { SpeakSafe("Sorry, I couldn't zip that. " + ex.Message); }
                        });
                        return "Zipping " + leaf + " now.";
                    }
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("ExecuteVerb error: " + ex.Message);
                return "Sorry, that didn't work. " + ex.Message;
            }
            return null;
        }

        // ==================================================================
        // 4. CHAINED COMMANDS
        // "find my resume and zip it and put it on the desktop"
        // ==================================================================
        private static string TryChainedFileCommand(string transcript, string low)
        {
            Match m = Regex.Match(low, @"^(?:please\s+)?(?:find|locate|get)\s+(?:my\s+|the\s+)?(.+?)\s*(?:,\s*|\s+and\s+then\s+|\s+then\s+|\s+and\s+)(zip|compress|copy|move|rename|delete|open)\s+(?:it|that|the\s+file)(.*)$");
            if (!m.Success) return null;

            string name = m.Groups[1].Value;
            string verb = m.Groups[2].Value;
            string tail = m.Groups[3].Value.Trim();
            if (verb == "compress") verb = "zip";

            bool isDir;
            string path = ResolveItem(name, out isDir);
            if (path == null)
                return "I couldn't find " + CleanName(name) + " on your Desktop, Downloads or Documents, so I didn't do the rest.";

            string arg = null;
            Match d = Regex.Match(tail, @"\b(?:to|into|in|on)\s+(?:my\s+|the\s+)?(.+?)(?:\s+folder)?$");
            if (verb == "rename")
            {
                Match rn = Regex.Match(tail, @"^(?:to|as)\s+(.+)$");
                if (rn.Success) arg = rn.Groups[1].Value;
            }
            else if (d.Success)
            {
                arg = d.Groups[1].Value;
            }
            else if (Regex.IsMatch(tail, @"(?:put|save|place).*(desktop)"))
            {
                arg = "desktop";
            }

            if ((verb == "copy" || verb == "move") && string.IsNullOrWhiteSpace(arg)) return "Where should I " + verb + " it?";

            string res = ExecuteVerb(verb, path, isDir, arg);
            return "Found " + Path.GetFileName(path) + ". " + (res ?? "");
        }

        // ==================================================================
        // 5. QUIZ MODE
        // ==================================================================
        private sealed class QuizState
        {
            public List<string> Questions = new List<string>();
            public List<string> Answers = new List<string>();
            public int Index;
            public int Correct;
            public bool Grading;
        }
        private static QuizState _quiz;
        private static readonly object _quizLock = new object();

        private static string TryQuiz(string transcript, string low)
        {
            QuizState active;
            lock (_quizLock) { active = _quiz; }

            if (active != null)
            {
                if (Regex.IsMatch(low, @"^(?:stop|cancel|exit|end|quit)(?:\s+the)?\s+quiz$|^that'?s\s+enough$"))
                {
                    lock (_quizLock) { _quiz = null; }
                    return "Okay, quiz stopped. You got " + active.Correct + " out of " + active.Index + " so far.";
                }
                if (Regex.IsMatch(low, @"whiteboard|timer|close|hub|volume|open |email|cancel|stop"))
                    return null;
                if (Regex.IsMatch(low, @"^(?:repeat|say that again|again)(?:\s+the\s+question)?$"))
                    return "Question " + (active.Index + 1) + ": " + active.Questions[active.Index];
                if (Regex.IsMatch(low, @"^(?:skip|pass|i don'?t know|no idea)(?:\s+this\s+one)?$"))
                {
                    string ans = active.Answers[active.Index];
                    return AdvanceQuiz(active, false, "The answer is " + ans + ".");
                }
                if (active.Grading) return "";

                active.Grading = true;
                string q = active.Questions[active.Index];
                string refAns = active.Answers[active.Index];
                string said = transcript;
                Task.Run(delegate
                {
                    string verdict = TransformText(
                        "Question: " + q + "\nReference answer: " + refAns + "\nStudent said: " + said,
                        "You are a kind tutor grading a spoken answer. Reply with exactly one line that starts with CORRECT or WRONG, then ' - ' and one short friendly sentence. If wrong, state the right answer in that sentence. Be lenient on wording.");
                    bool ok = verdict != null && verdict.Trim().ToUpperInvariant().StartsWith("CORRECT");
                    string comment = "";
                    if (verdict != null)
                    {
                        int dash = verdict.IndexOf(" - ", StringComparison.Ordinal);
                        comment = dash >= 0 ? verdict.Substring(dash + 3).Trim() : "";
                    }
                    if (verdict == null) { comment = "I couldn't grade that. The answer is " + refAns + "."; }
                    string title = ok ? "\u2713 Correct" : "\u2717 Not quite";
                    try { if (ShowNudgeAlert != null) ShowNudgeAlert(title, comment.Length > 0 ? comment : title); } catch { }
                    string next = AdvanceQuiz(active, ok, (ok ? "Correct! " : "Not quite. ") + comment);
                    active.Grading = false;
                    SpeakSafe(next);
                });
                return "";
            }

            // Start a quiz
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:quiz\s+me|test\s+me|give\s+me\s+a\s+quiz)(?:\s+(?:on|about)\s+(?:this|that|it|the\s+whiteboard|the\s+board))?$"))
            {
                string ctx = null;
                try { ctx = OverlayWindow.ActiveWhiteboardContext; } catch { }
                if (string.IsNullOrWhiteSpace(ctx) && _boardCache.Count > 0)
                    ctx = _boardCache[0].Title + "\n" + _boardCache[0].Text;
                if (string.IsNullOrWhiteSpace(ctx))
                    return "Ask me to explain something first, then I'll quiz you on it.";

                Task.Run(delegate
                {
                    string raw = TransformText(ctx,
                        "Write exactly 3 short quiz questions that test understanding of this text. Output ONLY 3 lines, each in the form: <question> || <short correct answer>");
                    var qs = new QuizState();
                    if (raw != null)
                    {
                        foreach (string line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int bar = line.IndexOf("||", StringComparison.Ordinal);
                            if (bar < 0) continue;
                            string qq = Regex.Replace(line.Substring(0, bar), @"^\s*(?:Q?\d+[\.\):]\s*)", "").Trim();
                            string aa = line.Substring(bar + 2).Trim();
                            if (qq.Length > 3 && aa.Length > 0 && qs.Questions.Count < 3) { qs.Questions.Add(qq); qs.Answers.Add(aa); }
                        }
                    }
                    if (qs.Questions.Count == 0) { SpeakSafe("Sorry, I couldn't put a quiz together."); return; }
                    lock (_quizLock) { _quiz = qs; }
                    SpeakSafe("Okay, quiz time. Answer out loud with Alt V. Question 1: " + qs.Questions[0]);
                });
                return "Putting together a quick quiz.";
            }
            return null;
        }

        private static string AdvanceQuiz(QuizState qs, bool wasCorrect, string feedback)
        {
            if (wasCorrect) qs.Correct++;
            qs.Index++;
            if (qs.Index >= qs.Questions.Count)
            {
                int c = qs.Correct, t = qs.Questions.Count;
                lock (_quizLock) { _quiz = null; }
                string verdict = c == t ? "Perfect score!" : (c >= t / 2.0 ? "Nice work." : "Worth another look at the board.");
                return feedback + " That's the quiz: " + c + " out of " + t + ". " + verdict;
            }
            return feedback + " Question " + (qs.Index + 1) + ": " + qs.Questions[qs.Index];
        }

        // ==================================================================
        // 6. WHITEBOARD HISTORY, REOPEN, EXPORT
        // ==================================================================
        internal sealed class BoardRecord
        {
            public string Title { get; set; }
            public string Subtitle { get; set; }
            public string Text { get; set; }
            public string Style { get; set; }
            public string Graph { get; set; }
            public string Sources { get; set; }
            public string ImageUrl { get; set; }
            public string SavedAt { get; set; }
        }

        private static List<BoardRecord> _boardCache = new List<BoardRecord>();
        private static bool _boardsLoaded = false;
        private static readonly object _boardLock = new object();

        private static string BoardsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flinch");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "boards.json");
        }

        private static void EnsureBoardsLoaded()
        {
            lock (_boardLock)
            {
                if (_boardsLoaded) return;
                _boardsLoaded = true;
                try
                {
                    string p = BoardsPath();
                    if (File.Exists(p))
                    {
                        var list = Json.Deserialize<List<BoardRecord>>(File.ReadAllText(p, Encoding.UTF8));
                        if (list != null) _boardCache = list;
                    }
                }
                catch { _boardCache = new List<BoardRecord>(); }
            }
        }

        // Called from the whiteboard renderer every time a board is shown.
        public static void SaveBoard(string title, string subtitle, string text, string style, string graph, string sources, string imageUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                EnsureBoardsLoaded();
                lock (_boardLock)
                {
                    if (_boardCache.Count > 0 && _boardCache[0].Title == title && _boardCache[0].Text == text) return;
                    _boardCache.Insert(0, new BoardRecord
                    {
                        Title = title, Subtitle = subtitle, Text = text, Style = style, Graph = graph,
                        Sources = sources, ImageUrl = imageUrl, SavedAt = DateTime.Now.ToString("g")
                    });
                    if (_boardCache.Count > 30) _boardCache.RemoveRange(30, _boardCache.Count - 30);
                    File.WriteAllText(BoardsPath(), Json.Serialize(_boardCache), Encoding.UTF8);
                }
            }
            catch (Exception ex) { FlinchEnv.Log("SaveBoard error: " + ex.Message); }
        }

        private static string TryBoards(string transcript, string low)
        {
            Match m;
            if (Regex.IsMatch(low, @"^(?:list|what are)\s+(?:my\s+)?(?:recent\s+|past\s+|saved\s+)?(?:whiteboards|boards)$|^show\s+my\s+(?:past\s+|saved\s+)?(?:whiteboards|boards)$"))
            {
                EnsureBoardsLoaded();
                lock (_boardLock)
                {
                    if (_boardCache.Count == 0) return "You don't have any saved whiteboards yet.";
                    var titles = _boardCache.Take(5).Select(b => b.Title).ToArray();
                    return "Your recent whiteboards are: " + string.Join(", ", titles) + ".";
                }
            }

            m = Regex.Match(low, @"^(?:please\s+)?(?:show|reopen|open|bring\s+back|pull\s+up)\s+(?:me\s+)?(?:my\s+|the\s+)?(last|previous|latest|earlier)\s+(?:whiteboard|board)$");
            if (m.Success)
            {
                EnsureBoardsLoaded();
                BoardRecord rec = null;
                lock (_boardLock)
                {
                    int idx = (m.Groups[1].Value == "previous" || m.Groups[1].Value == "earlier") && _boardCache.Count > 1 ? 1 : 0;
                    if (_boardCache.Count > idx) rec = _boardCache[idx];
                }
                if (rec == null) return "I don't have a saved whiteboard to reopen.";
                ReopenBoard(rec);
                return "Reopening " + rec.Title + ".";
            }

            m = Regex.Match(low, @"^(?:please\s+)?(?:show|reopen|open|bring\s+back|pull\s+up)\s+(?:me\s+)?(?:the\s+|my\s+)?(?:whiteboard|board)\s+(?:about|on|for)\s+(.+)$");
            if (m.Success)
            {
                EnsureBoardsLoaded();
                string q = m.Groups[1].Value.Trim().ToLowerInvariant();
                BoardRecord rec = null;
                lock (_boardLock)
                {
                    rec = _boardCache.FirstOrDefault(b => (b.Title ?? "").ToLowerInvariant().Contains(q) || (b.Text ?? "").ToLowerInvariant().Contains(q));
                }
                if (rec == null) return "I couldn't find a saved whiteboard about " + q + ".";
                ReopenBoard(rec);
                return "Here's the board about " + rec.Title + ".";
            }

            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:export|save|print)\s+(?:this\s+|the\s+|my\s+|that\s+)?(?:whiteboard|board)(?:\s+(?:as|to)\s+(?:a\s+)?(?:pdf|notes|file|html))?$"))
            {
                EnsureBoardsLoaded();
                BoardRecord rec = null;
                lock (_boardLock) { if (_boardCache.Count > 0) rec = _boardCache[0]; }
                if (rec == null) return "There's no whiteboard to export yet.";
                return ExportBoardHtml(rec);
            }
            return null;
        }

        private static void ReopenBoard(BoardRecord rec)
        {
            RunOnUi(delegate
            {
                try
                {
                    var scene = new ScenePayload { durationMs = 30000 };
                    scene.objects.Add(new SceneObjectPayload
                    {
                        type = "notepad",
                        title = rec.Title,
                        subtitle = rec.Subtitle,
                        text = rec.Text,
                        style = rec.Style,
                        graph = rec.Graph,
                        sources = rec.Sources,
                        imageUrl = rec.ImageUrl,
                        delayMs = 0
                    });
                    OverlayWindow.ShowScene(GeminiClient.LastActiveTargetBounds, scene, 2500, null);
                }
                catch (Exception ex) { FlinchEnv.Log("ReopenBoard error: " + ex.Message); }
            });
        }

        private static string Esc(string s)
        {
            return System.Net.WebUtility.HtmlEncode(s ?? "");
        }

        private static string ExportBoardHtml(BoardRecord rec)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><title>").Append(Esc(rec.Title)).Append("</title>");
                sb.Append("<style>body{font-family:'Segoe Print','Segoe UI',sans-serif;max-width:720px;margin:40px auto;color:#1e293b;line-height:1.7}");
                sb.Append("h1{border-bottom:3px solid #e11d48;padding-bottom:6px}p,li{font-size:16px}.sub{color:#64748b}.src{margin-top:28px;font-size:12px;color:#64748b}</style></head><body>");
                sb.Append("<h1>").Append(Esc(rec.Title)).Append("</h1>");
                if (!string.IsNullOrWhiteSpace(rec.Subtitle)) sb.Append("<div class='sub'>").Append(Esc(rec.Subtitle)).Append("</div>");
                bool steps = string.Equals(rec.Style, "steps", StringComparison.OrdinalIgnoreCase);
                string[] lines = (rec.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (steps)
                {
                    sb.Append("<ol>");
                    foreach (string l in lines)
                        sb.Append("<li>").Append(Esc(Regex.Replace(l.Trim(), @"^(?:\d+[\.\)\:]|Step\s+\d+[\.\)\:]?)\s*", "", RegexOptions.IgnoreCase))).Append("</li>");
                    sb.Append("</ol>");
                }
                else
                {
                    foreach (string l in lines) sb.Append("<p>").Append(Esc(l.Trim())).Append("</p>");
                }
                if (!string.IsNullOrWhiteSpace(rec.Sources)) sb.Append("<div class='src'>Sources: ").Append(Esc(rec.Sources)).Append("</div>");
                sb.Append("<div class='src'>Saved by Flinch &middot; ").Append(Esc(rec.SavedAt)).Append("</div>");
                sb.Append("<script>window.onload=function(){setTimeout(function(){window.print();},400);};</script></body></html>");

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string safe = Regex.Replace(rec.Title ?? "Whiteboard", @"[^\w\- ]", "").Trim();
                if (safe.Length == 0) safe = "Whiteboard";
                string path = UniquePath(desktop, "Flinch - " + safe + ".html");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return "Exported the board to your Desktop. Choose Save as PDF in the print window that just opened.";
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("ExportBoardHtml error: " + ex.Message);
                return "Sorry, I couldn't export that board.";
            }
        }

        // ==================================================================
        // 7. CLIPBOARD HISTORY
        // ==================================================================
        private sealed class ClipEntry { public DateTime Time; public string Text; }
        private static readonly List<ClipEntry> _clipHistory = new List<ClipEntry>();
        private static readonly object _clipLock = new object();
        private static uint _lastClipSeq = 0;
        private static Timer _clipTimer;

        private static void StartClipboardHistory()
        {
            _lastClipSeq = GetClipboardSequenceNumber();
            _clipTimer = new Timer(delegate { PollClipboard(); }, null, 3000, 1500);
        }

        private static void PollClipboard()
        {
            try
            {
                uint seq = GetClipboardSequenceNumber();
                if (seq == _lastClipSeq) return;
                _lastClipSeq = seq;
                string t = GetClipboardText();
                if (string.IsNullOrWhiteSpace(t)) return;
                if (t.StartsWith("[Active Whiteboard")) return;
                if (t.Length > 20000) t = t.Substring(0, 20000);
                lock (_clipLock)
                {
                    if (_clipHistory.Count > 0 && _clipHistory[0].Text == t) return;
                    _clipHistory.Insert(0, new ClipEntry { Time = DateTime.Now, Text = t });
                    if (_clipHistory.Count > 25) _clipHistory.RemoveRange(25, _clipHistory.Count - 25);
                }
            }
            catch { }
        }

        private static string Preview(string t, int max)
        {
            string s = Regex.Replace(t ?? "", @"\s+", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        private static string TryClipboardHistory(string transcript, string low)
        {
            Match m = Regex.Match(low, @"^what\s+did\s+i\s+copy\s+(\d+)\s+(second|sec|minute|min|hour|hr)s?\s+ago$");
            if (m.Success)
            {
                int n = int.Parse(m.Groups[1].Value);
                string u = m.Groups[2].Value;
                TimeSpan ago = u.StartsWith("sec") ? TimeSpan.FromSeconds(n) : (u.StartsWith("h") ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n));
                DateTime target = DateTime.Now - ago;
                lock (_clipLock)
                {
                    ClipEntry e = _clipHistory.FirstOrDefault(c => c.Time <= target);
                    if (e == null) return "I don't have anything copied that far back. I only remember this session.";
                    return "Around then you copied: " + Preview(e.Text, 220);
                }
            }

            if (Regex.IsMatch(low, @"^what\s+did\s+i\s+copy\s+(?:before\s+that|previously|earlier)$|^what\s+was\s+the\s+(?:previous|last)\s+thing\s+i\s+copied$"))
            {
                lock (_clipLock)
                {
                    if (_clipHistory.Count < 2) return "I only have one thing in your clipboard history.";
                    return "Before that you copied: " + Preview(_clipHistory[1].Text, 220);
                }
            }

            if (Regex.IsMatch(low, @"^(?:show|read|list)\s+(?:me\s+)?(?:my\s+)?clipboard\s+history$|^what\s+have\s+i\s+copied\s+(?:recently|lately|today)$"))
            {
                lock (_clipLock)
                {
                    if (_clipHistory.Count == 0) return "I haven't seen anything copied yet this session.";
                    var parts = _clipHistory.Take(3).Select((c, i) => (i + 1) + ": " + Preview(c.Text, 70)).ToArray();
                    return "Your last copies are. " + string.Join(". ", parts) + ".";
                }
            }

            m = Regex.Match(low, @"^(?:copy|restore|bring\s+back|paste)\s+(?:the\s+)?(?:previous|last|earlier)\s+(?:thing\s+)?(?:i\s+)?copied(?:\s+(?:text|item))?$|^(?:copy|bring\s+back)\s+what\s+i\s+copied\s+before(?:\s+that)?$");
            if (m.Success)
            {
                string t;
                lock (_clipLock)
                {
                    if (_clipHistory.Count < 2) return "I don't have an earlier copy to bring back.";
                    t = _clipHistory[1].Text;
                }
                SetClipboardText(t);
                return "Done. The previous copy is back on your clipboard, ready to paste.";
            }
            return null;
        }

        // ==================================================================
        // 8. WINDOW, VOLUME & MEDIA CONTROL
        // ==================================================================
        private static IntPtr TargetWindow()
        {
            IntPtr fg = GetForegroundWindow();
            string title = "";
            try
            {
                var sb = new StringBuilder(256);
                GetWindowTextPlus(fg, sb, 256);
                title = sb.ToString();
            }
            catch { }
            if (fg == IntPtr.Zero || title == "Flinch" || title == "") return LastForegroundWindowHwnd;
            return fg;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowText", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextPlus(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        private static void Steps(byte vk, int n)
        {
            for (int i = 0; i < n; i++) { Tap(vk); Thread.Sleep(6); }
        }

        private static string TrySystemControl(string transcript, string low)
        {
            Match m;

            // Volume
            m = Regex.Match(low, @"^(?:please\s+)?(?:set|change)\s+(?:the\s+)?volume\s+to\s+(\d{1,3})(?:\s*(?:percent|%))?$");
            if (m.Success)
            {
                int v = Math.Max(0, Math.Min(100, int.Parse(m.Groups[1].Value)));
                Task.Run(delegate { Steps(VK_VOL_DOWN, 50); Steps(VK_VOL_UP, v / 2); });
                return "Volume set to about " + v + " percent.";
            }
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:turn\s+(?:the\s+)?(?:volume|it|sound)\s+up|volume\s+up|louder|raise\s+the\s+volume|increase\s+(?:the\s+)?volume)$"))
            { Steps(VK_VOL_UP, 5); return "Turned it up."; }
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:turn\s+(?:the\s+)?(?:volume|it|sound)\s+down|volume\s+down|quieter|lower\s+the\s+volume|decrease\s+(?:the\s+)?volume)$"))
            { Steps(VK_VOL_DOWN, 5); return "Turned it down."; }
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:mute|unmute)(?:\s+(?:the\s+)?(?:sound|volume|audio|pc|computer))?$|^(?:mute|unmute)\s+it$"))
            { Tap(VK_VOL_MUTE); return ""; }

            // Media
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:pause|resume|play|stop)\s+(?:the\s+)?(?:music|song|media|video|playback|audio|track)$|^(?:pause|resume)$"))
            { Tap(VK_MEDIA_PLAY); return ""; }
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:next|skip)\s+(?:the\s+)?(?:song|track)$|^skip\s+(?:this\s+)?(?:song|track)$"))
            { Tap(VK_MEDIA_NEXT); return ""; }
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:previous|last)\s+(?:song|track)$|^go\s+back\s+a\s+(?:song|track)$"))
            { Tap(VK_MEDIA_PREV); return ""; }

            // Windows
            IntPtr hwnd;
            if (Regex.IsMatch(low, @"^(?:please\s+)?minimi[sz]e(?:\s+(?:this|the|that)(?:\s+window)?)?$"))
            {
                hwnd = TargetWindow(); if (hwnd == IntPtr.Zero) return "I'm not sure which window you mean.";
                ShowWindow(hwnd, 6); return "";
            }
            if (Regex.IsMatch(low, @"^(?:please\s+)?maximi[sz]e(?:\s+(?:this|the|that)(?:\s+window)?)?$|^make\s+(?:this|the)\s+window\s+(?:bigger|fullscreen|full\s+screen)$"))
            {
                hwnd = TargetWindow(); if (hwnd == IntPtr.Zero) return "I'm not sure which window you mean.";
                ShowWindow(hwnd, 3); return "";
            }
            if (Regex.IsMatch(low, @"^(?:please\s+)?restore(?:\s+(?:this|the|that)(?:\s+window)?)?$"))
            {
                hwnd = TargetWindow(); if (hwnd == IntPtr.Zero) return "I'm not sure which window you mean.";
                ShowWindow(hwnd, 9); return "";
            }
            m = Regex.Match(low, @"^(?:please\s+)?snap\s+(?:this\s+|the\s+)?(?:window\s+)?(left|right)$|^(?:put|move)\s+(?:this|the)\s+window\s+(?:on\s+the\s+|to\s+the\s+)?(left|right)(?:\s+side)?$");
            if (m.Success)
            {
                string side = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                hwnd = TargetWindow(); if (hwnd == IntPtr.Zero) return "I'm not sure which window you mean.";
                Task.Run(delegate
                {
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(180);
                    Chord(VK_LWIN, side == "left" ? VK_LEFT : VK_RIGHT);
                });
                return "";
            }
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:show|go\s+to)\s+(?:the\s+)?desktop$|^minimi[sz]e\s+(?:all|everything)(?:\s+windows)?$"))
            { Chord(VK_LWIN, VK_D); return ""; }
            if (Regex.IsMatch(low, @"^(?:please\s+)?(?:switch|next|go\s+to\s+(?:the\s+)?(?:next|other))\s+(?:window|app)$|^switch\s+windows$|^alt\s*tab$"))
            { Chord(VK_MENU, VK_TAB); return ""; }
            if (Regex.IsMatch(low, @"^(?:please\s+)?take\s+a\s+screenshot$|^(?:open\s+)?(?:the\s+)?snipping\s+tool$"))
            { Chord3(VK_LWIN, VK_SHIFT, VK_S); return ""; }

            return null;
        }

        // ==================================================================
        // 9. DOWNLOAD-FINISHED ALERTS
        // ==================================================================
        private static bool _downloadAlerts = true;
        private static FileSystemWatcher _dlWatcher;
        private static readonly Dictionary<string, DateTime> _dlSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static void StartDownloadWatcher()
        {
            string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dl)) return;
            _dlWatcher = new FileSystemWatcher(dl);
            _dlWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
            _dlWatcher.Created += OnDownloadEvent;
            _dlWatcher.Renamed += OnDownloadRenamed;
            _dlWatcher.EnableRaisingEvents = true;
        }

        private static void OnDownloadRenamed(object s, RenamedEventArgs e) { HandleDownloadFile(e.FullPath); }
        private static void OnDownloadEvent(object s, FileSystemEventArgs e) { HandleDownloadFile(e.FullPath); }

        private static void HandleDownloadFile(string path)
        {
            if (!_downloadAlerts) return;
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".crdownload" || ext == ".tmp" || ext == ".part" || ext == ".download" || ext == ".partial" || ext == "") return;

            Task.Run(delegate
            {
                try
                {
                    lock (_dlSeen)
                    {
                        DateTime t;
                        if (_dlSeen.TryGetValue(path, out t) && (DateTime.UtcNow - t).TotalSeconds < 20) return;
                        _dlSeen[path] = DateTime.UtcNow;
                    }
                    for (int i = 0; i < 40; i++)
                    {
                        try
                        {
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                            break;
                        }
                        catch (FileNotFoundException) { return; }
                        catch (DirectoryNotFoundException) { return; }
                        catch { Thread.Sleep(500); }
                    }
                    if (!File.Exists(path)) return;
                    LastFoundPath = path;
                    LastFoundUtc = DateTime.UtcNow;
                    if (ShowNudgeAlert != null) ShowNudgeAlert("Download finished", Path.GetFileName(path));
                }
                catch { }
            });
        }

        private static string TryPlusToggles(string transcript, string low)
        {
            if (Regex.IsMatch(low, @"^(?:please\s+)?turn\s+off\s+download\s+(?:alerts|notifications)$|^stop\s+(?:alerting|telling)\s+me\s+(?:about|when)\s+downloads?.*$"))
            { _downloadAlerts = false; return "Okay, I'll stay quiet about downloads."; }
            if (Regex.IsMatch(low, @"^(?:please\s+)?turn\s+on\s+download\s+(?:alerts|notifications)$|^tell\s+me\s+when\s+(?:a\s+|my\s+)?downloads?\s+(?:is\s+)?(?:finished|done|completes?)$"))
            { _downloadAlerts = true; return "Got it. I'll let you know when a download finishes."; }
            return null;
        }
    }
}
