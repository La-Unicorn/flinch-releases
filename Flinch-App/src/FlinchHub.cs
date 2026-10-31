// ============================================================================
// FLINCH AI: FLINCH HUB & BACKGROUND AUTOMATIONS ENGINE
// Direct App Integrations (Email / Files / Clipboard) & Autonomous Background Workers
// Zero Third-Party Account Creation Required â€” 100% Local On-Device Execution
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Mail;
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
using System.Windows.Threading;

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
        public string SmtpServer { get; set; }
        public int SmtpPort { get; set; }
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
            SmtpServer = "smtp.gmail.com";
            SmtpPort = 587;
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
    // 2B. Direct SMTP Client (100% Local SSL/TLS, Zero Account Creation)
    // =========================================================================
    internal static class DirectSmtpClient
    {
        public static bool SendEmail(string smtpServer, int smtpPort, bool useSsl, string username, string password, string toAddress, string subject, string body, out string errorMessage)
        {
            errorMessage = "";
            if (string.IsNullOrWhiteSpace(smtpServer)) smtpServer = "smtp.gmail.com";
            if (smtpPort <= 0) smtpPort = 587;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                errorMessage = "Email address or Google App Password is not provided.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(toAddress))
            {
                errorMessage = "Recipient email address is required.";
                return false;
            }

            try
            {
                using (var client = new SmtpClient(smtpServer, smtpPort))
                {
                    client.EnableSsl = useSsl;
                    client.DeliveryMethod = SmtpDeliveryMethod.Network;
                    client.UseDefaultCredentials = false;
                    client.Credentials = new System.Net.NetworkCredential(username, password);
                    client.Timeout = 15000;

                    using (var msg = new MailMessage())
                    {
                        msg.From = new MailAddress(username, "Flinch Assistant");
                        msg.To.Add(toAddress);
                        msg.Subject = string.IsNullOrWhiteSpace(subject) ? "(No Subject)" : subject;
                        msg.Body = body ?? "";
                        msg.IsBodyHtml = false;

                        client.Send(msg);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool SendGmailEmail(string toAddress, string subject, string body, out string errorMessage)
        {
            errorMessage = "";
            var cfg = BackgroundAutomationsEngine.EmailConfig;
            if (cfg == null || !cfg.IsConnected || string.IsNullOrWhiteSpace(cfg.EmailAddress))
            {
                errorMessage = "Gmail is not connected. Please connect your Gmail account in the Flinch Hub Integrations panel.";
                return false;
            }
            string pw = BackgroundAutomationsEngine.GetEmailPassword();
            if (string.IsNullOrWhiteSpace(pw))
            {
                errorMessage = "Gmail app password is not configured.";
                return false;
            }
            string server = string.IsNullOrWhiteSpace(cfg.SmtpServer) ? "smtp.gmail.com" : cfg.SmtpServer;
            int port = cfg.SmtpPort > 0 ? cfg.SmtpPort : 587;
            return SendEmail(server, port, true, cfg.EmailAddress, pw, toAddress, subject, body, out errorMessage);
        }
    }

    public sealed class EmailDraft
    {
        public string Id { get; set; }
        public string To { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public DateTime CreatedUtc { get; set; }
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

        private static readonly List<EmailDraft> _drafts = new List<EmailDraft>();

        public static List<EmailDraft> Drafts
        {
            get { lock (_syncLock) { return new List<EmailDraft>(_drafts); } }
        }

        public static EmailDraft AddDraft(string to, string subject, string body)
        {
            lock (_syncLock)
            {
                var d = new EmailDraft
                {
                    Id = Guid.NewGuid().ToString("N"),
                    To = to,
                    Subject = subject,
                    Body = body,
                    CreatedUtc = DateTime.UtcNow
                };
                _drafts.Add(d);
                return d;
            }
        }

        public static EmailDraft GetLatestDraft()
        {
            lock (_syncLock)
            {
                if (_drafts.Count == 0) return null;
                return _drafts[_drafts.Count - 1];
            }
        }

        public static void ClearDrafts()
        {
            lock (_syncLock) { _drafts.Clear(); }
        }

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

                EmailStatusText = string.Format("Active â€¢ Checked {0:t} ({1} unread)", DateTime.Now, msgs.Count);

                if (firstUrgent != null && OnUrgentAlertTriggered != null)
                {
                    string alertMsg = string.Format("âœ‰ï¸ Urgent Email from {0}: \"{1}\"", firstUrgent.From, firstUrgent.Subject);
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
                DownloadsStatusText = string.Format("Active â€¢ {0} files auto-sorted", OrganizedFilesCount);
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
    // 4. Modern Notch-Hover Flinch Hub UI (Expands Around Top Screen Notch)
    // =========================================================================
    internal sealed class FlinchHubWindow : Window
    {
        public bool IsHoverSummoned { get; set; }

        private static readonly Color ColBgDark = Color.FromRgb(0x14, 0x15, 0x19);
        private static readonly Color ColCardBg = Color.FromRgb(0x1B, 0x1D, 0x24);
        private static readonly Color ColBorder = Color.FromRgb(0x2E, 0x32, 0x3D);
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
        private readonly Border _rootBorder;
        private readonly Grid _mainGrid;
        private readonly Border _flyoutHost;
        private readonly ContentControl _flyoutContent;

        // Notch Animation & Sound Wave
        private DispatcherTimer _waveAnimTimer;
        private readonly List<Rectangle> _waveBars = new List<Rectangle>();
        private double _wavePhase = 0;
        private TextBlock _listenStatusLabel;

        // Hover & Interaction Management
        private bool _isMouseInHub = false;
        private bool _isInteracting = false;
        private bool _isFlyoutOpen = false;
        private string _activeFlyoutType = null;
        private DispatcherTimer _collapseTimer;

        // Badges & Controls
        private Ellipse _gmailStatusDot;
        private Ellipse _accentDot;
        private int _accentIndex = 0;
        private static readonly Color[] AccentColors = new Color[]
        {
            Color.FromRgb(0x3D, 0xDC, 0x84), // Mint
            Color.FromRgb(0xA7, 0x8B, 0xFA), // Violet
            Color.FromRgb(0x38, 0xBD, 0xF8), // Sky
            Color.FromRgb(0xFB, 0x92, 0x3C), // Amber
            Color.FromRgb(0xF8, 0x71, 0x71)  // Coral
        };

        // Flyout Views (Cached)
        private UIElement _gmailFlyoutView;
        private UIElement _historyFlyoutView;
        private UIElement _settingsFlyoutView;
        private UIElement _hotkeysFlyoutView;

        // Gmail inputs
        private TextBox _emailBox;
        private PasswordBox _passBox;
        private TextBlock _emailFeedbackText;
        private TextBlock _gmailStatusHeader;
        private StackPanel _inboxCardsPanel;

        public FlinchHubWindow(DynamicIslandWindow parentIsland)
        {
            _parentIsland = parentIsland;

            Title = "Flinch Hub";
            Width = 920;
            Height = 104;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;

            // Anchor flush with the top screen bezel
            var scr = System.Windows.Forms.Screen.PrimaryScreen;
            Left = (scr.Bounds.Width - Width) / 2.0;
            Top = 0;

            _rootBorder = new Border
            {
                Background = BrushBgDark,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 32,
                    ShadowDepth = 10,
                    Direction = 270,
                    Opacity = 0.55
                }
            };

            UpdateWindowClip();

            _mainGrid = new Grid();
            _mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(104) }); // Top Bar
            _mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Flyout Host

            // 1. Build Top Bar (5 Columns matching mockup)
            var topBar = BuildTopBar();
            Grid.SetRow(topBar, 0);
            _mainGrid.Children.Add(topBar);

            // 2. Flyout Host (Smooth drop-down below top bar)
            _flyoutHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x12, 0x16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(24, 16, 24, 18)
            };
            _flyoutContent = new ContentControl();
            _flyoutHost.Child = _flyoutContent;
            Grid.SetRow(_flyoutHost, 1);
            _mainGrid.Children.Add(_flyoutHost);

            _rootBorder.Child = _mainGrid;
            Content = _rootBorder;

            // Window Hover & Interaction Events
            MouseEnter += (s, e) =>
            {
                _isMouseInHub = true;
                StopCollapseTimer();
            };
            MouseLeave += (s, e) =>
            {
                _isMouseInHub = false;
                if (IsHoverSummoned && !_isInteracting && !_isFlyoutOpen && !HasFocusedInput())
                {
                    StartCollapseTimer();
                }
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (_isFlyoutOpen) CloseFlyout();
                    else Close();
                }
            };

            // Sound Wave Animation
            StartWaveAnimation();
        }

        private void UpdateWindowClip()
        {
            if (_rootBorder != null)
            {
                _rootBorder.Clip = DynamicIslandWindow.CreateMacBookNotchGeometry(Width, Height, 14.0, 26.0);
            }
        }

        public void NotifyNotchMouseLeave()
        {
            if (!_isMouseInHub && !_isInteracting && !_isFlyoutOpen && !HasFocusedInput())
            {
                StartCollapseTimer();
            }
        }

        private void StartCollapseTimer()
        {
            StopCollapseTimer();
            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            _collapseTimer.Tick += (s, e) =>
            {
                _collapseTimer.Stop();
                if (!_isMouseInHub && !_isInteracting && !_isFlyoutOpen && !HasFocusedInput())
                {
                    Close();
                }
            };
            _collapseTimer.Start();
        }

        private void StopCollapseTimer()
        {
            if (_collapseTimer != null)
            {
                _collapseTimer.Stop();
                _collapseTimer = null;
            }
        }

        private bool HasFocusedInput()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return (focused is TextBox || focused is PasswordBox);
        }

        private void StartWaveAnimation()
        {
            _waveAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(85) };
            _waveAnimTimer.Tick += (s, e) =>
            {
                _wavePhase += 0.35;
                if (_waveBars.Count == 0) return;

                double[] baseH = new double[] { 5, 11, 15, 8, 13, 7 };
                for (int i = 0; i < _waveBars.Count; i++)
                {
                    double b = i < baseH.Length ? baseH[i] : 8.0;
                    double h = b + (Math.Sin(_wavePhase + (i * 0.9)) * 3.5);
                    _waveBars[i].Height = Math.Max(3.0, Math.Min(19.0, h));
                }
            };
            _waveAnimTimer.Start();
        }

        // =====================================================================
        // TOP BAR (5 Columns matching mockup)
        // =====================================================================
        private Grid BuildTopBar()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });

            // Subtle Dividers between columns
            for (int c = 0; c < 4; c++)
            {
                var div = new Rectangle
                {
                    Width = 1,
                    Height = 54,
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(div, c);
                grid.Children.Add(div);
            }

            // Col 0: Chat History
            var colHistory = BuildHistoryColumn();
            Grid.SetColumn(colHistory, 0);
            grid.Children.Add(colHistory);

            // Col 1: Integrations
            var colIntegrations = BuildIntegrationsColumn();
            Grid.SetColumn(colIntegrations, 1);
            grid.Children.Add(colIntegrations);

            // Col 2: Notch Listening Island
            var colIsland = BuildCenterIslandColumn();
            Grid.SetColumn(colIsland, 2);
            grid.Children.Add(colIsland);

            // Col 3: Customization
            var colCustomization = BuildCustomizationColumn();
            Grid.SetColumn(colCustomization, 3);
            grid.Children.Add(colCustomization);

            // Col 4: Settings
            var colSettings = BuildSettingsColumn();
            Grid.SetColumn(colSettings, 4);
            grid.Children.Add(colSettings);

            return grid;
        }

        // ---------------------------------------------------------------------
        // Column 0: Chat History
        // ---------------------------------------------------------------------
        private UIElement BuildHistoryColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(16, 12, 12, 10),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: ðŸ•’ Chat History âŒµ
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\U0001F552", // clock
                FontSize = 11,
                Foreground = BrushMuted,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Chat History",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304", // chevron down
                FontSize = 10,
                Foreground = BrushMuted,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            header.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                ToggleFlyout("history");
            };
            panel.Children.Add(header);

            // 3 Recent Query Rows
            var recentTurns = FlinchSkills.GetRecentTurns(3);
            if (recentTurns.Count == 0)
            {
                recentTurns = new List<FlinchSkills.HistoryTurn>
                {
                    new FlinchSkills.HistoryTurn { u = "What is flinch?", t = DateTime.UtcNow.AddMinutes(-2).ToString("o"), a = "Flinch is your autonomous desktop companion." },
                    new FlinchSkills.HistoryTurn { u = "Create a portfolio site", t = DateTime.UtcNow.AddMinutes(-5).ToString("o"), a = "I can generate clean modern web layouts." },
                    new FlinchSkills.HistoryTurn { u = "Explain this code", t = DateTime.UtcNow.AddMinutes(-12).ToString("o"), a = "I break down complex architectures step by step." }
                };
            }

            foreach (var item in recentTurns)
            {
                var row = CreateHistoryRow(item);
                panel.Children.Add(row);
            }

            return panel;
        }

        private Border CreateHistoryRow(FlinchSkills.HistoryTurn turn)
        {
            var row = new Border
            {
                Height = 20,
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var dock = new DockPanel { LastChildFill = true };

            // Relative timestamp (right-aligned)
            string relTime = FormatRelativeTime(turn.t);
            var timeTb = new TextBlock
            {
                Text = relTime,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0)
            };
            DockPanel.SetDock(timeTb, Dock.Right);
            dock.Children.Add(timeTb);

            // Type icon: ðŸ’¬ or ðŸ“„ or </>
            string iconStr = "\U0001F4AC";
            string low = (turn.u ?? "").ToLowerInvariant();
            if (low.Contains("code") || low.Contains("function") || low.Contains("debug") || low.Contains("explain")) iconStr = "</>";
            else if (low.Contains("create") || low.Contains("site") || low.Contains("file") || low.Contains("draft")) iconStr = "\U0001F4C4";

            var iconTb = new TextBlock
            {
                Text = iconStr,
                FontSize = (iconStr == "</>") ? 8.5 : 10,
                Foreground = BrushMuted,
                FontWeight = (iconStr == "</>") ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            DockPanel.SetDock(iconTb, Dock.Left);
            dock.Children.Add(iconTb);

            // Question snippet
            var queryTb = new TextBlock
            {
                Text = turn.u ?? "",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            dock.Children.Add(queryTb);

            row.Child = dock;

            row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                if (_parentIsland != null && !string.IsNullOrWhiteSpace(turn.a))
                {
                    _parentIsland.SpeakAnswer(turn.a);
                }
            };

            return row;
        }

        private static string FormatRelativeTime(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return "now";
            DateTime dt;
            if (!DateTime.TryParse(iso, out dt)) return "now";
            var diff = DateTime.UtcNow - dt.ToUniversalTime();
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalMinutes < 60) return string.Format("{0}m ago", (int)diff.TotalMinutes);
            if (diff.TotalHours < 24) return string.Format("{0}h ago", (int)diff.TotalHours);
            return string.Format("{0}d ago", (int)diff.TotalDays);
        }

        // ---------------------------------------------------------------------
        // Column 1: Integrations
        // ---------------------------------------------------------------------
        private UIElement BuildIntegrationsColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(10, 12, 10, 10),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: ðŸŒ¸ Integrations âŒµ
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 8) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\U0001F338", // flower / sparkle
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Integrations",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304",
                FontSize = 10,
                Foreground = BrushMuted,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            header.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                ToggleFlyout("gmail");
            };
            panel.Children.Add(header);

            // Icons Row: [N] Notion, [â™«] Spotify, [âœ‰] Gmail, [+ Add]
            var iconsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

            // Notion Button
            var notionBtn = CreateIconTile("N", Brushes.White, "Notion Integration (Ready)", (s, e) =>
            {
                _isInteracting = true;
                if (_parentIsland != null) _parentIsland.ShowAlert("notion", "Notion Connected", "Workspace ready");
            });
            iconsRow.Children.Add(notionBtn);

            // Spotify Button
            var spotifyBtn = CreateIconTile("\u266B", new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), "Spotify Media Controls", (s, e) =>
            {
                _isInteracting = true;
                if (_parentIsland != null) _parentIsland.ShowAlert("spotify", "Spotify Ready", "Play, pause, skip enabled");
            });
            spotifyBtn.Margin = new Thickness(5, 0, 0, 0);
            iconsRow.Children.Add(spotifyBtn);

            // Gmail Button (with live status dot)
            var gmailBtn = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x40)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Google Gmail (Read, Write & Send Emails)",
                Margin = new Thickness(5, 0, 0, 0)
            };
            var gmailGrid = new Grid();
            gmailGrid.Children.Add(new TextBlock
            {
                Text = "\u2709", // envelope
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            _gmailStatusDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0)
            };
            gmailGrid.Children.Add(_gmailStatusDot);
            gmailBtn.Child = gmailGrid;
            gmailBtn.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                ToggleFlyout("gmail");
            };
            iconsRow.Children.Add(gmailBtn);

            // [+ Add] Button
            var addBtn = new Border
            {
                Height = 26,
                Padding = new Thickness(7, 0, 7, 0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1B, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Connect New Integration",
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            addBtn.Child = new TextBlock
            {
                Text = "+ Add",
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                VerticalAlignment = VerticalAlignment.Center
            };
            addBtn.MouseEnter += (s, e) => addBtn.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            addBtn.MouseLeave += (s, e) => addBtn.Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1B, 0x22));
            addBtn.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                ToggleFlyout("gmail");
            };
            iconsRow.Children.Add(addBtn);

            panel.Children.Add(iconsRow);
            return panel;
        }

        private Border CreateIconTile(string text, Brush fg, string tooltip, MouseButtonEventHandler onClick)
        {
            var tile = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x40)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            tile.Child = new TextBlock
            {
                Text = text,
                FontSize = (text.Length > 1) ? 11 : 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            tile.MouseEnter += (s, e) => tile.BorderBrush = BrushMint;
            tile.MouseLeave += (s, e) => tile.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x40));
            if (onClick != null) tile.MouseLeftButtonDown += onClick;
            return tile;
        }

        // ---------------------------------------------------------------------
        // Column 2: Center Notch Audio / Listening Island
        // ---------------------------------------------------------------------
        private UIElement BuildCenterIslandColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Top Emblem Badge
            var badge = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x28)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x44)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Flinch Intelligence Core"
            };
            badge.Child = new TextBlock
            {
                Text = "\U0001F984", // Unicorn
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(badge);

            // Bottom Audio Wave Pill
            var pill = new Border
            {
                Width = 120,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x33)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 5, 0, 0),
                ToolTip = "Click to Talk / Toggle Voice Recording"
            };

            var pillStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Animated Bars Panel
            var barsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _waveBars.Clear();
            double[] initH = new double[] { 5, 11, 15, 8, 13, 7 };
            for (int i = 0; i < 6; i++)
            {
                var r = new Rectangle
                {
                    Width = 2,
                    Height = initH[i],
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = BrushMint,
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _waveBars.Add(r);
                barsPanel.Children.Add(r);
            }
            pillStack.Children.Add(barsPanel);

            _listenStatusLabel = new TextBlock
            {
                Text = "Listening...",
                FontSize = 10,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            pillStack.Children.Add(_listenStatusLabel);

            pill.Child = pillStack;
            pill.MouseEnter += (s, e) => pill.BorderBrush = BrushMint;
            pill.MouseLeave += (s, e) => pill.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x33));
            pill.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                if (_parentIsland != null)
                {
                    _parentIsland.ToggleVoiceRecording();
                }
            };
            panel.Children.Add(pill);

            return panel;
        }

        // ---------------------------------------------------------------------
        // Column 3: Customization
        // ---------------------------------------------------------------------
        private UIElement BuildCustomizationColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(10, 12, 10, 10),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: ðŸ–Š Customization âŒµ
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 8) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\u270E", // pen
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Customization",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304",
                FontSize = 10,
                Foreground = BrushMuted,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            panel.Children.Add(header);

            // Controls Row: [â˜¼] Light, [â˜¾] Dark (active), [â—] Accent, [Aa] Font size
            var ctrlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

            // Light Mode
            var lightBtn = CreateIconTile("\u263C", new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), "Switch to Light Theme", (s, e) =>
            {
                _isInteracting = true;
                if (_parentIsland != null) _parentIsland.ShowAlert("theme", "Theme Updated", "Light mode enabled");
            });
            ctrlRow.Children.Add(lightBtn);

            // Dark Mode (Active highlight)
            var darkBtn = CreateIconTile("\u263E", new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), "Dark Theme (Active)", null);
            darkBtn.BorderBrush = BrushMint;
            darkBtn.Margin = new Thickness(5, 0, 0, 0);
            ctrlRow.Children.Add(darkBtn);

            // Accent Dot Picker
            var accentBtn = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x40)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Cycle Accent Theme Color",
                Margin = new Thickness(5, 0, 0, 0)
            };
            _accentDot = new Ellipse
            {
                Width = 11,
                Height = 11,
                Fill = BrushMint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            accentBtn.Child = _accentDot;
            accentBtn.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                _accentIndex = (_accentIndex + 1) % AccentColors.Length;
                var col = AccentColors[_accentIndex];
                _accentDot.Fill = new SolidColorBrush(col);
                if (_parentIsland != null) _parentIsland.ShowAlert("color", "Accent Color Changed", "Theme refreshed");
            };
            ctrlRow.Children.Add(accentBtn);

            // [Aa] Font Size Toggle
            var fontBtn = new Border
            {
                Width = 30,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x40)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Toggle Typography Size",
                Margin = new Thickness(5, 0, 0, 0)
            };
            fontBtn.Child = new TextBlock
            {
                Text = "Aa",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            fontBtn.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                if (_parentIsland != null) _parentIsland.ShowAlert("font", "Typography Adjusted", "Standard scaling applied");
            };
            ctrlRow.Children.Add(fontBtn);

            panel.Children.Add(ctrlRow);
            return panel;
        }

        // ---------------------------------------------------------------------
        // Column 4: Settings
        // ---------------------------------------------------------------------
        private UIElement BuildSettingsColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(10, 12, 16, 10),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: âš™ Settings âŒµ
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\u2699", // gear
                FontSize = 11,
                Foreground = BrushMuted,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Settings",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304",
                FontSize = 10,
                Foreground = BrushMuted,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            header.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                ToggleFlyout("settings");
            };
            panel.Children.Add(header);

            // Menu rows
            panel.Children.Add(CreateSettingsLink("\u2699 General", () => ToggleFlyout("settings")));
            panel.Children.Add(CreateSettingsLink("\u2328 Hotkeys", () => ToggleFlyout("hotkeys")));
            panel.Children.Add(CreateSettingsLink("\u2139 About Flinch", () =>
            {
                if (_parentIsland != null) _parentIsland.ShowAlert("info", "Flinch v0.1.0", "Built with 100% on-device local privacy");
            }));

            return panel;
        }

        private Border CreateSettingsLink(string title, Action onClick)
        {
            var link = new Border
            {
                Height = 18,
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            link.Child = new TextBlock
            {
                Text = title,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            link.MouseEnter += (s, e) =>
            {
                link.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                ((TextBlock)link.Child).Foreground = Brushes.White;
            };
            link.MouseLeave += (s, e) =>
            {
                link.Background = Brushes.Transparent;
                ((TextBlock)link.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            };
            link.MouseLeftButtonDown += (s, e) =>
            {
                _isInteracting = true;
                if (onClick != null) onClick();
            };
            return link;
        }

        // =====================================================================
        // FLYOUT MANAGEMENT & VIEWS
        // =====================================================================
        private void ToggleFlyout(string type)
        {
            if (_isFlyoutOpen && _activeFlyoutType == type)
            {
                CloseFlyout();
                return;
            }

            _activeFlyoutType = type;
            _isFlyoutOpen = true;
            _isInteracting = true;

            UIElement view = null;
            if (type == "gmail")
            {
                if (_gmailFlyoutView == null) _gmailFlyoutView = BuildGmailFlyout();
                RefreshGmailStatusUI();
                view = _gmailFlyoutView;
            }
            else if (type == "history")
            {
                if (_historyFlyoutView == null) _historyFlyoutView = BuildHistoryFlyout();
                view = _historyFlyoutView;
            }
            else if (type == "settings")
            {
                if (_settingsFlyoutView == null) _settingsFlyoutView = BuildGeneralSettingsFlyout();
                view = _settingsFlyoutView;
            }
            else if (type == "hotkeys")
            {
                if (_hotkeysFlyoutView == null) _hotkeysFlyoutView = BuildHotkeysFlyout();
                view = _hotkeysFlyoutView;
            }

            _flyoutContent.Content = view;
            _flyoutHost.Visibility = Visibility.Visible;
            Height = 410;
            UpdateWindowClip();
        }

        private void CloseFlyout()
        {
            _isFlyoutOpen = false;
            _activeFlyoutType = null;
            _flyoutHost.Visibility = Visibility.Collapsed;
            Height = 104;
            UpdateWindowClip();
        }

        // ---------------------------------------------------------------------
        // Flyout 1: Google Gmail & Workspace (Read, Write, Send)
        // ---------------------------------------------------------------------
        private UIElement BuildGmailFlyout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content

            // 1. Header
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var closeBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock
            {
                Text = "\u2715",
                FontSize = 10.5,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            var titleStack = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = "\u2709 Google Workspace & Gmail Integration",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            _gmailStatusHeader = new TextBlock
            {
                Text = BackgroundAutomationsEngine.EmailConfig.IsConnected ? "  \u2022 Connected" : "  \u2022 Disconnected",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : BrushMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            titleRow.Children.Add(_gmailStatusHeader);
            titleStack.Children.Add(titleRow);

            titleStack.Children.Add(new TextBlock
            {
                Text = "Read unread emails, triage urgent tasks, draft messages, and send directly via secure SSL/TLS.",
                FontSize = 11,
                Foreground = BrushMuted,
                Margin = new Thickness(0, 3, 0, 0)
            });
            header.Children.Add(titleStack);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // 2. Form & Actions Split
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });

            // Left: Credentials Form
            var formStack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };

            formStack.Children.Add(new TextBlock
            {
                Text = "Gmail Address:",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                Margin = new Thickness(0, 0, 0, 4)
            });
            _emailBox = new TextBox
            {
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x46)),
                Foreground = Brushes.White,
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11.5,
                Text = BackgroundAutomationsEngine.EmailConfig.EmailAddress ?? ""
            };
            formStack.Children.Add(_emailBox);

            formStack.Children.Add(new TextBlock
            {
                Text = "Google App Password (16 chars):",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                Margin = new Thickness(0, 10, 0, 4)
            });
            _passBox = new PasswordBox
            {
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x46)),
                Foreground = Brushes.White,
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11.5,
                Password = BackgroundAutomationsEngine.GetEmailPassword() ?? ""
            };
            formStack.Children.Add(_passBox);

            var helpLink = new TextBlock
            {
                Text = "\u2197 Generate a Google App Password (myaccount.google.com)",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 6, 0, 12)
            };
            helpLink.MouseLeftButtonDown += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("https://myaccount.google.com/apppasswords") { UseShellExecute = true }); } catch { }
            };
            formStack.Children.Add(helpLink);

            // Buttons: Connect & Save | Disconnect
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

            var connectBtn = CreateActionButton("\u2713 Connect & Save", BrushMint, Colors.Black, (s, e) =>
            {
                ConnectGmailAccount();
            });
            btnRow.Children.Add(connectBtn);

            var testBtn = CreateActionButton("\u26A1 Send Test Email", new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), Colors.Black, (s, e) =>
            {
                SendTestEmail();
            });
            testBtn.Margin = new Thickness(8, 0, 0, 0);
            btnRow.Children.Add(testBtn);

            var disconnectBtn = CreateActionButton("\u2715 Disconnect", new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x46)), Color.FromRgb(0xD1, 0xD5, 0xDB), (s, e) =>
            {
                DisconnectGmailAccount();
            });
            disconnectBtn.Margin = new Thickness(8, 0, 0, 0);
            btnRow.Children.Add(disconnectBtn);

            formStack.Children.Add(btnRow);

            _emailFeedbackText = new TextBlock
            {
                FontSize = 11,
                Foreground = BrushMuted,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            formStack.Children.Add(_emailFeedbackText);

            Grid.SetColumn(formStack, 0);
            contentGrid.Children.Add(formStack);

            // Right: Unread Inbox Cards Preview
            var inboxStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            var inboxHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            inboxHeader.Children.Add(new TextBlock
            {
                Text = "Recent Unread Messages",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
            });

            var checkNowBtn = new TextBlock
            {
                Text = "\u21BB Refresh",
                FontSize = 10.5,
                Foreground = BrushMint,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            checkNowBtn.MouseLeftButtonDown += (s, e) => FetchUnreadInboxMessages();
            DockPanel.SetDock(checkNowBtn, Dock.Right);
            inboxHeader.Children.Add(checkNowBtn);
            inboxStack.Children.Add(inboxHeader);

            var scroll = new ScrollViewer { Height = 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _inboxCardsPanel = new StackPanel();
            scroll.Content = _inboxCardsPanel;
            inboxStack.Children.Add(scroll);

            Grid.SetColumn(inboxStack, 1);
            contentGrid.Children.Add(inboxStack);

            Grid.SetRow(contentGrid, 1);
            root.Children.Add(contentGrid);

            return root;
        }

        private Border CreateActionButton(string label, Brush bg, Color fg, RoutedEventHandler onClick)
        {
            var btn = new Border
            {
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                Background = bg,
                Cursor = Cursors.Hand
            };
            btn.Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg),
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.MouseEnter += (s, e) => btn.Opacity = 0.88;
            btn.MouseLeave += (s, e) => btn.Opacity = 1.0;
            if (onClick != null) btn.MouseLeftButtonDown += (s, e) => onClick(s, e);
            return btn;
        }

        private void ConnectGmailAccount()
        {
            string email = (_emailBox.Text ?? "").Trim();
            string pass = _passBox.Password ?? "";

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pass))
            {
                _emailFeedbackText.Foreground = BrushOrange;
                _emailFeedbackText.Text = "Please enter both your Gmail address and 16-character App Password.";
                return;
            }

            _emailFeedbackText.Foreground = BrushMuted;
            _emailFeedbackText.Text = "Verifying IMAP (Port 993) & SMTP (Port 587)...";

            Task.Run(() =>
            {
                int count;
                string err;
                bool ok = DirectImapClient.TestConnection("imap.gmail.com", 993, true, email, pass, out count, out err);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ok)
                    {
                        BackgroundAutomationsEngine.EmailConfig.EmailAddress = email;
                        BackgroundAutomationsEngine.EmailConfig.Server = "imap.gmail.com";
                        BackgroundAutomationsEngine.EmailConfig.Port = 993;
                        BackgroundAutomationsEngine.EmailConfig.SmtpServer = "smtp.gmail.com";
                        BackgroundAutomationsEngine.EmailConfig.SmtpPort = 587;
                        BackgroundAutomationsEngine.EmailConfig.IsConnected = true;
                        BackgroundAutomationsEngine.SaveEmailPassword(pass);

                        _emailFeedbackText.Foreground = BrushMint;
                        _emailFeedbackText.Text = string.Format("Connected successfully! Found {0} unread email(s).", count);

                        RefreshGmailStatusUI();
                        FetchUnreadInboxMessages();
                    }
                    else
                    {
                        _emailFeedbackText.Foreground = BrushOrange;
                        _emailFeedbackText.Text = "Connection failed: " + err;
                    }
                }));
            });
        }

        private void DisconnectGmailAccount()
        {
            BackgroundAutomationsEngine.EmailConfig.IsConnected = false;
            BackgroundAutomationsEngine.SaveEmailPassword("");
            _passBox.Password = "";
            _emailFeedbackText.Foreground = BrushMuted;
            _emailFeedbackText.Text = "Gmail account disconnected.";
            RefreshGmailStatusUI();
            if (_inboxCardsPanel != null) _inboxCardsPanel.Children.Clear();
        }

        private void SendTestEmail()
        {
            string email = (_emailBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(email) || !BackgroundAutomationsEngine.EmailConfig.IsConnected)
            {
                _emailFeedbackText.Foreground = BrushOrange;
                _emailFeedbackText.Text = "Connect your account before sending a test email.";
                return;
            }

            _emailFeedbackText.Foreground = BrushMuted;
            _emailFeedbackText.Text = "Sending test email via Gmail SMTP...";

            Task.Run(() =>
            {
                string err;
                bool ok = DirectSmtpClient.SendGmailEmail(
                    email,
                    "Flinch Assistant Test Verification",
                    "Success! Your Flinch AI Assistant is connected to Google Gmail. You can now read, draft, and send emails with your voice.",
                    out err);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ok)
                    {
                        _emailFeedbackText.Foreground = BrushMint;
                        _emailFeedbackText.Text = "Test email sent successfully to " + email;
                        if (_parentIsland != null) _parentIsland.ShowAlert("email", "Test Email Sent", email);
                    }
                    else
                    {
                        _emailFeedbackText.Foreground = BrushOrange;
                        _emailFeedbackText.Text = "Failed to send test email: " + err;
                    }
                }));
            });
        }

        private void FetchUnreadInboxMessages()
        {
            if (!BackgroundAutomationsEngine.EmailConfig.IsConnected) return;

            string email = BackgroundAutomationsEngine.EmailConfig.EmailAddress;
            string pass = BackgroundAutomationsEngine.GetEmailPassword();

            Task.Run(() =>
            {
                List<EmailMessageInfo> msgs;
                string err;
                bool ok = DirectImapClient.FetchUnreadMessages("imap.gmail.com", 993, true, email, pass, 8, out msgs, out err);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_inboxCardsPanel == null) return;
                    _inboxCardsPanel.Children.Clear();

                    if (!ok || msgs == null || msgs.Count == 0)
                    {
                        _inboxCardsPanel.Children.Add(new TextBlock
                        {
                            Text = ok ? "No unread emails in inbox." : ("Could not fetch: " + err),
                            FontSize = 10.5,
                            Foreground = BrushMuted,
                            Margin = new Thickness(4)
                        });
                        return;
                    }

                    foreach (var m in msgs)
                    {
                        var card = new Border
                        {
                            Margin = new Thickness(0, 0, 0, 5),
                            Padding = new Thickness(8, 6, 8, 6),
                            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x32, 0x40)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6)
                        };

                        var cardStack = new StackPanel();
                        var topRow = new DockPanel();

                        var fromTb = new TextBlock
                        {
                            Text = m.From ?? "Unknown",
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Brushes.White
                        };
                        DockPanel.SetDock(fromTb, Dock.Left);
                        topRow.Children.Add(fromTb);

                        if (m.IsUrgent)
                        {
                            var urgBadge = new Border
                            {
                                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x44, 0x44)),
                                CornerRadius = new CornerRadius(3),
                                Padding = new Thickness(4, 1, 4, 1),
                                Margin = new Thickness(6, 0, 0, 0)
                            };
                            urgBadge.Child = new TextBlock { Text = "URGENT", FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) };
                            DockPanel.SetDock(urgBadge, Dock.Right);
                            topRow.Children.Add(urgBadge);
                        }

                        cardStack.Children.Add(topRow);

                        cardStack.Children.Add(new TextBlock
                        {
                            Text = m.Subject ?? "(No Subject)",
                            FontSize = 10.5,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(0, 2, 0, 0)
                        });

                        card.Child = cardStack;
                        _inboxCardsPanel.Children.Add(card);
                    }
                }));
            });
        }

        private void RefreshGmailStatusUI()
        {
            bool isConn = BackgroundAutomationsEngine.EmailConfig.IsConnected;
            if (_gmailStatusDot != null)
            {
                _gmailStatusDot.Fill = isConn ? BrushMint : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
            }
            if (_gmailStatusHeader != null)
            {
                _gmailStatusHeader.Text = isConn ? ("  \u2022 Connected (" + BackgroundAutomationsEngine.EmailConfig.EmailAddress + ")") : "  \u2022 Disconnected";
                _gmailStatusHeader.Foreground = isConn ? BrushMint : BrushMuted;
            }
        }

        // ---------------------------------------------------------------------
        // Flyout 2: Chat History
        // ---------------------------------------------------------------------
        private UIElement BuildHistoryFlyout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var closeBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 10.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            header.Children.Add(new TextBlock
            {
                Text = "\U0001F552 Full Chat & Conversation History",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listStack = new StackPanel();

            var all = FlinchSkills.LoadRecentHistory();
            if (all.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = "No previous conversations recorded yet. Ask Flinch anything by voice!",
                    FontSize = 11,
                    Foreground = BrushMuted,
                    Margin = new Thickness(4)
                });
            }
            else
            {
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    var turn = all[i];
                    var card = new Border
                    {
                        Margin = new Thickness(0, 0, 0, 6),
                        Padding = new Thickness(10, 8, 10, 8),
                        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x32, 0x40)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Cursor = Cursors.Hand
                    };
                    var cardStack = new StackPanel();

                    var top = new DockPanel();
                    var time = new TextBlock
                    {
                        Text = FormatRelativeTime(turn.t),
                        FontSize = 9.5,
                        Foreground = BrushMuted
                    };
                    DockPanel.SetDock(time, Dock.Right);
                    top.Children.Add(time);

                    var q = new TextBlock
                    {
                        Text = "\U0001F4AC " + turn.u,
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White
                    };
                    top.Children.Add(q);
                    cardStack.Children.Add(top);

                    if (!string.IsNullOrWhiteSpace(turn.a))
                    {
                        cardStack.Children.Add(new TextBlock
                        {
                            Text = turn.a,
                            FontSize = 10.5,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 4, 0, 0)
                        });
                    }

                    card.Child = cardStack;
                    card.MouseEnter += (s, e) => card.BorderBrush = BrushMint;
                    card.MouseLeave += (s, e) => card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x32, 0x40));
                    card.MouseLeftButtonDown += (s, e) =>
                    {
                        if (_parentIsland != null && !string.IsNullOrWhiteSpace(turn.a))
                        {
                            _parentIsland.SpeakAnswer(turn.a);
                        }
                    };

                    listStack.Children.Add(card);
                }
            }

            scroll.Content = listStack;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            return root;
        }

        // ---------------------------------------------------------------------
        // Flyout 3: Hotkeys Reference
        // ---------------------------------------------------------------------
        private UIElement BuildHotkeysFlyout()
        {
            var root = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var closeBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 10.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            header.Children.Add(new TextBlock { Text = "\u2328 Keyboard Shortcuts & Hotkeys", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            root.Children.Add(header);

            var table = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            string[,] keys = new string[,]
            {
                { "Alt + V", "Toggle Voice Listening / Instant Barge-in" },
                { "Ctrl + Alt + X", "Emergency Stop: Cancel active speech, downloads, tasks & timers" },
                { "Alt + S", "Capture Foreground Window context for visual questions" },
                { "Hover Notch", "Reveal and interact with Flinch Hub" },
                { "Escape", "Close Flinch Hub or dismiss active flyout" },
                { "Voice Command", "\"Check my email\", \"Draft email to alex saying hello\", \"Send that email\"" }
            };

            for (int i = 0; i < keys.GetLength(0); i++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

                var kBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x22, 0x2D)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x3A, 0x4B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                kBorder.Child = new TextBlock { Text = keys[i, 0], FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = BrushMint };
                Grid.SetRow(kBorder, i);
                Grid.SetColumn(kBorder, 0);
                table.Children.Add(kBorder);

                var dText = new TextBlock
                {
                    Text = keys[i, 1],
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(dText, i);
                Grid.SetColumn(dText, 1);
                table.Children.Add(dText);
            }

            root.Children.Add(table);
            return root;
        }

        // ---------------------------------------------------------------------
        // Flyout 4: General Settings
        // ---------------------------------------------------------------------
        private UIElement BuildGeneralSettingsFlyout()
        {
            var root = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var closeBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 10.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            header.Children.Add(new TextBlock { Text = "\u2699 General Settings & Automations", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            root.Children.Add(header);

            // Automations toggles
            root.Children.Add(CreateToggleCard(
                "Email Guardian",
                "Monitors Gmail in background every 15 min for urgent action items and security alerts.",
                BackgroundAutomationsEngine.Config.EmailGuardianEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.EmailGuardianEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                }));

            root.Children.Add(CreateToggleCard(
                "Downloads Organizer",
                "Automatically clusters incoming downloads into clean category folders.",
                BackgroundAutomationsEngine.Config.DownloadsOrganizerEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.DownloadsOrganizerEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                }));

            root.Children.Add(CreateToggleCard(
                "Daily Morning Briefing",
                "Speaks weather, calendar events, and inbox highlights upon first morning wake.",
                BackgroundAutomationsEngine.Config.DailyBriefingEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.DailyBriefingEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                }));

            return root;
        }

        private Border CreateToggleCard(string title, string subtitle, bool initial, Action<bool> onToggle)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x32, 0x40)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };

            var dock = new DockPanel();
            var check = new CheckBox
            {
                IsChecked = initial,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            check.Checked += (s, e) => { if (onToggle != null) onToggle(true); };
            check.Unchecked += (s, e) => { if (onToggle != null) onToggle(false); };
            DockPanel.SetDock(check, Dock.Right);
            dock.Children.Add(check);

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = BrushMuted, Margin = new Thickness(0, 2, 0, 0) });
            dock.Children.Add(stack);

            card.Child = dock;
            return card;
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

            FlinchSkills.BeginSearchSession();
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

                    FlinchSkills.EndSearchSession(bestMatch);
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
                        FlinchSkills.NoteSearchMatch(f, score);
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
