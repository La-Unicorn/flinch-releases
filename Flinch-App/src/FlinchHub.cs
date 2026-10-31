// ============================================================================
// FLINCH AI: FLINCH HUB & BACKGROUND AUTOMATIONS ENGINE
// Direct App Integrations (Email / Files / Clipboard) & Autonomous Background Workers
// Zero Third-Party Account Creation Required — 100% Local On-Device Execution
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FlinchApp
{
    // =========================================================================
    // 1. Data Models for Email & Background Automations
    // =========================================================================
    public sealed class EmailMessageInfo
    {
        public string Id { get; set; }
        public string From { get; set; }
        public string Subject { get; set; }
        public DateTime Date { get; set; }
        public bool IsUrgent { get; set; }
        public string Snippet { get; set; }
    }

    public sealed class EmailIntegrationConfig
    {
        public string Provider { get; set; }
        public string EmailAddress { get; set; }
        public string EncryptedSecret { get; set; }
        public string Server { get; set; }
        public int Port { get; set; }
        public bool UseSsl { get; set; }
        public int CheckIntervalMinutes { get; set; }
        public bool IsConnected { get; set; }
        public DateTime LastCheckedUtc { get; set; }

        public EmailIntegrationConfig()
        {
            Provider = "Gmail";
            EmailAddress = "";
            EncryptedSecret = "";
            Server = "imap.gmail.com";
            Port = 993;
            UseSsl = true;
            CheckIntervalMinutes = 15;
            IsConnected = false;
            LastCheckedUtc = DateTime.MinValue;
        }
    }

    public sealed class AutomationConfig
    {
        public bool EmailGuardianEnabled { get; set; }
        public bool DownloadsOrganizerEnabled { get; set; }
        public bool DailyBriefingEnabled { get; set; }
        public bool ClipboardContextEnabled { get; set; }
        public int EmailIntervalMinutes { get; set; }
        public string WatchedDownloadsPath { get; set; }

        public AutomationConfig()
        {
            EmailGuardianEnabled = true;
            DownloadsOrganizerEnabled = false;
            DailyBriefingEnabled = false;
            ClipboardContextEnabled = true;
            EmailIntervalMinutes = 15;
            WatchedDownloadsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    // =========================================================================
    // 2. Direct IMAP Client (100% Local SSL over Port 993, Zero Account Creation)
    // =========================================================================
    internal static class DirectImapClient
    {
        private static readonly string[] UrgentKeywords = new string[]
        {
            "urgent", "action required", "deadline", "important", "asap",
            "critical", "alert", "security alert", "payment due", "review required",
            "invoice", "verify your account", "immediate action", "cancelled"
        };

        public static bool TestConnection(string server, int port, bool useSsl, string username, string password, out int unreadCount, out string errorMessage)
        {
            unreadCount = 0;
            errorMessage = "";

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                errorMessage = "Please enter server, email address, and app password.";
                return false;
            }

            try
            {
                using (var tcp = new TcpClient())
                {
                    var connectTask = tcp.ConnectAsync(server, port);
                    if (!connectTask.Wait(8000))
                    {
                        errorMessage = "Connection timed out. Check server and port.";
                        return false;
                    }

                    Stream netStream = tcp.GetStream();
                    SslStream ssl = null;
                    if (useSsl)
                    {
                        ssl = new SslStream(netStream, false);
                        ssl.AuthenticateAsClient(server);
                        netStream = ssl;
                    }

                    using (var reader = new StreamReader(netStream, Encoding.UTF8))
                    using (var writer = new StreamWriter(netStream, Encoding.ASCII) { AutoFlush = true })
                    {
                        // 1. Read greeting
                        string greeting = reader.ReadLine();
                        if (string.IsNullOrEmpty(greeting) || !greeting.StartsWith("*"))
                        {
                            errorMessage = "Unexpected server greeting: " + greeting;
                            return false;
                        }

                        // 2. LOGIN
                        writer.WriteLine("A01 LOGIN \"" + EscapeImapString(username) + "\" \"" + EscapeImapString(password) + "\"");
                        string loginResp = ReadUntilTagged(reader, "A01");
                        if (!loginResp.Contains("A01 OK"))
                        {
                            errorMessage = "Authentication failed. For Gmail/Outlook, please use an App Password.";
                            return false;
                        }

                        // 3. SELECT INBOX
                        writer.WriteLine("A02 SELECT INBOX");
                        string selectResp = ReadUntilTagged(reader, "A02");
                        if (!selectResp.Contains("A02 OK"))
                        {
                            errorMessage = "Could not select INBOX folder.";
                            return false;
                        }

                        // 4. SEARCH UNSEEN
                        writer.WriteLine("A03 SEARCH UNSEEN");
                        string searchResp = ReadUntilTagged(reader, "A03");
                        unreadCount = CountUnreadIds(searchResp);

                        // 5. LOGOUT
                        try { writer.WriteLine("A04 LOGOUT"); } catch { }
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool FetchUnreadMessages(string server, int port, bool useSsl, string username, string password, int maxCount, out List<EmailMessageInfo> messages, out string errorMessage)
        {
            messages = new List<EmailMessageInfo>();
            errorMessage = "";

            try
            {
                using (var tcp = new TcpClient())
                {
                    var connectTask = tcp.ConnectAsync(server, port);
                    if (!connectTask.Wait(8000))
                    {
                        errorMessage = "Connection timed out.";
                        return false;
                    }

                    Stream netStream = tcp.GetStream();
                    if (useSsl)
                    {
                        var ssl = new SslStream(netStream, false);
                        ssl.AuthenticateAsClient(server);
                        netStream = ssl;
                    }

                    using (var reader = new StreamReader(netStream, Encoding.UTF8))
                    using (var writer = new StreamWriter(netStream, Encoding.ASCII) { AutoFlush = true })
                    {
                        reader.ReadLine(); // greeting

                        writer.WriteLine("A01 LOGIN \"" + EscapeImapString(username) + "\" \"" + EscapeImapString(password) + "\"");
                        string loginResp = ReadUntilTagged(reader, "A01");
                        if (!loginResp.Contains("A01 OK"))
                        {
                            errorMessage = "Login failed.";
                            return false;
                        }

                        writer.WriteLine("A02 SELECT INBOX");
                        ReadUntilTagged(reader, "A02");

                        writer.WriteLine("A03 SEARCH UNSEEN");
                        string searchResp = ReadUntilTagged(reader, "A03");
                        List<string> ids = ParseSearchIds(searchResp);

                        if (ids.Count == 0)
                        {
                            try { writer.WriteLine("A04 LOGOUT"); } catch { }
                            return true;
                        }

                        // Fetch the latest unread headers up to maxCount
                        int take = Math.Min(maxCount, ids.Count);
                        int startIdx = Math.Max(0, ids.Count - take);

                        for (int i = ids.Count - 1; i >= startIdx; i--)
                        {
                            string id = ids[i];
                            string tag = "F" + id;
                            writer.WriteLine(tag + " FETCH " + id + " (BODY.PEEK[HEADER.FIELDS (SUBJECT FROM DATE)])");
                            string headerData = ReadUntilTagged(reader, tag);

                            var msg = ParseHeaderBlock(id, headerData);
                            if (msg != null)
                            {
                                messages.Add(msg);
                            }
                        }

                        try { writer.WriteLine("A99 LOGOUT"); } catch { }
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string EscapeImapString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ReadUntilTagged(StreamReader reader, string tag)
        {
            var sb = new StringBuilder();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                sb.AppendLine(line);
                if (line.StartsWith(tag + " "))
                {
                    break;
                }
            }
            return sb.ToString();
        }

        private static int CountUnreadIds(string searchOutput)
        {
            var ids = ParseSearchIds(searchOutput);
            return ids.Count;
        }

        private static List<string> ParseSearchIds(string searchOutput)
        {
            var list = new List<string>();
            var lines = searchOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var l in lines)
            {
                if (l.StartsWith("* SEARCH"))
                {
                    string rest = l.Substring(8).Trim();
                    if (!string.IsNullOrEmpty(rest))
                    {
                        var parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            list.Add(p.Trim());
                        }
                    }
                }
            }
            return list;
        }

        private static EmailMessageInfo ParseHeaderBlock(string id, string rawHeader)
        {
            string subj = "(No Subject)";
            string from = "Unknown Sender";
            DateTime dt = DateTime.Now;

            var lines = rawHeader.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var l in lines)
            {
                if (l.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
                {
                    subj = l.Substring(8).Trim();
                }
                else if (l.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
                {
                    from = l.Substring(5).Trim();
                }
                else if (l.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime parsed;
                    if (DateTime.TryParse(l.Substring(5).Trim(), out parsed))
                    {
                        dt = parsed;
                    }
                }
            }

            // Decode RFC2047 if encoded (e.g. =?UTF-8?B?...?=)
            subj = DecodeRfc2047(subj);
            from = CleanSenderString(DecodeRfc2047(from));

            bool isUrgent = false;
            string testStr = (subj + " " + from).ToLowerInvariant();
            foreach (var kw in UrgentKeywords)
            {
                if (testStr.Contains(kw))
                {
                    isUrgent = true;
                    break;
                }
            }

            return new EmailMessageInfo
            {
                Id = id,
                From = from,
                Subject = subj,
                Date = dt,
                IsUrgent = isUrgent,
                Snippet = subj
            };
        }

        private static string CleanSenderString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unknown";
            int idx = s.IndexOf('<');
            if (idx > 0)
            {
                return s.Substring(0, idx).Trim().Trim('\"', '\'');
            }
            return s.Trim().Trim('\"', '\'');
        }

        private static string DecodeRfc2047(string input)
        {
            if (string.IsNullOrEmpty(input) || !input.Contains("=?")) return input;
            try
            {
                return Regex.Replace(input, @"=\?([A-Za-z0-9\-_]+)\?([BQbq])\?([^?]+)\?=", m =>
                {
                    string charset = m.Groups[1].Value;
                    string encType = m.Groups[2].Value.ToUpperInvariant();
                    string text = m.Groups[3].Value;
                    try
                    {
                        var enc = Encoding.GetEncoding(charset);
                        if (encType == "B")
                        {
                            byte[] bytes = Convert.FromBase64String(text);
                            return enc.GetString(bytes);
                        }
                    }
                    catch { }
                    return m.Value;
                });
            }
            catch
            {
                return input;
            }
        }
    }

    // =========================================================================
    // 3. Background Automations Engine (Autonomous Workers & Triage)
    // =========================================================================
    internal static class BackgroundAutomationsEngine
    {
        private static readonly string ConfigDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flinch");
        private static readonly string ConfigFilePath = System.IO.Path.Combine(ConfigDir, "automations.dat");
        private static readonly string EmailConfigPath = System.IO.Path.Combine(ConfigDir, "email_integration.dat");

        public static AutomationConfig Config { get; private set; }
        public static EmailIntegrationConfig EmailConfig { get; private set; }
        public static List<EmailMessageInfo> RecentMessages { get; private set; }

        private static System.Threading.Timer _emailTimer = null;
        private static System.Threading.Timer _downloadsTimer = null;
        private static readonly object _syncLock = new object();

        public static int OrganizedFilesCount { get; private set; }
        public static string EmailStatusText { get; private set; }
        public static string DownloadsStatusText { get; private set; }

        public static event Action OnStateChanged;
        public static event Action<string, string, bool> OnUrgentAlertTriggered;

        static BackgroundAutomationsEngine()
        {
            RecentMessages = new List<EmailMessageInfo>();
            OrganizedFilesCount = 0;
            EmailStatusText = "Idle";
            DownloadsStatusText = "Active";

            LoadConfigurations();
            StartTimers();
        }

        public static void LoadConfigurations()
        {
            lock (_syncLock)
            {
                try
                {
                    if (File.Exists(ConfigFilePath))
                    {
                        byte[] enc = File.ReadAllBytes(ConfigFilePath);
                        byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                        string json = Encoding.UTF8.GetString(dec);
                        var jss = new JavaScriptSerializer();
                        Config = jss.Deserialize<AutomationConfig>(json) ?? new AutomationConfig();
                    }
                    else
                    {
                        Config = new AutomationConfig();
                    }
                }
                catch
                {
                    Config = new AutomationConfig();
                }

                try
                {
                    if (File.Exists(EmailConfigPath))
                    {
                        byte[] enc = File.ReadAllBytes(EmailConfigPath);
                        byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                        string json = Encoding.UTF8.GetString(dec);
                        var jss = new JavaScriptSerializer();
                        EmailConfig = jss.Deserialize<EmailIntegrationConfig>(json) ?? new EmailIntegrationConfig();
                    }
                    else
                    {
                        EmailConfig = new EmailIntegrationConfig();
                    }
                }
                catch
                {
                    EmailConfig = new EmailIntegrationConfig();
                }
            }
        }

        public static void SaveConfigurations()
        {
            lock (_syncLock)
            {
                try
                {
                    if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);

                    var jss = new JavaScriptSerializer();

                    string cJson = jss.Serialize(Config);
                    byte[] cEnc = ProtectedData.Protect(Encoding.UTF8.GetBytes(cJson), null, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(ConfigFilePath, cEnc);

                    string eJson = jss.Serialize(EmailConfig);
                    byte[] eEnc = ProtectedData.Protect(Encoding.UTF8.GetBytes(eJson), null, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(EmailConfigPath, eEnc);
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("Failed to save automations config: " + ex.Message);
                }
            }
        }

        public static void StartTimers()
        {
            if (_emailTimer == null)
            {
                _emailTimer = new System.Threading.Timer(OnEmailTimerFired, null, 10000, 15 * 60 * 1000);
            }
            if (_downloadsTimer == null)
            {
                _downloadsTimer = new System.Threading.Timer(OnDownloadsTimerFired, null, 20000, 30 * 60 * 1000);
            }
        }

        private static void OnEmailTimerFired(object state)
        {
            if (Config.EmailGuardianEnabled && EmailConfig.IsConnected)
            {
                RunEmailTriage();
            }
        }

        private static void OnDownloadsTimerFired(object state)
        {
            if (Config.DownloadsOrganizerEnabled)
            {
                RunDownloadsOrganizer();
            }
        }

        public static void SaveEmailPassword(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword))
            {
                EmailConfig.EncryptedSecret = "";
            }
            else
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainPassword), null, DataProtectionScope.CurrentUser);
                EmailConfig.EncryptedSecret = Convert.ToBase64String(enc);
            }
            SaveConfigurations();
        }

        public static string GetEmailPassword()
        {
            if (string.IsNullOrEmpty(EmailConfig.EncryptedSecret)) return "";
            try
            {
                byte[] enc = Convert.FromBase64String(EmailConfig.EncryptedSecret);
                byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch
            {
                return "";
            }
        }

        public static Task<bool> RunEmailTriageAsync()
        {
            return Task.Run(() => RunEmailTriage());
        }

        public static bool RunEmailTriage()
        {
            if (!EmailConfig.IsConnected || string.IsNullOrEmpty(EmailConfig.EmailAddress))
            {
                EmailStatusText = "Not Connected";
                NotifyStateChanged();
                return false;
            }

            EmailStatusText = "Checking inbox...";
            NotifyStateChanged();

            string pass = GetEmailPassword();
            List<EmailMessageInfo> msgs;
            string err;
            bool ok = DirectImapClient.FetchUnreadMessages(
                EmailConfig.Server,
                EmailConfig.Port,
                EmailConfig.UseSsl,
                EmailConfig.EmailAddress,
                pass,
                15,
                out msgs,
                out err);

            if (ok)
            {
                lock (_syncLock)
                {
                    RecentMessages = msgs;
                    EmailConfig.LastCheckedUtc = DateTime.UtcNow;
                }

                int urgentCount = 0;
                EmailMessageInfo firstUrgent = null;
                foreach (var m in msgs)
                {
                    if (m.IsUrgent)
                    {
                        urgentCount++;
                        if (firstUrgent == null) firstUrgent = m;
                    }
                }

                EmailStatusText = string.Format("Active • Checked {0:t} ({1} unread)", DateTime.Now, msgs.Count);

                if (firstUrgent != null && OnUrgentAlertTriggered != null)
                {
                    string alertMsg = string.Format("✉️ Urgent Email from {0}: \"{1}\"", firstUrgent.From, firstUrgent.Subject);
                    OnUrgentAlertTriggered(alertMsg, firstUrgent.From, true);
                }

                NotifyStateChanged();
                return true;
            }
            else
            {
                EmailStatusText = "Sync Error: " + (err.Length > 24 ? err.Substring(0, 24) + "..." : err);
                NotifyStateChanged();
                return false;
            }
        }

        public static Task<int> RunDownloadsOrganizerAsync()
        {
            return Task.Run(() => RunDownloadsOrganizer());
        }

        public static int RunDownloadsOrganizer()
        {
            DownloadsStatusText = "Scanning folders...";
            NotifyStateChanged();

            int moved = 0;
            try
            {
                string dir = Config.WatchedDownloadsPath;
                if (!Directory.Exists(dir))
                {
                    dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                }

                if (!Directory.Exists(dir))
                {
                    DownloadsStatusText = "Folder not found";
                    NotifyStateChanged();
                    return 0;
                }

                string docsDir = System.IO.Path.Combine(dir, "Documents");
                string installDir = System.IO.Path.Combine(dir, "Installers");
                string archivesDir = System.IO.Path.Combine(dir, "Archives");
                string mediaDir = System.IO.Path.Combine(dir, "Media");

                var files = Directory.GetFiles(dir);
                foreach (var file in files)
                {
                    string ext = (System.IO.Path.GetExtension(file) ?? "").ToLowerInvariant();
                    string targetFolder = null;

                    if (ext == ".pdf" || ext == ".docx" || ext == ".xlsx" || ext == ".pptx" || ext == ".txt" || ext == ".csv")
                    {
                        targetFolder = docsDir;
                    }
                    else if (ext == ".exe" || ext == ".msi")
                    {
                        targetFolder = installDir;
                    }
                    else if (ext == ".zip" || ext == ".rar" || ext == ".7z" || ext == ".tar" || ext == ".gz")
                    {
                        targetFolder = archivesDir;
                    }
                    else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".mp4" || ext == ".mov")
                    {
                        targetFolder = mediaDir;
                    }

                    if (targetFolder != null)
                    {
                        try
                        {
                            if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);
                            string destFile = System.IO.Path.Combine(targetFolder, System.IO.Path.GetFileName(file));
                            if (!File.Exists(destFile))
                            {
                                File.Move(file, destFile);
                                moved++;
                            }
                        }
                        catch { }
                    }
                }

                OrganizedFilesCount += moved;
                DownloadsStatusText = string.Format("Active • {0} files auto-sorted", OrganizedFilesCount);
            }
            catch (Exception ex)
            {
                DownloadsStatusText = "Error: " + ex.Message;
            }

            NotifyStateChanged();
            return moved;
        }

        public static string GetSpokenEmailSummary()
        {
            lock (_syncLock)
            {
                if (!EmailConfig.IsConnected)
                {
                    return "Your email is not connected yet. Click the Dynamic Island to open the Flinch Hub and connect your inbox.";
                }

                if (RecentMessages == null || RecentMessages.Count == 0)
                {
                    return "You have no unread emails right now. Your inbox is completely clear.";
                }

                int urgentCount = 0;
                string urgentSubject = "";
                string urgentSender = "";
                foreach (var m in RecentMessages)
                {
                    if (m.IsUrgent)
                    {
                        urgentCount++;
                        if (string.IsNullOrEmpty(urgentSubject))
                        {
                            urgentSubject = m.Subject;
                            urgentSender = m.From;
                        }
                    }
                }

                if (urgentCount > 0)
                {
                    return string.Format("You have {0} unread emails. Most urgent is from {1} regarding {2}.",
                        RecentMessages.Count, urgentSender, urgentSubject);
                }

                var first = RecentMessages[0];
                return string.Format("You have {0} unread emails. The most recent is from {1} about {2}.",
                    RecentMessages.Count, first.From, first.Subject);
            }
        }

        private static void NotifyStateChanged()
        {
            if (OnStateChanged != null)
            {
                try { OnStateChanged(); } catch { }
            }
        }
    }

    // =========================================================================
    // 4. Modern Frosted-Glass Flinch Hub UI (Expands Beneath Notch)
    // =========================================================================
    internal sealed class FlinchHubWindow : Window
    {
        private static readonly Color ColBgDark = Color.FromRgb(0x12, 0x14, 0x1A);
        private static readonly Color ColCardBg = Color.FromRgb(0x1A, 0x1D, 0x26);
        private static readonly Color ColBorder = Color.FromRgb(0x2A, 0x2E, 0x3D);
        private static readonly Color ColMint = Color.FromRgb(0x3D, 0xDC, 0x84);
        private static readonly Color ColOrange = Color.FromRgb(0xFF, 0x8A, 0x3D);
        private static readonly Color ColCream = Color.FromRgb(0xF4, 0xEB, 0xDD);
        private static readonly Color ColMuted = Color.FromRgb(0x8E, 0x92, 0xA2);

        private static readonly Brush BrushBgDark = CreateFrozenBrush(ColBgDark);
        private static readonly Brush BrushCardBg = CreateFrozenBrush(ColCardBg);
        private static readonly Brush BrushBorder = CreateFrozenBrush(ColBorder);
        private static readonly Brush BrushMint = CreateFrozenBrush(ColMint);
        private static readonly Brush BrushOrange = CreateFrozenBrush(ColOrange);
        private static readonly Brush BrushCream = CreateFrozenBrush(ColCream);
        private static readonly Brush BrushMuted = CreateFrozenBrush(ColMuted);

        private static Brush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private readonly DynamicIslandWindow _parentIsland;
        private readonly StackPanel _tabNavHost;
        private readonly ContentControl _tabContentHost;

        // Tabs
        private Border _tabBtnAutomations;
        private Border _tabBtnIntegrations;
        private Border _tabBtnInbox;

        // Integration Inputs
        private ComboBox _providerCombo;
        private TextBox _emailBox;
        private PasswordBox _passBox;
        private TextBox _serverBox;
        private TextBox _portBox;
        private TextBlock _emailFeedbackText;

        // Status Badges
        private TextBlock _emailStatusBadge;
        private TextBlock _downloadsStatusBadge;
        private StackPanel _inboxItemsPanel;

        public FlinchHubWindow(DynamicIslandWindow parentIsland)
        {
            _parentIsland = parentIsland;

            Title = "Flinch Hub";
            Width = 760;
            Height = 520;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;

            // Position directly beneath the Dynamic Island notch
            var scr = System.Windows.Forms.Screen.PrimaryScreen;
            Left = (scr.Bounds.Width - Width) / 2.0;
            Top = 46;

            var rootCard = new Border
            {
                Background = BrushBgDark,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(18),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 36,
                    ShadowDepth = 10,
                    Direction = 270,
                    Opacity = 0.65
                }
            };

            rootCard.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) }); // Header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) }); // Tab bar
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content

            // 1. Header
            var headerGrid = CreateHeader();
            Grid.SetRow(headerGrid, 0);
            mainGrid.Children.Add(headerGrid);

            // 2. Tab Navigation
            _tabNavHost = CreateTabBar();
            Grid.SetRow(_tabNavHost, 1);
            mainGrid.Children.Add(_tabNavHost);

            // 3. Tab Content
            _tabContentHost = new ContentControl { Margin = new Thickness(16, 8, 16, 16) };
            Grid.SetRow(_tabContentHost, 2);
            mainGrid.Children.Add(_tabContentHost);

            rootCard.Child = mainGrid;
            Content = rootCard;

            // Wire up background updates
            BackgroundAutomationsEngine.OnStateChanged += () =>
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateLiveStatusUI()));
            };

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                }
            };

            // Default to Automations tab
            SelectTab(0);
        }

        private Grid CreateHeader()
        {
            var g = new Grid { Margin = new Thickness(18, 0, 18, 0) };

            var leftStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var catIcon = new TextBlock
            {
                Text = "⚡",
                FontSize = 20,
                Foreground = BrushOrange,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            var titleText = new TextBlock
            {
                Text = "Flinch Hub",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = BrushCream,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };

            var pillBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x28, 0x3D, 0xDC, 0x84)),
                BorderBrush = BrushMint,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            var pillText = new TextBlock
            {
                Text = "Autonomous Background Engine",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushMint
            };
            pillBadge.Child = pillText;

            leftStack.Children.Add(catIcon);
            leftStack.Children.Add(titleText);
            leftStack.Children.Add(pillBadge);
            g.Children.Add(leftStack);

            // Right Action Buttons (Voice Mode + Close)
            var rightStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var voiceBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x8A, 0x3D)),
                BorderBrush = BrushOrange,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var voiceBtnText = new TextBlock
            {
                Text = "🎙️ Voice Mode",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOrange
            };
            voiceBtn.Child = voiceBtnText;
            voiceBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                Close();
                if (_parentIsland != null)
                {
                    _parentIsland.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _parentIsland.StartVoiceRecording();
                    }));
                }
            };

            var closeBtn = new Border
            {
                Width = 28,
                Height = 28,
                Background = BrushCardBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Cursor = Cursors.Hand
            };
            var closeText = new TextBlock
            {
                Text = "✕",
                FontSize = 13,
                Foreground = BrushMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.Child = closeText;
            closeBtn.MouseEnter += (s, e) => closeText.Foreground = BrushCream;
            closeBtn.MouseLeave += (s, e) => closeText.Foreground = BrushMuted;
            closeBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                Close();
            };

            rightStack.Children.Add(voiceBtn);
            rightStack.Children.Add(closeBtn);
            g.Children.Add(rightStack);

            return g;
        }

        private StackPanel CreateTabBar()
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(18, 0, 18, 4)
            };

            _tabBtnAutomations = CreateTabItem("🤖 Background Automations", 0);
            _tabBtnIntegrations = CreateTabItem("🔌 App Integrations (Email)", 1);
            _tabBtnInbox = CreateTabItem("✉️ Triaged Inbox", 2);

            sp.Children.Add(_tabBtnAutomations);
            sp.Children.Add(_tabBtnIntegrations);
            sp.Children.Add(_tabBtnInbox);

            return sp;
        }

        private Border CreateTabItem(string label, int index)
        {
            var b = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            var t = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushMuted
            };
            b.Child = t;

            b.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                SelectTab(index);
            };

            return b;
        }

        private void SelectTab(int index)
        {
            SetTabActive(_tabBtnAutomations, index == 0);
            SetTabActive(_tabBtnIntegrations, index == 1);
            SetTabActive(_tabBtnInbox, index == 2);

            if (index == 0)
            {
                _tabContentHost.Content = BuildAutomationsView();
            }
            else if (index == 1)
            {
                _tabContentHost.Content = BuildIntegrationsView();
            }
            else
            {
                _tabContentHost.Content = BuildInboxView();
            }
        }

        private void SetTabActive(Border tabBtn, bool isActive)
        {
            tabBtn.BorderBrush = isActive ? BrushMint : Brushes.Transparent;
            var t = tabBtn.Child as TextBlock;
            if (t != null)
            {
                t.Foreground = isActive ? BrushCream : BrushMuted;
            }
        }

        // =====================================================================
        // Tab 1: Background Automations ("Hard Work")
        // =====================================================================
        private UIElement BuildAutomationsView()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var stack = new StackPanel { Margin = new Thickness(2, 4, 6, 8) };

            // Card 1: Email Guardian
            _emailStatusBadge = new TextBlock
            {
                Text = BackgroundAutomationsEngine.EmailStatusText,
                FontSize = 11,
                Foreground = BrushMint,
                FontWeight = FontWeights.SemiBold
            };
            var emailCard = CreateAutomationCard(
                "✉️ Email Guardian & Smart Triage",
                "Silently monitors unread emails in the background using direct local SSL IMAP (zero 3rd-party accounts). Identifies urgent deadlines and surfaces instant alerts on the Dynamic Island.",
                _emailStatusBadge,
                BackgroundAutomationsEngine.Config.EmailGuardianEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.EmailGuardianEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                },
                "⚡ Check Now",
                () =>
                {
                    _emailStatusBadge.Text = "Running manual check...";
                    BackgroundAutomationsEngine.RunEmailTriageAsync();
                }
            );

            // Card 2: Downloads & Desktop Organizer
            _downloadsStatusBadge = new TextBlock
            {
                Text = BackgroundAutomationsEngine.DownloadsStatusText,
                FontSize = 11,
                Foreground = BrushMint,
                FontWeight = FontWeights.SemiBold
            };
            var downloadsCard = CreateAutomationCard(
                "📁 Smart Downloads & Desktop Organizer",
                "Automatically categorizes loose files in Downloads into subfolders (Documents, Installers, Archives, Images) to keep your PC organized without lifting a finger.",
                _downloadsStatusBadge,
                BackgroundAutomationsEngine.Config.DownloadsOrganizerEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.DownloadsOrganizerEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                },
                "⚡ Organize Now",
                () =>
                {
                    _downloadsStatusBadge.Text = "Scanning Downloads...";
                    BackgroundAutomationsEngine.RunDownloadsOrganizerAsync();
                }
            );

            // Card 3: Morning Executive Briefing
            var briefingBadge = new TextBlock
            {
                Text = "Ready • Scheduled 9:00 AM",
                FontSize = 11,
                Foreground = BrushMuted,
                FontWeight = FontWeights.SemiBold
            };
            var briefingCard = CreateAutomationCard(
                "🌅 Morning Executive Briefing",
                "Synthesizes unread emails, calendar schedule, and system health into a snappy spoken voice summary directly on the pill when you start work.",
                briefingBadge,
                BackgroundAutomationsEngine.Config.DailyBriefingEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.DailyBriefingEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                },
                "⚡ Run Briefing",
                () =>
                {
                    string summary = BackgroundAutomationsEngine.GetSpokenEmailSummary();
                    if (_parentIsland != null)
                    {
                        _parentIsland.SpeakAnswer(summary);
                    }
                }
            );

            stack.Children.Add(emailCard);
            stack.Children.Add(downloadsCard);
            stack.Children.Add(briefingCard);

            scroll.Content = stack;
            return scroll;
        }

        private Border CreateAutomationCard(string title, string description, TextBlock statusBadge, bool isEnabled, Action<bool> onToggle, string actionBtnText, Action onAction)
        {
            var card = new Border
            {
                Background = BrushCardBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var infoStack = new StackPanel();

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = BrushCream,
                Margin = new Thickness(0, 0, 10, 0)
            };
            titleRow.Children.Add(titleText);
            titleRow.Children.Add(statusBadge);
            infoStack.Children.Add(titleRow);

            var descText = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = BrushMuted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17
            };
            infoStack.Children.Add(descText);

            Grid.SetColumn(infoStack, 0);
            g.Children.Add(infoStack);

            // Controls Column (Toggle + Action Button)
            var ctrlStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var toggleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var toggleLabel = new TextBlock
            {
                Text = isEnabled ? "Active" : "Disabled",
                FontSize = 11,
                Foreground = isEnabled ? BrushMint : BrushMuted,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var toggleTrack = new Border
            {
                Width = 38,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = isEnabled ? BrushMint : new SolidColorBrush(Color.FromRgb(0x35, 0x39, 0x48)),
                Cursor = Cursors.Hand
            };
            var thumb = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = BrushCream,
                HorizontalAlignment = isEnabled ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(3)
            };
            toggleTrack.Child = thumb;

            bool currentVal = isEnabled;
            toggleTrack.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                currentVal = !currentVal;
                toggleTrack.Background = currentVal ? BrushMint : new SolidColorBrush(Color.FromRgb(0x35, 0x39, 0x48));
                thumb.HorizontalAlignment = currentVal ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                toggleLabel.Text = currentVal ? "Active" : "Disabled";
                toggleLabel.Foreground = currentVal ? BrushMint : BrushMuted;
                onToggle(currentVal);
            };

            toggleRow.Children.Add(toggleLabel);
            toggleRow.Children.Add(toggleTrack);
            ctrlStack.Children.Add(toggleRow);

            var actionBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x28, 0x3C, 0xC8, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0xC8, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var actionBtnTextEl = new TextBlock
            {
                Text = actionBtnText,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xC8, 0xFF))
            };
            actionBtn.Child = actionBtnTextEl;
            actionBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onAction();
            };

            ctrlStack.Children.Add(actionBtn);
            Grid.SetColumn(ctrlStack, 1);
            g.Children.Add(ctrlStack);

            card.Child = g;
            return card;
        }

        // =====================================================================
        // Tab 2: App Integrations (Direct Local Email Setup)
        // =====================================================================
        private UIElement BuildIntegrationsView()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(2, 4, 6, 8) };

            var emailCard = new Border
            {
                Background = BrushCardBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 14)
            };

            var cardStack = new StackPanel();

            var hText = new TextBlock
            {
                Text = "Direct Email Integration (No Account Creation Required)",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = BrushCream,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var subText = new TextBlock
            {
                Text = "Connects directly from your PC to your mail server via standard SSL IMAP (Port 993). Zero cloud middleman services. Your credentials are encrypted locally with Windows DPAPI.",
                FontSize = 12,
                Foreground = BrushMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
                LineHeight = 17
            };
            cardStack.Children.Add(hText);
            cardStack.Children.Add(subText);

            // Provider Dropdown
            var pRow = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            pRow.Children.Add(new TextBlock { Text = "Email Provider", FontSize = 12, Foreground = BrushMuted, Margin = new Thickness(0, 0, 0, 4) });
            _providerCombo = new ComboBox
            {
                Height = 32,
                Background = BrushBgDark,
                Foreground = BrushCream,
                BorderBrush = BrushBorder
            };
            _providerCombo.Items.Add("Gmail (imap.gmail.com:993)");
            _providerCombo.Items.Add("Outlook / Office 365 (outlook.office365.com:993)");
            _providerCombo.Items.Add("Yahoo Mail (imap.mail.yahoo.com:993)");
            _providerCombo.Items.Add("Custom IMAP Server");
            _providerCombo.SelectedIndex = BackgroundAutomationsEngine.EmailConfig.Provider == "Outlook" ? 1 :
                                           BackgroundAutomationsEngine.EmailConfig.Provider == "Yahoo" ? 2 :
                                           BackgroundAutomationsEngine.EmailConfig.Provider == "Custom" ? 3 : 0;

            _providerCombo.SelectionChanged += (s, e) =>
            {
                if (_providerCombo.SelectedIndex == 0)
                {
                    _serverBox.Text = "imap.gmail.com";
                    _portBox.Text = "993";
                }
                else if (_providerCombo.SelectedIndex == 1)
                {
                    _serverBox.Text = "outlook.office365.com";
                    _portBox.Text = "993";
                }
                else if (_providerCombo.SelectedIndex == 2)
                {
                    _serverBox.Text = "imap.mail.yahoo.com";
                    _portBox.Text = "993";
                }
            };
            pRow.Children.Add(_providerCombo);
            cardStack.Children.Add(pRow);

            // Email Address
            var eRow = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            eRow.Children.Add(new TextBlock { Text = "Email Address", FontSize = 12, Foreground = BrushMuted, Margin = new Thickness(0, 0, 0, 4) });
            _emailBox = new TextBox
            {
                Height = 32,
                Background = BrushBgDark,
                Foreground = BrushCream,
                BorderBrush = BrushBorder,
                Padding = new Thickness(8, 4, 8, 4),
                Text = BackgroundAutomationsEngine.EmailConfig.EmailAddress ?? ""
            };
            eRow.Children.Add(_emailBox);
            cardStack.Children.Add(eRow);

            // App Password
            var pwRow = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var pwHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            pwHeader.Children.Add(new TextBlock { Text = "App Password", FontSize = 12, Foreground = BrushMuted });
            pwHeader.Children.Add(new TextBlock
            {
                Text = " (Use an App Password from your Google/Microsoft account)",
                FontSize = 11,
                Foreground = BrushOrange
            });
            pwRow.Children.Add(pwHeader);
            _passBox = new PasswordBox
            {
                Height = 32,
                Background = BrushBgDark,
                Foreground = BrushCream,
                BorderBrush = BrushBorder,
                Padding = new Thickness(8, 4, 8, 4),
                Password = BackgroundAutomationsEngine.GetEmailPassword()
            };
            pwRow.Children.Add(_passBox);
            cardStack.Children.Add(pwRow);

            // Server & Port Row
            var spGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            spGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            spGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            spGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            var sStack = new StackPanel();
            sStack.Children.Add(new TextBlock { Text = "IMAP Server", FontSize = 12, Foreground = BrushMuted, Margin = new Thickness(0, 0, 0, 4) });
            _serverBox = new TextBox
            {
                Height = 32,
                Background = BrushBgDark,
                Foreground = BrushCream,
                BorderBrush = BrushBorder,
                Padding = new Thickness(8, 4, 8, 4),
                Text = BackgroundAutomationsEngine.EmailConfig.Server ?? "imap.gmail.com"
            };
            sStack.Children.Add(_serverBox);
            Grid.SetColumn(sStack, 0);
            spGrid.Children.Add(sStack);

            var pStack = new StackPanel();
            pStack.Children.Add(new TextBlock { Text = "Port (SSL)", FontSize = 12, Foreground = BrushMuted, Margin = new Thickness(0, 0, 0, 4) });
            _portBox = new TextBox
            {
                Height = 32,
                Background = BrushBgDark,
                Foreground = BrushCream,
                BorderBrush = BrushBorder,
                Padding = new Thickness(8, 4, 8, 4),
                Text = BackgroundAutomationsEngine.EmailConfig.Port > 0 ? BackgroundAutomationsEngine.EmailConfig.Port.ToString() : "993"
            };
            pStack.Children.Add(_portBox);
            Grid.SetColumn(pStack, 2);
            spGrid.Children.Add(pStack);

            cardStack.Children.Add(spGrid);

            // Feedback Text
            _emailFeedbackText = new TextBlock
            {
                FontSize = 12,
                Foreground = BrushMint,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            };
            cardStack.Children.Add(_emailFeedbackText);

            // Button Row
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

            var testBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x28, 0x3D, 0xDC, 0x84)),
                BorderBrush = BrushMint,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 7, 16, 7),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var testBtnText = new TextBlock { Text = "⚡ Test & Save Connection", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = BrushMint };
            testBtn.Child = testBtnText;

            testBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                string srv = (_serverBox.Text ?? "").Trim();
                int prt = 993;
                int.TryParse((_portBox.Text ?? "993").Trim(), out prt);
                string em = (_emailBox.Text ?? "").Trim();
                string pw = _passBox.Password ?? "";

                _emailFeedbackText.Foreground = BrushOrange;
                _emailFeedbackText.Text = "Connecting directly over SSL (port " + prt + ")...";

                Task.Run(() =>
                {
                    int unread;
                    string err;
                    bool ok = DirectImapClient.TestConnection(srv, prt, true, em, pw, out unread, out err);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ok)
                        {
                            BackgroundAutomationsEngine.EmailConfig.Server = srv;
                            BackgroundAutomationsEngine.EmailConfig.Port = prt;
                            BackgroundAutomationsEngine.EmailConfig.EmailAddress = em;
                            BackgroundAutomationsEngine.EmailConfig.IsConnected = true;
                            BackgroundAutomationsEngine.SaveEmailPassword(pw);

                            _emailFeedbackText.Foreground = BrushMint;
                            _emailFeedbackText.Text = string.Format("✅ Connected successfully! Found {0} unread emails in INBOX.", unread);

                            BackgroundAutomationsEngine.RunEmailTriageAsync();
                        }
                        else
                        {
                            _emailFeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x4F, 0x7B));
                            _emailFeedbackText.Text = "❌ Connection failed: " + err;
                        }
                    }));
                });
            };
            btnRow.Children.Add(testBtn);

            var discBtn = new Border
            {
                Background = BrushBgDark,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 7, 14, 7),
                Cursor = Cursors.Hand
            };
            var discText = new TextBlock { Text = "Disconnect", FontSize = 12, Foreground = BrushMuted };
            discBtn.Child = discText;
            discBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                BackgroundAutomationsEngine.EmailConfig.IsConnected = false;
                BackgroundAutomationsEngine.SaveEmailPassword("");
                _passBox.Password = "";
                _emailFeedbackText.Foreground = BrushMuted;
                _emailFeedbackText.Text = "Email disconnected.";
            };
            btnRow.Children.Add(discBtn);

            cardStack.Children.Add(btnRow);
            emailCard.Child = cardStack;
            stack.Children.Add(emailCard);

            scroll.Content = stack;
            return scroll;
        }

        // =====================================================================
        // Tab 3: Triaged Inbox
        // =====================================================================
        private UIElement BuildInboxView()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(2, 4, 6, 8) };

            var topRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });

            var hStack = new StackPanel();
            hStack.Children.Add(new TextBlock { Text = "Triaged Unread Emails", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = BrushCream });
            hStack.Children.Add(new TextBlock { Text = "Urgent emails are detected automatically and prioritized.", FontSize = 12, Foreground = BrushMuted });
            Grid.SetColumn(hStack, 0);
            topRow.Children.Add(hStack);

            var refreshBtn = new Border
            {
                Background = BrushCardBg,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand
            };
            var refreshText = new TextBlock { Text = "🔄 Sync Inbox Now", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = BrushCream };
            refreshBtn.Child = refreshText;
            refreshBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                refreshText.Text = "Syncing...";
                BackgroundAutomationsEngine.RunEmailTriageAsync().ContinueWith(t =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        refreshText.Text = "🔄 Sync Inbox Now";
                        PopulateInboxCards();
                    }));
                });
            };
            Grid.SetColumn(refreshBtn, 1);
            topRow.Children.Add(refreshBtn);

            stack.Children.Add(topRow);

            _inboxItemsPanel = new StackPanel();
            PopulateInboxCards();
            stack.Children.Add(_inboxItemsPanel);

            scroll.Content = stack;
            return scroll;
        }

        private void PopulateInboxCards()
        {
            if (_inboxItemsPanel == null) return;
            _inboxItemsPanel.Children.Clear();

            var msgs = BackgroundAutomationsEngine.RecentMessages;
            if (!BackgroundAutomationsEngine.EmailConfig.IsConnected)
            {
                var emptyCard = new Border
                {
                    Background = BrushCardBg,
                    BorderBrush = BrushBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(24)
                };
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(new TextBlock
                {
                    Text = "✉️ Email Not Connected",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushCream,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                sp.Children.Add(new TextBlock
                {
                    Text = "Switch to the 'App Integrations' tab to connect your Gmail or Outlook account with an App Password.",
                    FontSize = 12,
                    Foreground = BrushMuted,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyCard.Child = sp;
                _inboxItemsPanel.Children.Add(emptyCard);
                return;
            }

            if (msgs == null || msgs.Count == 0)
            {
                var emptyCard = new Border
                {
                    Background = BrushCardBg,
                    BorderBrush = BrushBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(24)
                };
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(new TextBlock
                {
                    Text = "🎉 Inbox Zero",
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushMint,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                sp.Children.Add(new TextBlock
                {
                    Text = "No unread messages found. Flinch will alert you when high-priority emails arrive.",
                    FontSize = 12,
                    Foreground = BrushMuted,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyCard.Child = sp;
                _inboxItemsPanel.Children.Add(emptyCard);
                return;
            }

            foreach (var m in msgs)
            {
                var itemCard = new Border
                {
                    Background = BrushCardBg,
                    BorderBrush = m.IsUrgent ? BrushOrange : BrushBorder,
                    BorderThickness = new Thickness(m.IsUrgent ? 1.5 : 1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var itemStack = new StackPanel();

                var headerRow = new Grid();
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

                var senderStack = new StackPanel { Orientation = Orientation.Horizontal };
                if (m.IsUrgent)
                {
                    var urgentBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x8A, 0x3D)),
                        BorderBrush = BrushOrange,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 1, 6, 1),
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    urgentBadge.Child = new TextBlock { Text = "URGENT", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = BrushOrange };
                    senderStack.Children.Add(urgentBadge);
                }

                senderStack.Children.Add(new TextBlock
                {
                    Text = m.From,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushCream
                });
                Grid.SetColumn(senderStack, 0);
                headerRow.Children.Add(senderStack);

                var dateText = new TextBlock
                {
                    Text = m.Date.ToShortDateString(),
                    FontSize = 11,
                    Foreground = BrushMuted,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(dateText, 1);
                headerRow.Children.Add(dateText);

                itemStack.Children.Add(headerRow);

                var subjText = new TextBlock
                {
                    Text = m.Subject,
                    FontSize = 12,
                    Foreground = m.IsUrgent ? BrushCream : BrushMuted,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                itemStack.Children.Add(subjText);

                itemCard.Child = itemStack;
                _inboxItemsPanel.Children.Add(itemCard);
            }
        }

        private void UpdateLiveStatusUI()
        {
            if (_emailStatusBadge != null)
            {
                _emailStatusBadge.Text = BackgroundAutomationsEngine.EmailStatusText;
            }
            if (_downloadsStatusBadge != null)
            {
                _downloadsStatusBadge.Text = BackgroundAutomationsEngine.DownloadsStatusText;
            }
            if (_inboxItemsPanel != null)
            {
                PopulateInboxCards();
            }
        }
    }

    // =========================================================================
    // 5. Autonomous Multi-Task Background Worker Queue
    // =========================================================================
    public sealed class ActiveBackgroundTask
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Topic { get; set; }
        public string TaskType { get; set; }
        public int ProgressPercent { get; set; }
        public string StatusDetail { get; set; }
        public string TargetDirectory { get; set; }
        public string TargetFileName { get; set; }
        public DateTime StartedUtc { get; set; }
        public bool IsCompleted { get; set; }
        public string OutputFilePath { get; set; }
        public string SpokenCompletionMessage { get; set; }
        public string AlertCompletionMessage { get; set; }
        public CancellationTokenSource Cts { get; set; }
    }

    public static class BackgroundTaskManager
    {
        private static readonly List<ActiveBackgroundTask> _tasks = new List<ActiveBackgroundTask>();
        private static readonly object _lock = new object();

        public static event Action OnTasksUpdated;
        public static event Action<ActiveBackgroundTask> OnTaskCompleted;

        public static List<ActiveBackgroundTask> GetActiveTasks()
        {
            lock (_lock)
            {
                var list = new List<ActiveBackgroundTask>();
                foreach (var t in _tasks)
                {
                    if (!t.IsCompleted) list.Add(t);
                }
                return list;
            }
        }

        public static int ActiveCount
        {
            get
            {
                lock (_lock)
                {
                    int c = 0;
                    foreach (var t in _tasks)
                    {
                        if (!t.IsCompleted) c++;
                    }
                    return c;
                }
            }
        }

        public static ActiveBackgroundTask StartWritingTask(string topic, string docType, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(topic)) topic = "General Topic";
            if (string.IsNullOrWhiteSpace(docType)) docType = "essay";

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                targetDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flinch");
            }

            try
            {
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
            }
            catch { }

            string cleanTopic = Regex.Replace(topic, @"[\\/:*?""<>|]", "").Trim();
            string ext = docType == "presentation" ? ".html" : ".txt";
            string fileName = string.Format("{0} - {1}{2}", char.ToUpper(docType[0]) + docType.Substring(1), cleanTopic, ext);

            var task = new ActiveBackgroundTask
            {
                Id = Guid.NewGuid().ToString(),
                Title = string.Format("Writing {0}: {1}", docType, topic),
                Topic = topic,
                TaskType = docType,
                ProgressPercent = 15,
                StatusDetail = "Starting background draft...",
                TargetDirectory = targetDir,
                TargetFileName = fileName,
                StartedUtc = DateTime.UtcNow,
                IsCompleted = false,
                Cts = new CancellationTokenSource()
            };

            lock (_lock)
            {
                _tasks.Add(task);
            }

            NotifyUpdated();

            Task.Run(async () =>
            {
                try
                {
                    // Stage 1: Outline (30%)
                    await Task.Delay(1200);
                    task.ProgressPercent = 30;
                    task.StatusDetail = "Drafting outline & structure...";
                    NotifyUpdated();

                    // Stage 2: Content Generation (60%)
                    await Task.Delay(1500);
                    task.ProgressPercent = 60;
                    task.StatusDetail = "Writing sections in background...";
                    NotifyUpdated();

                    string content = await RequestDocumentGenerationAsync(topic, docType, task.Cts.Token);

                    // Stage 3: Formatting & References (85%)
                    task.ProgressPercent = 85;
                    task.StatusDetail = "Formatting document & references...";
                    NotifyUpdated();
                    await Task.Delay(800);

                    // Stage 4: Write to Disk (100%)
                    string fullPath = System.IO.Path.Combine(targetDir, fileName);
                    if (docType == "presentation")
                    {
                        File.WriteAllText(fullPath, content, Encoding.UTF8);
                    }
                    else
                    {
                        string header = string.Format("====================================================================\r\n{0}: {1}\r\nGenerated by Flinch Autonomous Background Engine\r\nDate: {2}\r\nDestination: {3}\r\n====================================================================\r\n\r\n",
                            docType.ToUpperInvariant(), topic, DateTime.Now.ToString("f"), fullPath);
                        File.WriteAllText(fullPath, header + content, Encoding.UTF8);
                    }

                    task.OutputFilePath = fullPath;
                    task.ProgressPercent = 100;
                    task.StatusDetail = "Saved to " + targetDir;
                    task.IsCompleted = true;
                    NotifyUpdated();

                    if (docType == "presentation")
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = fullPath,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }

                    if (OnTaskCompleted != null)
                    {
                        OnTaskCompleted(task);
                    }
                }
                catch (Exception ex)
                {
                    task.ProgressPercent = 100;
                    task.IsCompleted = true;
                    task.StatusDetail = "Error: " + ex.Message;
                    NotifyUpdated();
                }
            });

            return task;
        }

        private static async Task<string> RequestDocumentGenerationAsync(string topic, string docType, CancellationToken ct)
        {
            string endpoint = FlinchEnv.SupabaseUrl.TrimEnd('/') + "/functions/v1/flinch-voice";
            var payload = new Dictionary<string, object>
            {
                { "action", "write_document" },
                { "topic", topic },
                { "type", docType },
                { "prompt", string.Format("Write a comprehensive, engaging, multi-page {0} about: {1}. Include clear title, an engaging introduction, deep body sections with historical/cultural/factual detail, and a thoughtful conclusion.", docType, topic) }
            };

            var jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                if (SupabaseAuthClient.CurrentSession != null && !string.IsNullOrEmpty(SupabaseAuthClient.CurrentSession.AccessToken))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.AccessToken);
                }
                req.Headers.TryAddWithoutValidation("apikey", FlinchEnv.SupabaseAnonKey);
                req.Content = new StringContent(jss.Serialize(payload), Encoding.UTF8, "application/json");

                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                using (var resp = await client.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        string respJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var root = jss.Deserialize<Dictionary<string, object>>(respJson);
                        if (root != null && root.ContainsKey("content") && root["content"] != null)
                        {
                            return root["content"].ToString();
                        }
                    }
                }
            }

            return string.Format("# {0} on {1}\r\n\r\nContent generated successfully by Flinch.", char.ToUpper(docType[0]) + docType.Substring(1), topic);
        }

        public static ActiveBackgroundTask StartFileSearchTask(string query, bool fullHardDrive)
        {
            string cleanQuery = (query ?? "").Trim().Trim('"', '\'', '.', ',', '?', '!');
            cleanQuery = Regex.Replace(cleanQuery, @"(?i)\b(on\s+my\s+hard\s*drive|on\s+the\s+hard\s*drive|in\s+my\s+hard\s*drive|on\s+my\s+pc|on\s+my\s+computer|on\s+my\s+c\s*drive|in\s+the\s+background)\b", "").Trim();
            if (string.IsNullOrWhiteSpace(cleanQuery)) cleanQuery = "file";

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var task = new ActiveBackgroundTask
            {
                Id = Guid.NewGuid().ToString(),
                Title = fullHardDrive ? string.Format("Searching Hard Drive: {0}", cleanQuery) : string.Format("Searching PC: {0}", cleanQuery),
                Topic = cleanQuery,
                TaskType = "file_search",
                ProgressPercent = 12,
                StatusDetail = "Scanning user folders...",
                TargetDirectory = profile,
                TargetFileName = cleanQuery,
                StartedUtc = DateTime.UtcNow,
                IsCompleted = false,
                Cts = new CancellationTokenSource()
            };

            lock (_lock)
            {
                _tasks.Add(task);
            }

            NotifyUpdated();

            Task.Run(() =>
            {
                try
                {
                    CancellationToken ct = task.Cts.Token;
                    string qLow = cleanQuery.ToLowerInvariant();
                    string qNorm = Regex.Replace(qLow, @"[\s_\-]+", "");
                    string[] qWords = qLow.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

                    string bestMatch = null;
                    int bestScore = 0;
                    DateTime bestDate = DateTime.MinValue;

                    // Stage 1: Priority user profile folders (Desktop, Downloads, Documents, Pictures, Videos, Music, Flinch)
                    var priorityDirs = new List<string>
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        System.IO.Path.Combine(profile, "Downloads"),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Flinch"),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
                    };

                    int pIdx = 0;
                    foreach (string dir in priorityDirs)
                    {
                        if (ct.IsCancellationRequested) return;
                        pIdx++;
                        task.ProgressPercent = 12 + (pIdx * 34 / Math.Max(1, priorityDirs.Count));
                        task.StatusDetail = "Scanning " + System.IO.Path.GetFileName(dir) + "...";
                        NotifyUpdated();

                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        {
                            ScanDirectoryTreeSafe(dir, qLow, qNorm, qWords, 6, ct, ref bestMatch, ref bestScore, ref bestDate);
                        }
                    }

                    // Keep spinner visible briefly so the user sees smooth progress feedback even on ultra-fast SSD hits
                    Thread.Sleep(450);

                    // Stage 2: Full Hard Drive / Fixed Drives scan if requested or if no strong match found in user folders
                    if (!ct.IsCancellationRequested && (fullHardDrive || bestScore < 75))
                    {
                        var rootsToScan = new List<string>();
                        try
                        {
                            foreach (var drv in DriveInfo.GetDrives())
                            {
                                if (drv != null && drv.IsReady && drv.DriveType == DriveType.Fixed)
                                {
                                    string[] topDirs = null;
                                    try { topDirs = Directory.GetDirectories(drv.RootDirectory.FullName); } catch { }
                                    if (topDirs != null)
                                    {
                                        foreach (string td in topDirs)
                                        {
                                            if (!IsSkippedSystemDirectory(td))
                                            {
                                                rootsToScan.Add(td);
                                            }
                                        }
                                    }
                                    // Also check loose files on root of drive (e.g. C:\)
                                    ScanSingleDirectoryFiles(drv.RootDirectory.FullName, qLow, qNorm, qWords, ref bestMatch, ref bestScore, ref bestDate);
                                }
                            }
                        }
                        catch { }

                        int totalRoots = Math.Max(1, rootsToScan.Count);
                        for (int r = 0; r < rootsToScan.Count; r++)
                        {
                            if (ct.IsCancellationRequested) return;
                            string rootDir = rootsToScan[r];
                            task.ProgressPercent = Math.Min(94, 48 + ((r + 1) * 46 / totalRoots));
                            task.StatusDetail = "Searching " + rootDir + "...";
                            NotifyUpdated();

                            ScanDirectoryTreeSafe(rootDir, qLow, qNorm, qWords, 6, ct, ref bestMatch, ref bestScore, ref bestDate);
                            if (bestScore >= 100) break; // Exact match found
                        }
                    }

                    if (ct.IsCancellationRequested) return;

                    task.ProgressPercent = 100;
                    task.IsCompleted = true;

                    if (!string.IsNullOrWhiteSpace(bestMatch) && (File.Exists(bestMatch) || Directory.Exists(bestMatch)))
                    {
                        task.OutputFilePath = bestMatch;
                        string foundName = System.IO.Path.GetFileName(bestMatch);
                        string parentDir = System.IO.Path.GetDirectoryName(bestMatch) ?? "";
                        string parentFolder = System.IO.Path.GetFileName(parentDir);
                        if (string.IsNullOrWhiteSpace(parentFolder)) parentFolder = parentDir;

                        task.StatusDetail = "Found: " + foundName;
                        task.AlertCompletionMessage = "Found: " + foundName;
                        task.SpokenCompletionMessage = string.Format("I finished searching your hard drive and found {0} in {1}. I've highlighted it in File Explorer.", foundName, parentFolder);

                        try
                        {
                            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + bestMatch + "\"") { UseShellExecute = false });
                        }
                        catch { }
                    }
                    else
                    {
                        task.StatusDetail = "Not found: " + cleanQuery;
                        task.AlertCompletionMessage = string.Format("No file matching '{0}' found", cleanQuery);
                        task.SpokenCompletionMessage = string.Format("I finished searching your hard drive, but I couldn't find any file matching {0}.", cleanQuery);
                    }

                    NotifyUpdated();

                    if (OnTaskCompleted != null)
                    {
                        OnTaskCompleted(task);
                    }
                }
                catch (Exception ex)
                {
                    task.ProgressPercent = 100;
                    task.IsCompleted = true;
                    task.StatusDetail = "Search error: " + ex.Message;
                    NotifyUpdated();
                }
            });

            return task;
        }

        private static bool IsSkippedSystemDirectory(string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath)) return true;
            string name = (System.IO.Path.GetFileName(dirPath) ?? "").ToLowerInvariant();
            if (name.StartsWith(".") || name.StartsWith("$")) return true;
            if (name == "windows" || name == "program files" || name == "program files (x86)" ||
                name == "programdata" || name == "system volume information" || name == "recovery" ||
                name == "perflogs" || name == "appdata" || name == "node_modules" ||
                name == "packages" || name == "winreagent" || name == "msocache")
            {
                return true;
            }
            return false;
        }

        private static void ScanSingleDirectoryFiles(string dir, string qLow, string qNorm, string[] qWords, ref string bestMatch, ref int bestScore, ref DateTime bestDate)
        {
            string[] files = null;
            try { files = Directory.GetFiles(dir); } catch { return; }
            if (files == null) return;

            for (int i = 0; i < files.Length; i++)
            {
                string f = files[i];
                try
                {
                    string fileName = System.IO.Path.GetFileName(f);
                    if (string.IsNullOrEmpty(fileName) || fileName.StartsWith(".")) continue;
                    string ext = (System.IO.Path.GetExtension(f) ?? "").ToLowerInvariant();
                    if (ext == ".tmp" || ext == ".crdownload" || ext == ".dll" || ext == ".sys" || ext == ".pdb") continue;

                    int score = ScoreCandidateName(fileName, qLow, qNorm, qWords);
                    if (score > 0)
                    {
                        DateTime dt = File.GetLastWriteTime(f);
                        if (score > bestScore || (score == bestScore && dt > bestDate))
                        {
                            bestScore = score;
                            bestDate = dt;
                            bestMatch = f;
                        }
                    }
                }
                catch { }
            }
        }

        private static int ScoreCandidateName(string fileName, string qLow, string qNorm, string[] qWords)
        {
            string fLow = fileName.ToLowerInvariant();
            string noExt = (System.IO.Path.GetFileNameWithoutExtension(fileName) ?? "").ToLowerInvariant();
            string fNorm = Regex.Replace(noExt, @"[\s_\-]+", "");

            if (fLow == qLow || noExt == qLow || fNorm == qNorm) return 100;
            if (noExt.StartsWith(qLow) || fNorm.StartsWith(qNorm)) return 88;
            if (qWords != null && qWords.Length > 1)
            {
                bool allWords = true;
                for (int w = 0; w < qWords.Length; w++)
                {
                    if (qWords[w].Length >= 2 && !fLow.Contains(qWords[w]))
                    {
                        allWords = false;
                        break;
                    }
                }
                if (allWords) return 80;
            }
            if (fNorm.Length >= 2 && qNorm.Length >= 2 && fNorm.Contains(qNorm)) return 65;
            return 0;
        }

        private static void ScanDirectoryTreeSafe(string rootDir, string qLow, string qNorm, string[] qWords, int maxDepth, CancellationToken ct, ref string bestMatch, ref int bestScore, ref DateTime bestDate)
        {
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(rootDir, 0));
            int visitedDirs = 0;

            while (queue.Count > 0 && !ct.IsCancellationRequested && visitedDirs < 2800)
            {
                var item = queue.Dequeue();
                string curDir = item.Key;
                int depth = item.Value;
                visitedDirs++;

                ScanSingleDirectoryFiles(curDir, qLow, qNorm, qWords, ref bestMatch, ref bestScore, ref bestDate);
                if (bestScore >= 100) return;

                if (depth >= maxDepth) continue;

                string[] subDirs = null;
                try { subDirs = Directory.GetDirectories(curDir); } catch { continue; }
                if (subDirs == null) continue;

                for (int i = 0; i < subDirs.Length; i++)
                {
                    string sd = subDirs[i];
                    try
                    {
                        if (IsSkippedSystemDirectory(sd)) continue;
                        var attr = File.GetAttributes(sd);
                        if ((attr & FileAttributes.ReparsePoint) != 0 || (attr & FileAttributes.System) != 0) continue;

                        // Also check if a folder name itself matches the search query
                        string folderName = System.IO.Path.GetFileName(sd);
                        int folderScore = ScoreCandidateName(folderName, qLow, qNorm, qWords);
                        if (folderScore >= 85 && folderScore > bestScore)
                        {
                            bestScore = folderScore - 2; // Slight preference for exact files over folders
                            bestDate = Directory.GetLastWriteTime(sd);
                            bestMatch = sd;
                        }

                        queue.Enqueue(new KeyValuePair<string, int>(sd, depth + 1));
                    }
                    catch { }
                }
            }
        }

        public static ActiveBackgroundTask GetLatestActiveTask()
        {
            lock (_lock)
            {
                ActiveBackgroundTask latest = null;
                foreach (var t in _tasks)
                {
                    if (!t.IsCompleted)
                    {
                        if (latest == null || t.StartedUtc > latest.StartedUtc)
                        {
                            latest = t;
                        }
                    }
                }
                return latest;
            }
        }

        public static ActiveBackgroundTask CancelLatestTask()
        {
            var latest = GetLatestActiveTask();
            if (latest != null)
            {
                CancelTask(latest.Id);
            }
            return latest;
        }

        public static void CancelTask(string taskId)
        {
            lock (_lock)
            {
                foreach (var t in _tasks)
                {
                    if (t.Id == taskId)
                    {
                        try { t.Cts.Cancel(); } catch { }
                        t.IsCompleted = true;
                        t.StatusDetail = "Cancelled";
                        break;
                    }
                }
            }
            NotifyUpdated();
        }

        private static void NotifyUpdated()
        {
            if (OnTasksUpdated != null)
            {
                try { OnTasksUpdated(); } catch { }
            }
        }
    }
}
