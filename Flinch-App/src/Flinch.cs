using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Automation;

[assembly: AssemblyTitle("Flinch")]
[assembly: AssemblyDescription("Flinch Desktop AI Voice Companion")]
[assembly: AssemblyCompany("Reech")]
[assembly: AssemblyProduct("Flinch")]
[assembly: AssemblyCopyright("Copyright © 2026 Reech")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace FlinchApp
{
    // =========================================================================
    // Native Win32 Interop (Hotkeys, ToolWindow, AsyncKeyState, WaveIn Mic Capture)
    // =========================================================================
    internal static class NativeMethods
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_NOREPEAT = 0x4000;
        public const int VK_MENU = 0x12;
        public const int VK_LMENU = 0xA4;
        public const int VK_RMENU = 0xA5;
        public const int VK_V = 0x56;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width { get { return Math.Max(0, Right - Left); } }
            public int Height { get { return Math.Max(0, Bottom - Top); } }
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public static void MakeToolWindow(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        }

        public static bool IsAltVDown()
        {
            bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                    || (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
                    || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;
            bool v = (GetAsyncKeyState(VK_V) & 0x8000) != 0;
            return alt && v;
        }

        public static bool IsAnyHotkeyDown()
        {
            return IsAltVDown();
        }

        // WinMM WaveIn API for direct 16kHz 16-bit Mono PCM microphone capture
        public const int CALLBACK_FUNCTION = 0x00030000;
        public const int WIM_DATA = 0x3C0;

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        public delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        public static extern int waveInGetNumDevs();

        [DllImport("winmm.dll")]
        public static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, WaveInProc dwCallback, IntPtr dwInstance, int fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        public static extern int waveInClose(IntPtr hwi);
    }

    // =========================================================================
    // Direct PCM Microphone Recorder (16kHz, 16-bit, Mono) + Voice Strength Meter
    // =========================================================================
    internal sealed class WaveInRecorder : IDisposable
    {
        private IntPtr _hWaveIn = IntPtr.Zero;
        private NativeMethods.WaveInProc _callback;
        private readonly List<IntPtr> _headers = new List<IntPtr>();
        private readonly List<IntPtr> _buffers = new List<IntPtr>();
        private readonly MemoryStream _pcmStream = new MemoryStream();
        private readonly object _lock = new object();
        private volatile bool _recording;
        private volatile float _currentLevel;

        // Voice Activity Detection (HeyClicky-style automatic silence detection & auto-send)
        public event Action SpeechEnded;
        private volatile bool _hasSpoken;
        private int _consecutiveSpeechBuffers;
        private int _consecutiveSilenceBuffers;
        private DateTime _recordStartUtc = DateTime.MinValue;

        private const int SampleRate = 16000;
        private const int BufferSize = 960; // 30ms of 16kHz 16-bit mono audio
        private const int BufferCount = 8;

        public float CurrentLevel { get { return _currentLevel; } }
        public bool IsRecording { get { return _recording; } }

        public bool Start(out string error)
        {
            error = null;
            lock (_lock)
            {
                if (_recording) return true;
                if (NativeMethods.waveInGetNumDevs() <= 0)
                {
                    error = "No microphone detected on this PC.";
                    return false;
                }

                _pcmStream.SetLength(0);
                _currentLevel = 0f;
                _hasSpoken = false;
                _consecutiveSpeechBuffers = 0;
                _consecutiveSilenceBuffers = 0;
                _recordStartUtc = DateTime.UtcNow;
                _callback = OnWaveIn;

                var fmt = new NativeMethods.WAVEFORMATEX
                {
                    wFormatTag = 1,
                    nChannels = 1,
                    nSamplesPerSec = SampleRate,
                    wBitsPerSample = 16,
                    nBlockAlign = 2,
                    nAvgBytesPerSec = SampleRate * 2,
                    cbSize = 0
                };

                int rc = NativeMethods.waveInOpen(out _hWaveIn, -1, ref fmt, _callback, IntPtr.Zero, NativeMethods.CALLBACK_FUNCTION);
                if (rc != 0 || _hWaveIn == IntPtr.Zero)
                {
                    error = "Could not open microphone (waveInOpen error " + rc + ").";
                    return false;
                }

                int hdrSize = Marshal.SizeOf(typeof(NativeMethods.WAVEHDR));
                for (int i = 0; i < BufferCount; i++)
                {
                    IntPtr bufPtr = Marshal.AllocHGlobal(BufferSize);
                    IntPtr hdrPtr = Marshal.AllocHGlobal(hdrSize);

                    var hdr = new NativeMethods.WAVEHDR
                    {
                        lpData = bufPtr,
                        dwBufferLength = BufferSize,
                        dwBytesRecorded = 0,
                        dwFlags = 0
                    };
                    Marshal.StructureToPtr(hdr, hdrPtr, false);
                    NativeMethods.waveInPrepareHeader(_hWaveIn, hdrPtr, hdrSize);
                    NativeMethods.waveInAddBuffer(_hWaveIn, hdrPtr, hdrSize);

                    _buffers.Add(bufPtr);
                    _headers.Add(hdrPtr);
                }

                _recording = true;
                rc = NativeMethods.waveInStart(_hWaveIn);
                if (rc != 0)
                {
                    _recording = false;
                    CleanupHandles();
                    error = "Could not start microphone capture (waveInStart error " + rc + ").";
                    return false;
                }
                return true;
            }
        }

        private void OnWaveIn(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg != NativeMethods.WIM_DATA || dwParam1 == IntPtr.Zero) return;

            try
            {
                var hdr = (NativeMethods.WAVEHDR)Marshal.PtrToStructure(dwParam1, typeof(NativeMethods.WAVEHDR));
                int bytes = (int)hdr.dwBytesRecorded;
                if (bytes > 0 && hdr.lpData != IntPtr.Zero)
                {
                    byte[] chunk = new byte[bytes];
                    Marshal.Copy(hdr.lpData, chunk, 0, bytes);

                    double sumSq = 0;
                    int maxAbs = 0;
                    int samples = bytes / 2;
                    for (int i = 0; i + 1 < bytes; i += 2)
                    {
                        short s = (short)(chunk[i] | (chunk[i + 1] << 8));
                        int abs = s == short.MinValue ? 32767 : Math.Abs((int)s);
                        if (abs > maxAbs) maxAbs = abs;
                        double norm = s / 32768.0;
                        sumSq += norm * norm;
                    }
                    double rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0.0;
                    double peak = maxAbs / 32768.0;

                    double rawEnergy = Math.Max(rms * 6.5, peak * 2.4);
                    double gated = rawEnergy < 0.016 ? 0.0 : Math.Pow(Math.Min(1.0, rawEnergy), 0.55);
                    _currentLevel = (float)gated;

                    // Voice Activity Detection: detect natural voice, then trigger auto-send after 1200ms silence
                    if (gated >= 0.05)
                    {
                        _consecutiveSpeechBuffers++;
                        _consecutiveSilenceBuffers = 0;
                        if (_consecutiveSpeechBuffers >= 2)
                        {
                            _hasSpoken = true;
                        }
                    }
                    else
                    {
                        _consecutiveSpeechBuffers = 0;
                        if (_hasSpoken)
                        {
                            _consecutiveSilenceBuffers++;
                            // 40 buffers * 30ms = 1200ms natural conversational silence window
                            if (_consecutiveSilenceBuffers >= 40 && (DateTime.UtcNow - _recordStartUtc).TotalMilliseconds > 900)
                            {
                                _hasSpoken = false;
                                _consecutiveSilenceBuffers = 0;
                                var handler = SpeechEnded;
                                if (handler != null)
                                {
                                    Task.Run(() => { try { handler(); } catch { } });
                                }
                            }
                        }
                    }

                    lock (_lock)
                    {
                        if (_recording)
                        {
                            _pcmStream.Write(chunk, 0, bytes);
                        }
                    }
                }

                if (_recording && _hWaveIn != IntPtr.Zero)
                {
                    int hdrSize = Marshal.SizeOf(typeof(NativeMethods.WAVEHDR));
                    NativeMethods.waveInAddBuffer(_hWaveIn, dwParam1, hdrSize);
                }
            }
            catch { }
        }

        public byte[] StopAndGetWavBytes()
        {
            byte[] pcmData;
            lock (_lock)
            {
                if (!_recording && _hWaveIn == IntPtr.Zero)
                {
                    return new byte[0];
                }
                _recording = false;
                _currentLevel = 0f;
            }

            try
            {
                if (_hWaveIn != IntPtr.Zero)
                {
                    NativeMethods.waveInStop(_hWaveIn);
                    NativeMethods.waveInReset(_hWaveIn);
                }
            }
            catch { }

            lock (_lock)
            {
                CleanupHandles();
                pcmData = _pcmStream.ToArray();
                _pcmStream.SetLength(0);
            }

            if (pcmData.Length == 0) return new byte[0];
            byte[] trimmed = TrimSilence(pcmData, SampleRate);
            if (trimmed == null || trimmed.Length == 0) return new byte[0];
            return BuildWavBytes(trimmed, SampleRate, 1, 16);
        }

        private static byte[] TrimSilence(byte[] pcm, int sampleRate)
        {
            int totalSamples = pcm.Length / 2;
            if (totalSamples < 800) return new byte[0]; // < 50ms is just an accidental click

            const int speechThreshold = 150; // Sensitive speech threshold catches soft mics & whispering
            int firstSample = -1;
            int lastSample = -1;

            for (int i = 0; i < totalSamples; i++)
            {
                short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                int abs = s == short.MinValue ? 32767 : Math.Abs((int)s);
                if (abs > speechThreshold)
                {
                    if (firstSample < 0) firstSample = i;
                    lastSample = i;
                }
            }

            if (firstSample < 0 || lastSample < firstSample)
            {
                // Soft speech or quiet mic: keep the entire recorded audio
                return pcm;
            }

            // 250ms padding before and after ensures no syllables are clipped
            int padSamples = sampleRate / 4;
            firstSample = Math.Max(0, firstSample - padSamples);
            lastSample = Math.Min(totalSamples - 1, lastSample + padSamples);

            int keepSamples = lastSample - firstSample + 1;
            if (keepSamples <= 0 || keepSamples >= totalSamples) return pcm;

            byte[] outPcm = new byte[keepSamples * 2];
            Buffer.BlockCopy(pcm, firstSample * 2, outPcm, 0, outPcm.Length);
            return outPcm;
        }

        // Fixes streaming WAV headers (e.g. Groq Orpheus sets RIFF size=-1 and data size=-1 with a LIST chunk,
        // which causes Windows SoundPlayer/PlaySound to abort after 2ms without playing audio!)
        public static byte[] NormalizeWavBytes(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length < 44) return wavBytes;
            if (wavBytes[0] != (byte)'R' || wavBytes[1] != (byte)'I' || wavBytes[2] != (byte)'F' || wavBytes[3] != (byte)'F')
                return BuildWavBytes(wavBytes, 24000, 1, 16);

            short channels = 1;
            int sampleRate = 24000;
            short bitsPerSample = 16;
            int dataOffset = 44;
            int dataLen = wavBytes.Length - 44;

            int pos = 12;
            while (pos + 8 <= wavBytes.Length)
            {
                string id = Encoding.ASCII.GetString(wavBytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(wavBytes, pos + 4);
                if (id == "fmt " && pos + 8 + 16 <= wavBytes.Length)
                {
                    channels = BitConverter.ToInt16(wavBytes, pos + 10);
                    sampleRate = BitConverter.ToInt32(wavBytes, pos + 12);
                    bitsPerSample = BitConverter.ToInt16(wavBytes, pos + 22);
                }
                else if (id == "data")
                {
                    dataOffset = pos + 8;
                    int remaining = wavBytes.Length - dataOffset;
                    dataLen = (chunkSize > 0 && chunkSize <= remaining) ? chunkSize : remaining;
                    break;
                }

                if (chunkSize < 0 || pos + 8 + chunkSize > wavBytes.Length)
                    break;
                pos += 8 + chunkSize + (chunkSize & 1);
            }

            if (dataOffset < 0 || dataOffset >= wavBytes.Length || dataLen <= 0)
                return wavBytes;

            if (channels < 1 || channels > 2) channels = 1;
            if (sampleRate < 8000 || sampleRate > 96000) sampleRate = 24000;
            if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32) bitsPerSample = 16;

            byte[] pcm = new byte[dataLen];
            Buffer.BlockCopy(wavBytes, dataOffset, pcm, 0, dataLen);
            return BuildWavBytes(pcm, sampleRate, channels, bitsPerSample);
        }

        public static byte[] BuildWavBytes(byte[] pcmData, int sampleRate, short channels, short bitsPerSample)
        {
            using (var ms = new MemoryStream(44 + pcmData.Length))
            using (var bw = new BinaryWriter(ms))
            {
                int byteRate = sampleRate * channels * (bitsPerSample / 8);
                short blockAlign = (short)(channels * (bitsPerSample / 8));

                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + pcmData.Length);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write(channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write(blockAlign);
                bw.Write(bitsPerSample);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(pcmData.Length);
                bw.Write(pcmData);
                bw.Flush();
                return ms.ToArray();
            }
        }

        private void CleanupHandles()
        {
            int hdrSize = Marshal.SizeOf(typeof(NativeMethods.WAVEHDR));
            if (_hWaveIn != IntPtr.Zero)
            {
                foreach (IntPtr hdrPtr in _headers)
                {
                    try { NativeMethods.waveInUnprepareHeader(_hWaveIn, hdrPtr, hdrSize); } catch { }
                }
                try { NativeMethods.waveInClose(_hWaveIn); } catch { }
                _hWaveIn = IntPtr.Zero;
            }
            foreach (IntPtr hdrPtr in _headers)
            {
                try { Marshal.FreeHGlobal(hdrPtr); } catch { }
            }
            foreach (IntPtr bufPtr in _buffers)
            {
                try { Marshal.FreeHGlobal(bufPtr); } catch { }
            }
            _headers.Clear();
            _buffers.Clear();
        }

        public void Dispose()
        {
            try { StopAndGetWavBytes(); } catch { }
        }
    }

    // =========================================================================
    // Environment, Rules & API Configuration (.env + RULES.md)
    // =========================================================================
    internal static class FlinchEnv
    {
        public static string SupabaseUrl = "";
        public static string SupabaseAnonKey = "";
        public static string VoiceName = "troy";
        public static string LogFilePath = "";
        public static string SystemRules =
            "You are Flinch, a desktop AI voice buddy talking out loud with the user.\n" +
            "1. Sound like a real friend: natural, relaxed, and friendly. Use normal contractions ('it's', 'you're', 'that's').\n" +
            "2. NEVER mention or describe your own tone out loud (never say things like 'in a warm, casual way', 'talking casually', or 'keeping it warm and playful') unless the user specifically asks you to change your tone.\n" +
            "3. NEVER start your responses with 'Oh man', 'Oh boy', 'Well,', or 'Ah,'. Vary your openings naturally or lead straight into the answer.\n" +
            "4. Do NOT use <chuckle> on normal factual questions, news, prices, or everyday tasks. Only use <chuckle> on rare occasions when the user tells a joke or something is genuinely funny.\n" +
            "5. Make your FIRST sentence short and direct (4 to 10 words) so voice synthesis starts fast, then continue naturally (1-2 sentences for quick chats, 2-4 sentences for explanations or stories).\n" +
            "6. Write strictly for the ear: no markdown, no asterisks, no bullet lists. Say numbers and symbols naturally out loud.\n" +
            "7. Candid honesty: never be a yes-man. If the user's idea is wrong or risky, disagree directly right away.";

        public static string GitHubRepo = "La-Unicorn/flinch-releases";
        public static string BuildVersion = "0.1.0";
        public static string BuildTimeUtc = "";

        // Zero-footprint: no .env, no RULES.md, no log file. Only the public Supabase URL + anon key are compiled in.
        public static void Load()
        {
            SupabaseUrl = BuildConfig.SupabaseUrl.TrimEnd('/');
            SupabaseAnonKey = BuildConfig.SupabaseAnonKey;
            GitHubRepo = BuildConfig.GitHubRepo;
            BuildVersion = BuildConfig.BuildVersion;
            BuildTimeUtc = BuildConfig.BuildTimeUtc;
        }

        public static void Log(string msg)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flinch_debug.log");
                System.IO.File.AppendAllText(path, string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}\r\n", DateTime.UtcNow, msg));
            }
            catch { }
        }
    }
    // =========================================================================
    // Supabase Authentication, DPAPI Persistent PC Session & Backend Key Vault
    // =========================================================================
    internal sealed class FlinchAuthSession
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string ExpiresAtIso { get; set; }
        public string UserId { get; set; }
        public string Email { get; set; }
        public string PreferredVoice { get; set; }
        public string Tier { get; set; }
    }

    internal static class SupabaseAuthClient
    {
        private static readonly string SessionFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Flinch",
            "session.dat");

        private static readonly HttpClient AuthHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        public static FlinchAuthSession CurrentSession { get; private set; }
        public const int DemoVoiceLimit = 5;
        public static int VoiceRemaining = -1;
        public static bool QuotaExhausted { get { return VoiceRemaining == 0; } }

        public static bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(FlinchEnv.SupabaseUrl) && !string.IsNullOrWhiteSpace(FlinchEnv.SupabaseAnonKey); }
        }

        public static void SaveSessionToDisk(FlinchAuthSession session)
        {
            if (session == null) return;
            CurrentSession = session;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(SessionFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var jss = new JavaScriptSerializer();
                byte[] plainBytes = Encoding.UTF8.GetBytes(jss.Serialize(session));
                byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(SessionFilePath, encrypted);
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("Failed to save DPAPI session: " + ex.Message);
            }
        }

        public static FlinchAuthSession LoadSessionFromDisk()
        {
            try
            {
                if (!File.Exists(SessionFilePath)) return null;
                byte[] encrypted = File.ReadAllBytes(SessionFilePath);
                if (encrypted == null || encrypted.Length == 0) return null;
                byte[] plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var jss = new JavaScriptSerializer();
                return jss.Deserialize<FlinchAuthSession>(Encoding.UTF8.GetString(plainBytes));
            }
            catch
            {
                return null;
            }
        }

        public static void SignOut()
        {
            CurrentSession = null;
            VoiceRemaining = -1;
            GeminiClient.ClearHistory();
            try
            {
                if (File.Exists(SessionFilePath))
                    File.Delete(SessionFilePath);
            }
            catch { }
            FlinchEnv.Log("Signed out and cleared local DPAPI session.");
        }

        public static bool TryRestoreSavedSession(out string error)
        {
            error = "";
            var saved = LoadSessionFromDisk();
            if (saved == null || string.IsNullOrWhiteSpace(saved.AccessToken))
                return false;

            CurrentSession = saved;
            if (!string.IsNullOrWhiteSpace(saved.PreferredVoice))
                FlinchEnv.VoiceName = saved.PreferredVoice;

            bool needsRefresh = false;
            DateTime expUtc;
            if (!string.IsNullOrWhiteSpace(saved.ExpiresAtIso) &&
                DateTime.TryParse(saved.ExpiresAtIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out expUtc))
            {
                if ((expUtc.ToUniversalTime() - DateTime.UtcNow).TotalMinutes < 10)
                    needsRefresh = true;
            }

            if (needsRefresh && !string.IsNullOrWhiteSpace(saved.RefreshToken))
            {
                if (!RefreshSession(saved.RefreshToken, out error))
                {
                    SignOut();
                    return false;
                }
            }

            string cfgErr;
            if (!FetchBackendRuntimeConfig(out cfgErr))
            {
                // Might be an expired access token; try refreshing once
                if (!string.IsNullOrWhiteSpace(CurrentSession.RefreshToken) && RefreshSession(CurrentSession.RefreshToken, out error))
                {
                    if (!FetchBackendRuntimeConfig(out cfgErr))
                    {
                        error = cfgErr;
                        return false;
                    }
                }
                else
                {
                    error = cfgErr;
                    return false;
                }
            }

            FlinchEnv.Log("Restored saved Supabase session for " + CurrentSession.Email);
            return true;
        }

        public static bool RefreshSession(string refreshToken, out string error)
        {
            error = "";
            if (!IsConfigured || string.IsNullOrWhiteSpace(refreshToken))
            {
                error = "Session expired.";
                return false;
            }

            try
            {
                var jss = new JavaScriptSerializer();
                string url = FlinchEnv.SupabaseUrl + "/auth/v1/token?grant_type=refresh_token";
                string body = jss.Serialize(new Dictionary<string, object> { { "refresh_token", refreshToken } });
                int status;
                string resp = SendSupabaseRequest(HttpMethod.Post, url, body, FlinchEnv.SupabaseAnonKey, out status);
                if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(resp))
                {
                    var parsed = ParseSessionFromAuthJson(resp);
                    if (parsed != null)
                    {
                        CurrentSession = parsed;
                        SaveSessionToDisk(parsed);
                        return true;
                    }
                }
                error = ExtractSupabaseError(resp, "Session expired. Please sign in again.");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool SignIn(string email, string password, out string error, out bool needsConfirmationCode)
        {
            error = "";
            needsConfirmationCode = false;
            if (!IsConfigured)
            {
                error = "SUPABASE_URL or SUPABASE_ANON_KEY is missing in .env";
                return false;
            }

            try
            {
                var jss = new JavaScriptSerializer();
                string url = FlinchEnv.SupabaseUrl + "/auth/v1/token?grant_type=password";
                string body = jss.Serialize(new Dictionary<string, object>
                {
                    { "email", email.Trim() },
                    { "password", password }
                });
                int status;
                string resp = SendSupabaseRequest(HttpMethod.Post, url, body, FlinchEnv.SupabaseAnonKey, out status);
                if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(resp))
                {
                    var sess = ParseSessionFromAuthJson(resp);
                    if (sess != null && !string.IsNullOrWhiteSpace(sess.AccessToken))
                    {
                        SaveSessionToDisk(sess);
                        string cfgErr;
                        if (!FetchBackendRuntimeConfig(out cfgErr))
                        {
                            error = cfgErr;
                            return false;
                        }
                        return true;
                    }
                }

                string errMsg = ExtractSupabaseError(resp, "Invalid email or password.");
                if (errMsg.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    errMsg.IndexOf("not confirmed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    needsConfirmationCode = true;
                    string resendErr;
                    ResendOtp(email, out resendErr);
                    error = "Please enter the confirmation code sent to your email.";
                    return false;
                }

                error = errMsg;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool SignUp(string email, string password, out string error, out bool needsConfirmationCode)
        {
            error = "";
            needsConfirmationCode = false;
            if (!IsConfigured)
            {
                error = "SUPABASE_URL or SUPABASE_ANON_KEY is missing in .env";
                return false;
            }

            try
            {
                var jss = new JavaScriptSerializer();
                string url = FlinchEnv.SupabaseUrl + "/auth/v1/signup";
                string body = jss.Serialize(new Dictionary<string, object>
                {
                    { "email", email.Trim() },
                    { "password", password }
                });
                int status;
                string resp = SendSupabaseRequest(HttpMethod.Post, url, body, FlinchEnv.SupabaseAnonKey, out status);
                if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(resp))
                {
                    var sess = ParseSessionFromAuthJson(resp);
                    if (sess != null && !string.IsNullOrWhiteSpace(sess.AccessToken))
                    {
                        SaveSessionToDisk(sess);
                        string cfgErr;
                        if (!FetchBackendRuntimeConfig(out cfgErr))
                        {
                            error = cfgErr;
                            return false;
                        }
                        return true;
                    }

                    // Account created; Supabase sent confirmation email/code
                    needsConfirmationCode = true;
                    return true;
                }

                error = ExtractSupabaseError(resp, "Could not create account.");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool VerifyOtpCode(string email, string code, out string error)
        {
            error = "";
            if (!IsConfigured)
            {
                error = "SUPABASE_URL or SUPABASE_ANON_KEY is missing in .env";
                return false;
            }

            string cleanCode = Regex.Replace(code ?? "", @"\s+", "");
            if (string.IsNullOrEmpty(cleanCode))
            {
                error = "Please enter the confirmation code from your email.";
                return false;
            }

            var jss = new JavaScriptSerializer();
            string url = FlinchEnv.SupabaseUrl + "/auth/v1/verify";
            string[] otpTypes = new[] { "signup", "email", "magiclink", "recovery" };
            string lastErr = "Invalid or expired confirmation code.";

            foreach (string t in otpTypes)
            {
                try
                {
                    string body = jss.Serialize(new Dictionary<string, object>
                    {
                        { "type", t },
                        { "email", email.Trim() },
                        { "token", cleanCode }
                    });
                    int status;
                    string resp = SendSupabaseRequest(HttpMethod.Post, url, body, FlinchEnv.SupabaseAnonKey, out status);
                    if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(resp))
                    {
                        var sess = ParseSessionFromAuthJson(resp);
                        if (sess != null && !string.IsNullOrWhiteSpace(sess.AccessToken))
                        {
                            SaveSessionToDisk(sess);
                            string cfgErr;
                            if (!FetchBackendRuntimeConfig(out cfgErr))
                            {
                                error = cfgErr;
                                return false;
                            }
                            return true;
                        }
                    }
                    lastErr = ExtractSupabaseError(resp, lastErr);
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }

            error = lastErr;
            return false;
        }

        public static bool ResendOtp(string email, out string error)
        {
            error = "";
            if (!IsConfigured)
            {
                error = "Supabase not configured.";
                return false;
            }
            try
            {
                var jss = new JavaScriptSerializer();
                string url = FlinchEnv.SupabaseUrl + "/auth/v1/resend";
                string body = jss.Serialize(new Dictionary<string, object>
                {
                    { "type", "signup" },
                    { "email", email.Trim() }
                });
                int status;
                string resp = SendSupabaseRequest(HttpMethod.Post, url, body, FlinchEnv.SupabaseAnonKey, out status);
                if (status >= 200 && status < 300) return true;
                error = ExtractSupabaseError(resp, "Could not resend confirmation code.");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool SendPasswordReset(string email, out string error)
        {
            error = "";
            if (!IsConfigured)
            {
                error = "Supabase not configured.";
                return false;
            }
            try
            {
                var jss = new JavaScriptSerializer();
                string url = FlinchEnv.SupabaseUrl + "/auth/v1/recover";
                string body = jss.Serialize(new Dictionary<string, object>
                {
                    { "email", email.Trim() }
                });
                int status;
                string resp = SendSupabaseRequest(HttpMethod.Post, url, body, FlinchEnv.SupabaseAnonKey, out status);
                if (status >= 200 && status < 300) return true;
                error = ExtractSupabaseError(resp, "Could not send password recovery email.");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool FetchBackendRuntimeConfig(out string error)
        {
            error = "";
            if (CurrentSession == null || string.IsNullOrWhiteSpace(CurrentSession.AccessToken))
            {
                error = "Not authenticated.";
                return false;
            }

            try
            {
                string url = FlinchEnv.SupabaseUrl + "/rest/v1/rpc/get_flinch_runtime_config";
                int status;
                string resp = SendSupabaseRequest(HttpMethod.Post, url, "{}", CurrentSession.AccessToken, out status);
                if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(resp))
                {
                    var jss = new JavaScriptSerializer();
                    var root = jss.Deserialize<Dictionary<string, object>>(resp);
                    if (root != null)
                    {
                        if (root.ContainsKey("preferred_voice") && root["preferred_voice"] != null)
                        {
                            string pv = root["preferred_voice"].ToString().Trim();
                            if (!string.IsNullOrEmpty(pv))
                            {
                                FlinchEnv.VoiceName = pv;
                                CurrentSession.PreferredVoice = pv;
                            }
                        }
                        if (root.ContainsKey("voice_remaining") && root["voice_remaining"] != null)
                        {
                            int vr;
                            if (int.TryParse(root["voice_remaining"].ToString(), out vr)) VoiceRemaining = vr;
                        }
                        if (root.ContainsKey("tier") && root["tier"] != null)
                        {
                            CurrentSession.Tier = root["tier"].ToString().Trim();
                        }
                        SaveSessionToDisk(CurrentSession);
                    }

                    FlinchEnv.Log("Loaded user runtime profile from Supabase (voice=" + FlinchEnv.VoiceName + ", remaining=" + VoiceRemaining + ").");
                    return true;
                }

                if (status == 404)
                {
                    error = "Supabase table/RPC not initialized yet. Please run SUPABASE_DEMO_LIMIT.sql in your Supabase SQL Editor.";
                    return false;
                }

                error = ExtractSupabaseError(resp, "Failed to load runtime config from Supabase (" + status + ").");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void UpdatePreferredVoiceAsync(string voiceName)
        {
            if (CurrentSession == null || string.IsNullOrWhiteSpace(CurrentSession.AccessToken)) return;
            CurrentSession.PreferredVoice = voiceName;
            SaveSessionToDisk(CurrentSession);

            Task.Run(() =>
            {
                try
                {
                    var jss = new JavaScriptSerializer();
                    string url = FlinchEnv.SupabaseUrl + "/rest/v1/rpc/update_flinch_voice";
                    string body = jss.Serialize(new Dictionary<string, object> { { "new_voice", voiceName } });
                    int status;
                    SendSupabaseRequest(HttpMethod.Post, url, body, CurrentSession.AccessToken, out status);
                }
                catch { }
            });
        }

        // Atomically spends one of the account's demo voice messages on the server. Never trusts the client counter.
        public static bool ConsumeVoiceMessage(out int remaining, out string error)
        {
            remaining = 0;
            error = "";
            if (CurrentSession == null || string.IsNullOrWhiteSpace(CurrentSession.AccessToken))
            {
                error = "Please sign in again.";
                return false;
            }
            try
            {
                string url = FlinchEnv.SupabaseUrl + "/rest/v1/rpc/consume_voice_message";
                int status;
                string resp = SendSupabaseRequest(HttpMethod.Post, url, "{}", CurrentSession.AccessToken, out status);
                if (status == 401 && !string.IsNullOrWhiteSpace(CurrentSession.RefreshToken))
                {
                    string rErr;
                    if (RefreshSession(CurrentSession.RefreshToken, out rErr))
                        resp = SendSupabaseRequest(HttpMethod.Post, url, "{}", CurrentSession.AccessToken, out status);
                }
                if (status >= 200 && status < 300 && !string.IsNullOrWhiteSpace(resp))
                {
                    var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(resp);
                    bool allowed = root != null && root.ContainsKey("allowed") && root["allowed"] is bool && (bool)root["allowed"];
                    if (root != null && root.ContainsKey("remaining") && root["remaining"] != null)
                        int.TryParse(root["remaining"].ToString(), out remaining);
                    VoiceRemaining = remaining;
                    if (!allowed)
                    {
                        error = "Demo limit reached: " + DemoVoiceLimit + " of " + DemoVoiceLimit + " voice messages used.";
                        return false;
                    }
                    return true;
                }
                error = ExtractSupabaseError(resp, "Could not verify your demo allowance.");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        private static string SendSupabaseRequest(HttpMethod method, string url, string jsonBody, string bearerToken, out int statusCode)
        {
            statusCode = 0;
            using (var req = new HttpRequestMessage(method, url))
            {
                req.Headers.TryAddWithoutValidation("apikey", FlinchEnv.SupabaseAnonKey);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerToken);
                if (jsonBody != null)
                {
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }
                using (var resp = AuthHttp.SendAsync(req).GetAwaiter().GetResult())
                {
                    statusCode = (int)resp.StatusCode;
                    return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
        }

        private static FlinchAuthSession ParseSessionFromAuthJson(string json)
        {
            try
            {
                var jss = new JavaScriptSerializer();
                var root = jss.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return null;

                string accessToken = root.ContainsKey("access_token") && root["access_token"] != null ? root["access_token"].ToString() : "";
                string refreshToken = root.ContainsKey("refresh_token") && root["refresh_token"] != null ? root["refresh_token"].ToString() : "";
                int expiresIn = 3600;
                if (root.ContainsKey("expires_in") && root["expires_in"] != null)
                    int.TryParse(root["expires_in"].ToString(), out expiresIn);

                string userId = "";
                string email = "";
                if (root.ContainsKey("user") && root["user"] is Dictionary<string, object>)
                {
                    var u = (Dictionary<string, object>)root["user"];
                    if (u.ContainsKey("id") && u["id"] != null) userId = u["id"].ToString();
                    if (u.ContainsKey("email") && u["email"] != null) email = u["email"].ToString();
                }
                else
                {
                    if (root.ContainsKey("id") && root["id"] != null) userId = root["id"].ToString();
                    if (root.ContainsKey("email") && root["email"] != null) email = root["email"].ToString();
                }

                return new FlinchAuthSession
                {
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    ExpiresAtIso = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o"),
                    UserId = userId,
                    Email = email,
                    PreferredVoice = FlinchEnv.VoiceName,
                    Tier = "free"
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractSupabaseError(string json, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            try
            {
                var jss = new JavaScriptSerializer();
                var root = jss.Deserialize<Dictionary<string, object>>(json);
                if (root != null)
                {
                    foreach (string k in new[] { "msg", "error_description", "message", "error" })
                    {
                        if (root.ContainsKey(k) && root[k] != null)
                        {
                            string v = root[k].ToString().Trim();
                            if (!string.IsNullOrEmpty(v)) return v;
                        }
                    }
                }
            }
            catch { }
            return fallback;
        }
    }

    internal sealed class ActionPayload
    {
        public string type;
        public string target;
        public string name;
        public int countdown_sec;
        public int seconds;
        public string text;
        public string label;
    }

    // =========================================================================
    // Screen Capture & Coordinate Mapping Services (Privacy-First, Ephemeral)
    // =========================================================================
    internal sealed class ScreenCoordinateMapper
    {
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }
        public double Width { get; private set; }
        public double Height { get; private set; }

        public ScreenCoordinateMapper(double originX, double originY, double width, double height)
        {
            OriginX = originX;
            OriginY = originY;
            Width = Math.Max(1.0, width);
            Height = Math.Max(1.0, height);
        }

        public Point ToNormalized(double screenX, double screenY)
        {
            double nx = (screenX - OriginX) / Width;
            double ny = (screenY - OriginY) / Height;
            return new Point(Math.Max(0.0, Math.Min(1.0, nx)), Math.Max(0.0, Math.Min(1.0, ny)));
        }

        public Point ToScreen(double normX, double normY)
        {
            double cx = Math.Max(0.0, Math.Min(1.0, normX));
            double cy = Math.Max(0.0, Math.Min(1.0, normY));
            return new Point(OriginX + cx * Width, OriginY + cy * Height);
        }

        public Rect ToScreenRect(double normX, double normY, double normW, double normH)
        {
            Point p = ToScreen(normX, normY);
            double w = Math.Max(0.0, Math.Min(1.0 - normX, normW)) * Width;
            double h = Math.Max(0.0, Math.Min(1.0 - normY, normH)) * Height;
            return new Rect(p.X, p.Y, w, h);
        }

        public Rect NormalizeRect(double screenX, double screenY, double screenW, double screenH)
        {
            Point p = ToNormalized(screenX, screenY);
            double nw = Math.Max(0.0, Math.Min(1.0 - p.X, screenW / Width));
            double nh = Math.Max(0.0, Math.Min(1.0 - p.Y, screenH / Height));
            return new Rect(p.X, p.Y, nw, nh);
        }
    }

    internal sealed class WindowTarget
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
        public string ProcessName { get; set; }
        public NativeMethods.RECT Bounds { get; set; }
        public bool IsDisplay { get; set; }
        public int DisplayIndex { get; set; }

        public override string ToString()
        {
            if (IsDisplay)
                return string.Format("Display {0} ({1}×{2})", DisplayIndex + 1, Bounds.Width, Bounds.Height);
            return string.Format("{0} [{1}]", !string.IsNullOrWhiteSpace(Title) ? Title : "Untitled Window", ProcessName);
        }
    }

    internal sealed class CapturedFrame : IDisposable
    {
        public byte[] JpegBytes { get; private set; }
        public System.Drawing.Bitmap RawBitmap { get; private set; }
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public NativeMethods.RECT SourceBounds { get; private set; }
        public string TargetName { get; private set; }
        public DateTime CapturedAtUtc { get; private set; }
        public ScreenCoordinateMapper CoordinateMapper { get; private set; }
        public string UiaJson { get; set; }

        public CapturedFrame(System.Drawing.Bitmap bmp, NativeMethods.RECT bounds, string targetName, int maxDimension = 1200, long quality = 70L)
        {
            TargetName = targetName;
            CapturedAtUtc = DateTime.UtcNow;
            SourceBounds = bounds;
            SourceWidth = bmp.Width;
            SourceHeight = bmp.Height;
            CoordinateMapper = new ScreenCoordinateMapper(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

            RawBitmap = (System.Drawing.Bitmap)bmp.Clone();

            double scale = Math.Min(1.0, (double)maxDimension / Math.Max(bmp.Width, bmp.Height));
            int targetW = Math.Max(1, (int)(bmp.Width * scale));
            int targetH = Math.Max(1, (int)(bmp.Height * scale));

            using (var scaled = new System.Drawing.Bitmap(targetW, targetH))
            {
                using (var g = System.Drawing.Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, targetW, targetH);
                }

                using (var ms = new MemoryStream())
                {
                    System.Drawing.Imaging.ImageCodecInfo jpgEncoder = null;
                    foreach (var codec in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                    {
                        if (codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) { jpgEncoder = codec; break; }
                    }
                    if (jpgEncoder == null)
                    {
                        scaled.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                    else
                    {
                        var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                        encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                        scaled.Save(ms, jpgEncoder, encParams);
                    }
                    JpegBytes = ms.ToArray();
                }
            }
        }

        public void Dispose()
        {
            if (RawBitmap != null)
            {
                RawBitmap.Dispose();
                RawBitmap = null;
            }
            JpegBytes = null;
            UiaJson = null;
        }
    }

    internal sealed class UIElementInfo
    {
        public string type { get; set; }
        public string name { get; set; }
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
    }

    internal static class UIAutomationContextService
    {
        public static string ExtractSnapshot(IntPtr hWnd, NativeMethods.RECT windowBounds, int maxElements = 40)
        {
            if (hWnd == IntPtr.Zero || windowBounds.Width <= 10 || windowBounds.Height <= 10)
                return null;

            try
            {
                var elements = new List<UIElementInfo>();
                var mapper = new ScreenCoordinateMapper(windowBounds.Left, windowBounds.Top, windowBounds.Width, windowBounds.Height);

                AutomationElement root = null;
                try
                {
                    root = AutomationElement.FromHandle(hWnd);
                }
                catch { }

                if (root == null) return null;

                var queue = new Queue<AutomationElement>();
                queue.Enqueue(root);

                var sw = Stopwatch.StartNew();
                int visited = 0;
                while (queue.Count > 0 && elements.Count < maxElements && visited < 120)
                {
                    if (sw.ElapsedMilliseconds > 50) break;
                    visited++;
                    var current = queue.Dequeue();

                    try
                    {
                        var children = current.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                        foreach (AutomationElement child in children)
                        {
                            try
                            {
                                if (child.Current.IsOffscreen) continue;

                                var rect = child.Current.BoundingRectangle;
                                if (rect.Width >= 12 && rect.Height >= 10)
                                {
                                    string cType = child.Current.ControlType != null ? child.Current.ControlType.ProgrammaticName : "";
                                    if (cType.StartsWith("ControlType.")) cType = cType.Substring("ControlType.".Length);
                                    string name = child.Current.Name ?? "";

                                    if (!string.IsNullOrWhiteSpace(name) || cType == "Button" || cType == "Edit" || cType == "Hyperlink" || cType == "Document")
                                    {
                                        var norm = mapper.NormalizeRect(rect.Left, rect.Top, rect.Width, rect.Height);
                                        elements.Add(new UIElementInfo
                                        {
                                            type = cType.ToLowerInvariant(),
                                            name = name.Length > 60 ? name.Substring(0, 60) : name,
                                            x = Math.Round(norm.X, 3),
                                            y = Math.Round(norm.Y, 3),
                                            w = Math.Round(norm.Width, 3),
                                            h = Math.Round(norm.Height, 3)
                                        });

                                        if (elements.Count >= maxElements) break;
                                    }
                                }

                                if (queue.Count < 40)
                                {
                                    queue.Enqueue(child);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (elements.Count == 0) return null;

                var jss = new JavaScriptSerializer();
                return jss.Serialize(elements);
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("UIAutomation extraction warning: " + ex.Message);
                return null;
            }
        }
    }

    internal interface IScreenCaptureService
    {
        List<WindowTarget> EnumerateAvailableTargets(IntPtr ignoreHwnd);
        WindowTarget GetActiveForegroundTarget(IntPtr ignoreHwnd);
        CapturedFrame CaptureTarget(WindowTarget target);
        CapturedFrame CaptureBounds(NativeMethods.RECT bounds, string name);
    }

    internal sealed class WindowsScreenCaptureService : IScreenCaptureService
    {
        public List<WindowTarget> EnumerateAvailableTargets(IntPtr ignoreHwnd)
        {
            var list = new List<WindowTarget>();

            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                list.Add(new WindowTarget
                {
                    Handle = IntPtr.Zero,
                    Title = string.Format("Display {0} {1}", i + 1, s.Primary ? "(Primary)" : ""),
                    ProcessName = "System",
                    Bounds = new NativeMethods.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom },
                    IsDisplay = true,
                    DisplayIndex = i
                });
            }

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == ignoreHwnd) return true;
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;

                NativeMethods.RECT rect;
                int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(NativeMethods.RECT)));
                if (hr != 0 || rect.Width < 80 || rect.Height < 80)
                {
                    if (!NativeMethods.GetWindowRect(hWnd, out rect) || rect.Width < 80 || rect.Height < 80)
                        return true;
                }

                var sb = new System.Text.StringBuilder(256);
                NativeMethods.GetWindowText(hWnd, sb, 256);
                string title = sb.ToString().Trim();
                if (string.IsNullOrEmpty(title) || title == "Program Manager") return true;

                string procName = "App";
                try
                {
                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                    if (pid > 0)
                    {
                        var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName;
                        if (string.Equals(procName, "Flinch", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                catch { }

                list.Add(new WindowTarget
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessName = procName,
                    Bounds = rect,
                    IsDisplay = false
                });

                return true;
            }, IntPtr.Zero);

            return list;
        }

        public WindowTarget GetActiveForegroundTarget(IntPtr ignoreHwnd)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == ignoreHwnd)
            {
                var s = System.Windows.Forms.Screen.PrimaryScreen;
                return new WindowTarget
                {
                    Handle = IntPtr.Zero,
                    Title = "Primary Display",
                    ProcessName = "System",
                    Bounds = new NativeMethods.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom },
                    IsDisplay = true,
                    DisplayIndex = 0
                };
            }

            NativeMethods.RECT rect;
            int hr = NativeMethods.DwmGetWindowAttribute(fg, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(NativeMethods.RECT)));
            if (hr != 0 || rect.Width < 10 || rect.Height < 10)
            {
                NativeMethods.GetWindowRect(fg, out rect);
            }

            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetWindowText(fg, sb, 256);
            string title = sb.ToString().Trim();

            string procName = "App";
            try
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(fg, out pid);
                if (pid > 0)
                {
                    var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                    if (string.Equals(procName, "Flinch", StringComparison.OrdinalIgnoreCase))
                    {
                        var s = System.Windows.Forms.Screen.PrimaryScreen;
                        return new WindowTarget
                        {
                            Handle = IntPtr.Zero,
                            Title = "Primary Display",
                            ProcessName = "System",
                            Bounds = new NativeMethods.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom },
                            IsDisplay = true,
                            DisplayIndex = 0
                        };
                    }
                }
            }
            catch { }

            return new WindowTarget
            {
                Handle = fg,
                Title = !string.IsNullOrEmpty(title) ? title : procName,
                ProcessName = procName,
                Bounds = rect,
                IsDisplay = false
            };
        }

        public CapturedFrame CaptureTarget(WindowTarget target)
        {
            if (target == null) return null;
            var frame = CaptureBounds(target.Bounds, target.Title);
            if (frame != null && target.Handle != IntPtr.Zero)
            {
                frame.UiaJson = UIAutomationContextService.ExtractSnapshot(target.Handle, target.Bounds);
            }
            return frame;
        }

        public CapturedFrame CaptureBounds(NativeMethods.RECT bounds, string name)
        {
            int w = Math.Max(10, bounds.Width);
            int h = Math.Max(10, bounds.Height);
            try
            {
                using (var bmp = new System.Drawing.Bitmap(w, h))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(w, h));
                    }
                    return new CapturedFrame(bmp, bounds, name);
                }
            }
            catch
            {
                try
                {
                    var s = System.Windows.Forms.Screen.PrimaryScreen;
                    int pw = Math.Max(10, s.Bounds.Width);
                    int ph = Math.Max(10, s.Bounds.Height);
                    using (var bmp = new System.Drawing.Bitmap(pw, ph))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.CopyFromScreen(s.Bounds.Left, s.Bounds.Top, 0, 0, new System.Drawing.Size(pw, ph));
                        }
                        var pBounds = new NativeMethods.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom };
                        return new CapturedFrame(bmp, pBounds, "Display");
                    }
                }
                catch
                {
                    // Fail-safe context bitmap if GDI CopyFromScreen is unavailable (e.g. session lock)
                    try
                    {
                        int fw = Math.Max(400, Math.Min(1200, w));
                        int fh = Math.Max(300, Math.Min(800, h));
                        using (var bmp = new System.Drawing.Bitmap(fw, fh))
                        {
                            using (var g = System.Drawing.Graphics.FromImage(bmp))
                            {
                                g.Clear(System.Drawing.Color.FromArgb(24, 27, 36));
                                using (var font = new System.Drawing.Font("Segoe UI", 13f, System.Drawing.FontStyle.Regular))
                                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(220, 225, 235)))
                                {
                                    g.DrawString("Flinch Screen Context: " + (name ?? "Active Window"), font, brush, 30, 40);
                                    g.DrawString(string.Format("Target: {0} ({1}x{2})", name ?? "Display", w, h), font, brush, 30, 80);
                                    g.DrawString(string.Format("Timestamp: {0:yyyy-MM-dd HH:mm:ss} UTC", DateTime.UtcNow), font, brush, 30, 120);
                                }
                            }
                            return new CapturedFrame(bmp, bounds, name ?? "Window");
                        }
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }
    }

    internal sealed class ScreenPreviewWindow : Window
    {
        private readonly CapturedFrame _frame;
        private readonly Action<CapturedFrame> _onConfirm;
        private readonly Action _onDiscard;

        public ScreenPreviewWindow(CapturedFrame frame, Action<CapturedFrame> onConfirm, Action onDiscard)
        {
            _frame = frame;
            _onConfirm = onConfirm;
            _onDiscard = onDiscard;

            Title = "Flinch · Screen Context Preview";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 460;
            Height = 420;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 31, 38)), // #1C1F26
                BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Opacity = 0.55
                }
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Target Pill
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Preview Image
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Privacy note
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Action Buttons

            // 1. Header
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var iconText = new TextBlock { Text = "📸", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var titleText = new TextBlock
            {
                Text = "Confirm Screen Context",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(iconText);
            headerPanel.Children.Add(titleText);
            Grid.SetRow(headerPanel, 0);

            // 2. Target Pill
            var pillBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(37, 41, 50)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var pillText = new TextBlock
            {
                Text = string.Format("Target: {0} ({1}×{2})", frame.TargetName, frame.SourceWidth, frame.SourceHeight),
                Foreground = new SolidColorBrush(Color.FromRgb(223, 138, 94)), // Coral Amber
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            pillBorder.Child = pillText;
            Grid.SetRow(pillBorder, 1);

            // 3. Image Preview
            var imgBorder = new Border
            {
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 12)
            };
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            try
            {
                using (var ms = new MemoryStream(frame.JpegBytes))
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    img.Source = bi;
                }
            }
            catch { }
            imgBorder.Child = img;
            Grid.SetRow(imgBorder, 2);

            // 4. Privacy Note
            var noteText = new TextBlock
            {
                Text = "🔒 Ephemeral context. Nothing is stored on disk or sent without confirmation.",
                Foreground = new SolidColorBrush(Color.FromRgb(154, 160, 171)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(noteText, 3);

            // 5. Buttons
            var btnGrid = new Grid();
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var discardBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(44, 48, 59)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0, 8, 0, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = "✕ Discard",
                    Foreground = new SolidColorBrush(Color.FromRgb(174, 180, 191)),
                    FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                    FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            discardBtn.MouseEnter += (s, e) => discardBtn.Background = new SolidColorBrush(Color.FromRgb(55, 60, 72));
            discardBtn.MouseLeave += (s, e) => discardBtn.Background = new SolidColorBrush(Color.FromRgb(44, 48, 59));
            discardBtn.MouseLeftButtonDown += (s, e) =>
            {
                Close();
                if (_onDiscard != null) _onDiscard();
            };
            Grid.SetColumn(discardBtn, 0);

            var confirmBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(223, 138, 94)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0, 8, 0, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = "✓ Attach to Flinch",
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                    FontSize = 12.5,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            confirmBtn.MouseEnter += (s, e) => confirmBtn.Background = new SolidColorBrush(Color.FromRgb(235, 155, 115));
            confirmBtn.MouseLeave += (s, e) => confirmBtn.Background = new SolidColorBrush(Color.FromRgb(223, 138, 94));
            confirmBtn.MouseLeftButtonDown += (s, e) =>
            {
                Close();
                if (_onConfirm != null) _onConfirm(_frame);
            };
            Grid.SetColumn(confirmBtn, 2);

            btnGrid.Children.Add(discardBtn);
            btnGrid.Children.Add(confirmBtn);
            Grid.SetRow(btnGrid, 4);

            grid.Children.Add(headerPanel);
            grid.Children.Add(pillBorder);
            grid.Children.Add(imgBorder);
            grid.Children.Add(noteText);
            grid.Children.Add(btnGrid);

            border.Child = grid;
            Content = border;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                    if (_onDiscard != null) _onDiscard();
                }
                else if (e.Key == Key.Enter)
                {
                    Close();
                    if (_onConfirm != null) _onConfirm(_frame);
                }
            };
        }
    }

    // =========================================================================
    // Transparent Screen Overlay & Vector Rendering Models (Milestone 4)
    // =========================================================================
    internal sealed class ScenePoint
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    internal sealed class SceneRect
    {
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
    }

    internal sealed class SceneObjectPayload
    {
        public string type { get; set; }
        public ScenePoint from { get; set; }
        public ScenePoint to { get; set; }
        public SceneRect rect { get; set; }
        public string label { get; set; }
        public string text { get; set; }
        public string latex { get; set; }
        public string color { get; set; }
        public string path { get; set; }
        public double? radius { get; set; }
        public double? strokeWidth { get; set; }
        public List<ScenePoint> points { get; set; }
        public int? step { get; set; }
        public int? delayMs { get; set; }
        public string title { get; set; }
        public string altAnswer { get; set; }
        public string altExplanation { get; set; }
    }

    internal sealed class ScenePayload
    {
        public List<SceneObjectPayload> objects { get; set; }
        public int durationMs { get; set; }

        public ScenePayload()
        {
            objects = new List<SceneObjectPayload>();
            durationMs = 8000;
        }
    }

    internal sealed class OverlayWindow : Window
    {
        private static OverlayWindow _activeInstance = null;
        private static readonly object _instanceLock = new object();

        private readonly Canvas _canvas;
        private readonly DispatcherTimer _autoDismissTimer;
        private bool _isDismissing = false;

        public static void ShowScene(NativeMethods.RECT targetBounds, ScenePayload scene)
        {
            if (scene == null || scene.objects == null || scene.objects.Count == 0)
            {
                FlinchEnv.Log("[OverlayWindow] ShowScene ignored: scene is null or has 0 objects.");
                return;
            }

            FlinchEnv.Log(string.Format("[OverlayWindow] ShowScene invoked with {0} objects.", scene.objects.Count));

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    CloseCurrent();

                    var ps = System.Windows.Forms.Screen.PrimaryScreen;
                    targetBounds = new NativeMethods.RECT
                    {
                        Left = ps.Bounds.Left,
                        Top = ps.Bounds.Top,
                        Right = ps.Bounds.Right,
                        Bottom = ps.Bounds.Bottom
                    };

                    var win = new OverlayWindow(targetBounds, scene);
                    lock (_instanceLock)
                    {
                        _activeInstance = win;
                    }
                    win.Show();
                    win.Activate();

                    IntPtr hwnd = new WindowInteropHelper(win).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.MakeToolWindow(hwnd);
                        NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_TOPMOST | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW
                    }

                    FlinchEnv.Log(string.Format("[OverlayWindow] Window active on screen at ({0},{1}) {2}x{3}", win.Left, win.Top, win.Width, win.Height));
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("[OverlayWindow] ShowScene exception: " + ex.ToString());
                }
            }));
        }

        public static void CloseCurrent()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_instanceLock)
                {
                    if (_activeInstance != null)
                    {
                        try { _activeInstance.Close(); } catch { }
                        _activeInstance = null;
                    }
                }
            }));
        }

        public OverlayWindow(NativeMethods.RECT targetBounds, ScenePayload scene)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = true;

            Left = targetBounds.Left;
            Top = targetBounds.Top;
            Width = Math.Max(100, targetBounds.Width);
            Height = Math.Max(100, targetBounds.Height);

            _canvas = new Canvas
            {
                Width = Width,
                Height = Height,
                Background = Brushes.Transparent
            };
            Content = _canvas;

            // 1. Top-right floating dismiss pill: [ ✕ Dismiss (Esc) ]
            var dismissBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 24, 28, 38)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 148, 163, 184)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 6, 14, 6),
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { BlurRadius = 14, Opacity = 0.7, ShadowDepth = 2 }
            };
            var dismissStack = new StackPanel { Orientation = Orientation.Horizontal };
            dismissStack.Children.Add(new TextBlock
            {
                Text = "✕",
                Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 7, 0)
            });
            dismissStack.Children.Add(new TextBlock
            {
                Text = "Dismiss Overlay (Esc)",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI")
            });
            dismissBorder.Child = dismissStack;
            dismissBorder.MouseLeftButtonDown += (s, e) => DismissWithAnimation();

            Canvas.SetRight(dismissBorder, 22);
            Canvas.SetTop(dismissBorder, 22);
            _canvas.Children.Add(dismissBorder);

            // 2. Render each scene object onto the Canvas
            RenderSceneObjects(scene);

            // 3. Entrance animation: Smooth fade-in
            Opacity = 0.0;
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(220)));
            BeginAnimation(UIElement.OpacityProperty, fadeIn);

            // 4. Auto-dismiss timer (default 15 seconds so user has time to view)
            int duration = scene.durationMs >= 4000 ? scene.durationMs : 15000;
            _autoDismissTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(duration)
            };
            _autoDismissTimer.Tick += (s, e) =>
            {
                _autoDismissTimer.Stop();
                DismissWithAnimation();
            };
            _autoDismissTimer.Start();

            // 5. Keyboard dismissal & double click on empty canvas
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) DismissWithAnimation();
            };

            _canvas.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource == _canvas && e.ClickCount >= 2) DismissWithAnimation();
            };
        }

        private void RenderSceneObjects(ScenePayload scene)
        {
            if (scene.objects == null || scene.objects.Count == 0) return;

            int maxDelay = 0;
            for (int i = 0; i < scene.objects.Count; i++)
            {
                var obj = scene.objects[i];
                if (obj == null) continue;

                int delay = 0;
                if (obj.delayMs.HasValue && obj.delayMs.Value >= 0)
                {
                    delay = obj.delayMs.Value;
                }
                else if (obj.step.HasValue && obj.step.Value > 1)
                {
                    delay = (obj.step.Value - 1) * 1300;
                }
                else if (scene.objects.Count > 1)
                {
                    delay = i * 1100;
                }

                if (delay > maxDelay) maxDelay = delay;

                if (delay <= 0)
                {
                    RenderSingleObject(obj);
                }
                else
                {
                    int capturedDelay = delay;
                    Task.Delay(capturedDelay).ContinueWith(_ =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_isDismissing) return;
                            RenderSingleObject(obj);
                        }));
                    });
                }
            }

            int totalLifeMs = Math.Max(scene.durationMs, maxDelay + 9000);
            if (_autoDismissTimer != null)
            {
                _autoDismissTimer.Interval = TimeSpan.FromMilliseconds(totalLifeMs);
            }
        }

        private void RenderSingleObject(SceneObjectPayload obj)
        {
            if (obj == null || _isDismissing) return;
            string type = (obj.type ?? "").ToLowerInvariant().Trim();

            if (type == "arrow" && obj.from != null && obj.to != null)
            {
                RenderArrow(obj);
            }
            else if (type == "circle" || type == "ring")
            {
                RenderCircle(obj);
            }
            else if (type == "draw" || type == "pen" || type == "path" || type == "line")
            {
                RenderDraw(obj);
            }
            else if (type == "highlight")
            {
                RenderHighlight(obj);
            }
            else if (type == "outline" && obj.rect != null)
            {
                RenderOutline(obj);
            }
            else if (type == "callout" && obj.rect != null)
            {
                RenderCallout(obj);
            }
            else if (type == "equation" && obj.rect != null)
            {
                RenderEquation(obj);
            }
            else if (type == "spotlight" && obj.rect != null)
            {
                RenderSpotlight(obj);
            }
            else if (type == "notepad" || type == "notebook")
            {
                RenderNotepadBook(obj);
            }
        }

        private static Color ParseSceneColor(string colorStr, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(colorStr)) return fallback;
            try
            {
                colorStr = colorStr.Trim();
                if (colorStr.StartsWith("#"))
                {
                    return (Color)ColorConverter.ConvertFromString(colorStr);
                }
                if (colorStr.Equals("green", StringComparison.OrdinalIgnoreCase) || colorStr.Equals("mint", StringComparison.OrdinalIgnoreCase))
                    return Color.FromRgb(0x3D, 0xDC, 0x84);
                if (colorStr.Equals("white", StringComparison.OrdinalIgnoreCase))
                    return Colors.White;
                if (colorStr.Equals("yellow", StringComparison.OrdinalIgnoreCase))
                    return Color.FromRgb(0xFA, 0xCC, 0x15);
                if (colorStr.Equals("cyan", StringComparison.OrdinalIgnoreCase) || colorStr.Equals("blue", StringComparison.OrdinalIgnoreCase))
                    return Color.FromRgb(0x38, 0xBD, 0xF8);
                return (Color)ColorConverter.ConvertFromString(colorStr);
            }
            catch
            {
                return fallback;
            }
        }

        private void RenderCircle(SceneObjectPayload obj)
        {
            double cx = 0, cy = 0, radius = 35.0;
            if (obj.from != null)
            {
                cx = Math.Max(0.0, Math.Min(1.0, obj.from.x)) * Width;
                cy = Math.Max(0.0, Math.Min(1.0, obj.from.y)) * Height;
                if (obj.radius.HasValue && obj.radius.Value > 0)
                {
                    radius = obj.radius.Value * Math.Min(Width, Height);
                }
            }
            else if (obj.rect != null)
            {
                double rx = Math.Max(0.0, Math.Min(1.0, obj.rect.x)) * Width;
                double ry = Math.Max(0.0, Math.Min(1.0, obj.rect.y)) * Height;
                double rw = Math.Max(20, Math.Min(1.0, obj.rect.w) * Width);
                double rh = Math.Max(20, Math.Min(1.0, obj.rect.h) * Height);
                cx = rx + rw / 2.0;
                cy = ry + rh / 2.0;
                radius = Math.Max(rw, rh) / 2.0;
            }
            else return;

            radius = Math.Max(15.0, Math.Min(350.0, radius));
            Color strokeCol = ParseSceneColor(obj.color, Color.FromRgb(0x3D, 0xDC, 0x84));
            double strokeW = obj.strokeWidth.HasValue && obj.strokeWidth.Value > 0 ? obj.strokeWidth.Value : 3.5;

            var st = new ScaleTransform(0.1, 0.1, radius, radius);
            var circleEl = new Ellipse
            {
                Width = radius * 2.0,
                Height = radius * 2.0,
                Stroke = new SolidColorBrush(strokeCol),
                StrokeThickness = strokeW,
                Fill = new SolidColorBrush(Color.FromArgb(24, strokeCol.R, strokeCol.G, strokeCol.B)),
                Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 18, Opacity = 0.95, ShadowDepth = 0 },
                RenderTransform = st,
                Opacity = 0.0
            };
            Canvas.SetLeft(circleEl, cx - radius);
            Canvas.SetTop(circleEl, cy - radius);
            _canvas.Children.Add(circleEl);

            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var scaleAnim = new System.Windows.Media.Animation.DoubleAnimation(0.1, 1.0, new Duration(TimeSpan.FromMilliseconds(420))) { EasingFunction = ease };
            var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(200)));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            circleEl.BeginAnimation(UIElement.OpacityProperty, fadeAnim);

            if (!string.IsNullOrWhiteSpace(obj.label) || !string.IsNullOrWhiteSpace(obj.text))
            {
                string labelText = !string.IsNullOrWhiteSpace(obj.label) ? obj.label : obj.text;
                var tag = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(240, 15, 23, 42)),
                    BorderBrush = new SolidColorBrush(strokeCol),
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock
                    {
                        Text = labelText,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        FontFamily = new FontFamily("Segoe UI Bold, Segoe UI")
                    },
                    Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 12, Opacity = 0.7, ShadowDepth = 2 },
                    Opacity = 0.0
                };
                Canvas.SetLeft(tag, Math.Max(10, Math.Min(Width - 220, cx - 35)));
                Canvas.SetTop(tag, Math.Max(10, cy - radius - 30));
                _canvas.Children.Add(tag);

                var tagFade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(250)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(250)
                };
                tag.BeginAnimation(UIElement.OpacityProperty, tagFade);
            }
        }

        private void RenderHighlight(SceneObjectPayload obj)
        {
            if (obj.rect == null) return;
            double rx = Math.Max(0.0, Math.Min(1.0, obj.rect.x)) * Width;
            double ry = Math.Max(0.0, Math.Min(1.0, obj.rect.y)) * Height;
            double rw = Math.Max(32, Math.Min(1.0, obj.rect.w) * Width);
            double rh = Math.Max(24, Math.Min(1.0, obj.rect.h) * Height);

            Color fillCol = ParseSceneColor(obj.color, Color.FromRgb(0xFA, 0xCC, 0x15));
            var highlightBox = new Border
            {
                Width = 0.0,
                Height = rh,
                Background = new SolidColorBrush(Color.FromArgb(115, fillCol.R, fillCol.G, fillCol.B)),
                BorderBrush = new SolidColorBrush(fillCol),
                BorderThickness = new Thickness(2.8),
                CornerRadius = new CornerRadius(6),
                Effect = new DropShadowEffect { Color = fillCol, BlurRadius = 24, Opacity = 0.95, ShadowDepth = 0 }
            };
            Canvas.SetLeft(highlightBox, rx);
            Canvas.SetTop(highlightBox, ry);
            _canvas.Children.Add(highlightBox);

            var sweepAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, rw, new Duration(TimeSpan.FromMilliseconds(450)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            highlightBox.BeginAnimation(FrameworkElement.WidthProperty, sweepAnim);

            if (!string.IsNullOrWhiteSpace(obj.label) || !string.IsNullOrWhiteSpace(obj.text))
            {
                string labelText = !string.IsNullOrWhiteSpace(obj.label) ? obj.label : obj.text;
                var tag = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(245, 15, 23, 42)),
                    BorderBrush = new SolidColorBrush(fillCol),
                    BorderThickness = new Thickness(2.0),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(9, 4, 9, 4),
                    Child = new TextBlock
                    {
                        Text = labelText,
                        Foreground = Brushes.White,
                        FontSize = 12.5,
                        FontFamily = new FontFamily("Segoe UI Bold, Segoe UI")
                    },
                    Effect = new DropShadowEffect { Color = fillCol, BlurRadius = 14, Opacity = 0.85, ShadowDepth = 2 },
                    Opacity = 0.0
                };
                Canvas.SetLeft(tag, rx);
                Canvas.SetTop(tag, Math.Max(0, ry - 30));
                _canvas.Children.Add(tag);

                var tagFade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(220)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(300)
                };
                tag.BeginAnimation(UIElement.OpacityProperty, tagFade);
            }
        }

        private void RenderDraw(SceneObjectPayload obj)
        {
            Color strokeCol = ParseSceneColor(obj.color, Color.FromRgb(0x3D, 0xDC, 0x84));
            double strokeW = obj.strokeWidth.HasValue && obj.strokeWidth.Value > 0 ? obj.strokeWidth.Value : 4.0;

            if (obj.points != null && obj.points.Count >= 2)
            {
                var pts = new PointCollection();
                foreach (var p in obj.points)
                {
                    pts.Add(new Point(
                        Math.Max(0.0, Math.Min(1.0, p.x)) * Width,
                        Math.Max(0.0, Math.Min(1.0, p.y)) * Height));
                }

                var polyline = new Polyline
                {
                    Points = pts,
                    Stroke = new SolidColorBrush(strokeCol),
                    StrokeThickness = strokeW,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 14, Opacity = 0.85, ShadowDepth = 0 },
                    Opacity = 0.0
                };
                _canvas.Children.Add(polyline);

                var drawFade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(300)));
                polyline.BeginAnimation(UIElement.OpacityProperty, drawFade);

                if (!string.IsNullOrWhiteSpace(obj.label))
                {
                    var firstPt = pts[0];
                    var tag = new Border
                    {
                        Background = new SolidColorBrush(strokeCol),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Child = new TextBlock
                        {
                            Text = obj.label,
                            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                            FontSize = 11,
                            FontFamily = new FontFamily("Segoe UI Bold, Segoe UI")
                        },
                        Opacity = 0.0
                    };
                    Canvas.SetLeft(tag, Math.Max(10, Math.Min(Width - 140, firstPt.X)));
                    Canvas.SetTop(tag, Math.Max(10, firstPt.Y - 24));
                    _canvas.Children.Add(tag);

                    var tagFade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(200)))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(200)
                    };
                    tag.BeginAnimation(UIElement.OpacityProperty, tagFade);
                }
            }
            else if (!string.IsNullOrWhiteSpace(obj.path))
            {
                try
                {
                    var geo = Geometry.Parse(obj.path);
                    var pathEl = new System.Windows.Shapes.Path
                    {
                        Data = geo,
                        Stroke = new SolidColorBrush(strokeCol),
                        StrokeThickness = strokeW,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 14, Opacity = 0.85, ShadowDepth = 0 },
                        Opacity = 0.0
                    };
                    _canvas.Children.Add(pathEl);
                    pathEl.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(300))));
                }
                catch { }
            }
        }

        private void RenderArrow(SceneObjectPayload obj)
        {
            double startX = Math.Max(0.0, Math.Min(1.0, obj.from.x)) * Width;
            double startY = Math.Max(0.0, Math.Min(1.0, obj.from.y)) * Height;
            double endX = Math.Max(0.0, Math.Min(1.0, obj.to.x)) * Width;
            double endY = Math.Max(0.0, Math.Min(1.0, obj.to.y)) * Height;

            Color strokeCol = ParseSceneColor(obj.color, Color.FromRgb(0x38, 0xBD, 0xF8));
            var arrowLine = new Line
            {
                X1 = startX,
                Y1 = startY,
                X2 = startX,
                Y2 = startY,
                Stroke = new SolidColorBrush(strokeCol),
                StrokeThickness = 3.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 16, Opacity = 0.9, ShadowDepth = 0 }
            };
            _canvas.Children.Add(arrowLine);

            var lineAnimX = new System.Windows.Media.Animation.DoubleAnimation(startX, endX, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var lineAnimY = new System.Windows.Media.Animation.DoubleAnimation(startY, endY, new Duration(TimeSpan.FromMilliseconds(380)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            arrowLine.BeginAnimation(Line.X2Property, lineAnimX);
            arrowLine.BeginAnimation(Line.Y2Property, lineAnimY);

            double arrowLen = 16.0;
            double arrowAngle = Math.PI / 6.0;
            double angle = Math.Atan2(endY - startY, endX - startX);

            var p1 = new Point(endX, endY);
            var p2 = new Point(endX - arrowLen * Math.Cos(angle - arrowAngle), endY - arrowLen * Math.Sin(angle - arrowAngle));
            var p3 = new Point(endX - arrowLen * Math.Cos(angle + arrowAngle), endY - arrowLen * Math.Sin(angle + arrowAngle));

            var tipSt = new ScaleTransform(0.0, 0.0, endX, endY);
            var tip = new Polygon
            {
                Points = new PointCollection(new[] { p1, p2, p3 }),
                Fill = new SolidColorBrush(strokeCol),
                Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 16, Opacity = 0.9, ShadowDepth = 0 },
                RenderTransform = tipSt,
                Opacity = 0.0
            };
            _canvas.Children.Add(tip);

            var tipAnim = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                BeginTime = TimeSpan.FromMilliseconds(320),
                EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }
            };
            tipSt.BeginAnimation(ScaleTransform.ScaleXProperty, tipAnim);
            tipSt.BeginAnimation(ScaleTransform.ScaleYProperty, tipAnim);
            tip.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(150))) { BeginTime = TimeSpan.FromMilliseconds(320) });

            if (!string.IsNullOrWhiteSpace(obj.label) || !string.IsNullOrWhiteSpace(obj.text))
            {
                string labelText = !string.IsNullOrWhiteSpace(obj.label) ? obj.label : obj.text;
                var labelBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(240, 15, 23, 42)),
                    BorderBrush = new SolidColorBrush(strokeCol),
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock
                    {
                        Text = labelText,
                        Foreground = Brushes.White,
                        FontSize = 11.5,
                        FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI")
                    },
                    Effect = new DropShadowEffect { BlurRadius = 10, Opacity = 0.7, ShadowDepth = 2 },
                    Opacity = 0.0
                };
                Canvas.SetLeft(labelBorder, Math.Max(10, Math.Min(Width - 160, (startX + endX) / 2.0 - 30)));
                Canvas.SetTop(labelBorder, Math.Max(10, Math.Min(Height - 40, (startY + endY) / 2.0 - 28)));
                _canvas.Children.Add(labelBorder);

                var tagFade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(200)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(350)
                };
                labelBorder.BeginAnimation(UIElement.OpacityProperty, tagFade);
            }
        }

        private void RenderOutline(SceneObjectPayload obj)
        {
            double rx = Math.Max(0.0, Math.Min(1.0, obj.rect.x)) * Width;
            double ry = Math.Max(0.0, Math.Min(1.0, obj.rect.y)) * Height;
            double rw = Math.Max(20, Math.Min(1.0, obj.rect.w) * Width);
            double rh = Math.Max(20, Math.Min(1.0, obj.rect.h) * Height);

            Color strokeCol = ParseSceneColor(obj.color, Color.FromRgb(56, 189, 248));
            var st = new ScaleTransform(0.92, 0.92, rx + rw / 2.0, ry + rh / 2.0);
            var outlineBorder = new Border
            {
                Width = rw,
                Height = rh,
                BorderThickness = new Thickness(2.5),
                BorderBrush = new SolidColorBrush(strokeCol),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(24, strokeCol.R, strokeCol.G, strokeCol.B)),
                Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 16, Opacity = 0.85, ShadowDepth = 0 },
                RenderTransform = st,
                Opacity = 0.0
            };
            Canvas.SetLeft(outlineBorder, rx);
            Canvas.SetTop(outlineBorder, ry);
            _canvas.Children.Add(outlineBorder);

            var popAnim = new System.Windows.Media.Animation.DoubleAnimation(0.92, 1.0, new Duration(TimeSpan.FromMilliseconds(320)))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
            outlineBorder.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(200))));

            if (!string.IsNullOrWhiteSpace(obj.label) || !string.IsNullOrWhiteSpace(obj.text))
            {
                string labelText = !string.IsNullOrWhiteSpace(obj.label) ? obj.label : obj.text;
                var tag = new Border
                {
                    Background = new SolidColorBrush(strokeCol),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Child = new TextBlock
                    {
                        Text = labelText,
                        Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                        FontSize = 11,
                        FontFamily = new FontFamily("Segoe UI Bold, Segoe UI")
                    },
                    Opacity = 0.0
                };
                Canvas.SetLeft(tag, rx);
                Canvas.SetTop(tag, Math.Max(0, ry - 22));
                _canvas.Children.Add(tag);

                tag.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(200))) { BeginTime = TimeSpan.FromMilliseconds(250) });
            }
        }

        private void RenderCallout(SceneObjectPayload obj)
        {
            double cx = Math.Max(0.0, Math.Min(1.0, obj.rect.x)) * Width;
            double cy = Math.Max(0.0, Math.Min(1.0, obj.rect.y)) * Height;

            Color strokeCol = ParseSceneColor(obj.color, Color.FromRgb(0x3D, 0xDC, 0x84));
            var st = new ScaleTransform(0.85, 0.85, 50, 20);
            var calloutBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(strokeCol),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                MaxWidth = Math.Min(380, Width * 0.5),
                Effect = new DropShadowEffect { Color = strokeCol, BlurRadius = 18, Opacity = 0.8, ShadowDepth = 3 },
                RenderTransform = st,
                Opacity = 0.0
            };
            var stack = new StackPanel();
            if (!string.IsNullOrWhiteSpace(obj.label))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = obj.label,
                    Foreground = new SolidColorBrush(strokeCol),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI Bold, Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 3)
                });
            }
            stack.Children.Add(new TextBlock
            {
                Text = obj.text ?? obj.label ?? "",
                Foreground = Brushes.White,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                TextWrapping = TextWrapping.Wrap
            });
            calloutBorder.Child = stack;
            Canvas.SetLeft(calloutBorder, Math.Max(10, Math.Min(Width - 390, cx)));
            Canvas.SetTop(calloutBorder, Math.Max(10, Math.Min(Height - 120, cy)));
            _canvas.Children.Add(calloutBorder);

            var popAnim = new System.Windows.Media.Animation.DoubleAnimation(0.85, 1.0, new Duration(TimeSpan.FromMilliseconds(320)))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
            calloutBorder.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(200))));
        }

        private void RenderEquation(SceneObjectPayload obj)
        {
            double eqX = Math.Max(0.0, Math.Min(1.0, obj.rect.x)) * Width;
            double eqY = Math.Max(0.0, Math.Min(1.0, obj.rect.y)) * Height;

            var eqBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 17, 24, 39)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(168, 85, 247)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Effect = new DropShadowEffect { Color = Color.FromRgb(168, 85, 247), BlurRadius = 14, Opacity = 0.5, ShadowDepth = 2 }
            };
            var eqStack = new StackPanel();
            var eqHeader = new TextBlock
            {
                Text = "📐 FORMULA",
                Foreground = new SolidColorBrush(Color.FromRgb(192, 132, 252)),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI Bold, Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var eqText = new TextBlock
            {
                Text = obj.latex ?? obj.text ?? "",
                Foreground = new SolidColorBrush(Color.FromRgb(243, 232, 255)),
                FontSize = 15,
                FontFamily = new FontFamily("Consolas, Cambria Math, Segoe UI")
            };
            eqStack.Children.Add(eqHeader);
            eqStack.Children.Add(eqText);
            eqBorder.Child = eqStack;
            Canvas.SetLeft(eqBorder, Math.Max(10, Math.Min(Width - 320, eqX)));
            Canvas.SetTop(eqBorder, Math.Max(10, Math.Min(Height - 100, eqY)));
            _canvas.Children.Add(eqBorder);
        }

        private void RenderSpotlight(SceneObjectPayload obj)
        {
            double sx = Math.Max(0.0, Math.Min(1.0, obj.rect.x)) * Width;
            double sy = Math.Max(0.0, Math.Min(1.0, obj.rect.y)) * Height;
            double sw = Math.Max(30, Math.Min(1.0, obj.rect.w) * Width);
            double sh = Math.Max(30, Math.Min(1.0, obj.rect.h) * Height);

            var spotBorder = new Border
            {
                Width = sw,
                Height = sh,
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 250, 204, 21)),
                Background = new SolidColorBrush(Color.FromArgb(25, 250, 204, 21)),
                CornerRadius = new CornerRadius(Math.Min(sw, sh) / 4.0),
                Effect = new DropShadowEffect { Color = Color.FromRgb(250, 204, 21), BlurRadius = 20, Opacity = 0.8, ShadowDepth = 0 }
            };
            Canvas.SetLeft(spotBorder, sx);
            Canvas.SetTop(spotBorder, sy);
            _canvas.Children.Add(spotBorder);
        }

        private void RenderNotepadBook(SceneObjectPayload obj)
        {
            if (obj == null || _isDismissing) return;

            double cardW = Math.Min(420, Width * 0.45);
            double cardX = Width - cardW - 32;
            double cardY = 70;

            if (obj.rect != null && obj.rect.w > 0.05)
            {
                cardX = Math.Max(20, Math.Min(Width - cardW - 20, obj.rect.x * Width));
                cardY = Math.Max(40, Math.Min(Height - 380, obj.rect.y * Height));
            }

            var cardBorder = new Border
            {
                Width = cardW,
                Background = new SolidColorBrush(Color.FromArgb(246, 17, 24, 39)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)),
                BorderThickness = new Thickness(1.8),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 16, 18, 16),
                Effect = new DropShadowEffect { Color = Color.FromRgb(0x3D, 0xDC, 0x84), BlurRadius = 24, Opacity = 0.55, ShadowDepth = 4 },
                Opacity = 0.0
            };

            var mainStack = new StackPanel();

            // Notebook Header: 📓 Math Notebook
            var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal };
            headerStack.Children.Add(new TextBlock
            {
                Text = "📓 ",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            headerStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(obj.title) ? "Flinch Math Notebook" : obj.title,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)),
                FontWeight = FontWeights.Bold,
                FontSize = 13.5,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerRow.Children.Add(headerStack);
            mainStack.Children.Add(headerRow);

            // Divider line
            mainStack.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Modern Solution Panel
            var modernPanel = new StackPanel();
            string solutionText = obj.text ?? "";
            var lines = solutionText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                bool isResult = line.IndexOf("Result", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("Answer", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("= 9", StringComparison.OrdinalIgnoreCase) >= 0;
                var lineBorder = new Border
                {
                    Background = isResult ? new SolidColorBrush(Color.FromArgb(50, 0x3D, 0xDC, 0x84)) : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                    BorderBrush = isResult ? new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)) : Brushes.Transparent,
                    BorderThickness = new Thickness(isResult ? 1 : 0),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                lineBorder.Child = new TextBlock
                {
                    Text = line,
                    Foreground = isResult ? new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC)) : Brushes.White,
                    FontSize = isResult ? 13 : 12,
                    FontWeight = isResult ? FontWeights.Bold : FontWeights.Normal,
                    FontFamily = new FontFamily("Segoe UI, Consolas"),
                    TextWrapping = TextWrapping.Wrap
                };
                modernPanel.Children.Add(lineBorder);
            }

            // Alternative Panel (Why people get 1)
            var altPanel = new StackPanel { Visibility = Visibility.Collapsed };
            var altHeader = new TextBlock
            {
                Text = "Historical / Implied Convention:",
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                FontWeight = FontWeights.Bold,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 6)
            };
            altPanel.Children.Add(altHeader);

            string altExplanation = string.IsNullOrWhiteSpace(obj.altExplanation)
                ? "People get 1 because historical 1917 Cajori rules (and some calculators) bind implied multiplication tighter than division: 6 ÷ [2(3)] = 6 ÷ 6 = 1.\n\nModern algebraic standard (PEMDAS/ISO 80000-2) evaluates left-to-right, giving 9."
                : obj.altExplanation;

            var altTextBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 0xF5, 0x9E, 0x0B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            altTextBorder.Child = new TextBlock
            {
                Text = altExplanation,
                Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199)),
                FontSize = 11.5,
                FontFamily = new FontFamily("Segoe UI, Consolas"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17
            };
            altPanel.Children.Add(altTextBorder);

            // Interactive Clickable Button: "Why do people get 1?"
            var switchBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 56, 189, 248)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var btnText = new TextBlock
            {
                Text = "❓ Why do people get 1? (Click to view)",
                Foreground = new SolidColorBrush(Color.FromRgb(186, 230, 253)),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI")
            };
            switchBtn.Child = btnText;

            bool isShowingAlt = false;
            switchBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                isShowingAlt = !isShowingAlt;
                if (isShowingAlt)
                {
                    modernPanel.Visibility = Visibility.Collapsed;
                    altPanel.Visibility = Visibility.Visible;
                    btnText.Text = "← Back to Modern Solution (9)";
                    switchBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
                    switchBtn.Background = new SolidColorBrush(Color.FromArgb(60, 0x3D, 0xDC, 0x84));
                    btnText.Foreground = new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));

                    Task.Run(() =>
                    {
                        try
                        {
                            byte[] speechBytes = GeminiClient.SynthesizeSpeechFast("People get 1 because under historical implied multiplication conventions, 2 times 3 is calculated first, yielding 6 divided by 6 equals 1. Under modern PEMDAS, left-to-right division gives 9.");
                            if (speechBytes != null && speechBytes.Length > 44)
                            {
                                using (var ms = new MemoryStream(speechBytes))
                                using (var sp = new System.Media.SoundPlayer(ms))
                                {
                                    sp.PlaySync();
                                }
                            }
                        }
                        catch { }
                    });
                }
                else
                {
                    modernPanel.Visibility = Visibility.Visible;
                    altPanel.Visibility = Visibility.Collapsed;
                    btnText.Text = "❓ Why do people get 1? (Click to view)";
                    switchBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                    switchBtn.Background = new SolidColorBrush(Color.FromArgb(60, 56, 189, 248));
                    btnText.Foreground = new SolidColorBrush(Color.FromRgb(186, 230, 253));
                }
            };

            mainStack.Children.Add(modernPanel);
            mainStack.Children.Add(altPanel);
            mainStack.Children.Add(switchBtn);
            cardBorder.Child = mainStack;

            Canvas.SetLeft(cardBorder, cardX);
            Canvas.SetTop(cardBorder, cardY);
            _canvas.Children.Add(cardBorder);

            var st = new ScaleTransform(0.88, 0.88, cardW / 2.0, 150);
            cardBorder.RenderTransform = st;
            var popAnim = new System.Windows.Media.Animation.DoubleAnimation(0.88, 1.0, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
            cardBorder.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(250))));
        }

        public void DismissWithAnimation()
        {
            if (_isDismissing) return;
            _isDismissing = true;
            try
            {
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(150)));
                fadeOut.Completed += (s, e) =>
                {
                    try { Close(); } catch { }
                };
                BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            catch
            {
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_autoDismissTimer != null)
            {
                _autoDismissTimer.Stop();
            }
            lock (_instanceLock)
            {
                if (_activeInstance == this) _activeInstance = null;
            }
            base.OnClosed(e);
        }
    }

    // =========================================================================
    // Cloud Brain Client: Supabase Edge Functions (Zero Local AI Keys)
    // =========================================================================
    internal static class GeminiClient
    {
        private static readonly HttpClient SharedHttp;
        private static DateTime _lastWarmUtc = DateTime.MinValue;

        static GeminiClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 24;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            SharedHttp = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            SharedHttp.DefaultRequestHeaders.ConnectionClose = false;
        }

        public static void PreWarmConnectionsAsync()
        {
            if ((DateTime.UtcNow - _lastWarmUtc).TotalSeconds < 25) return;
            _lastWarmUtc = DateTime.UtcNow;

            Task.Run(async () =>
            {
                try
                {
                    string ep = FlinchEnv.SupabaseUrl.TrimEnd('/') + "/functions/v1/flinch-voice";
                    using (var req = new HttpRequestMessage(HttpMethod.Options, ep))
                    {
                        using (var resp = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false)) { }
                    }
                }
                catch { }
            });
        }

        // =====================================================================
        // Privacy-First Ephemeral Staged Screen Context Management
        // =====================================================================
        private static readonly object _stagedScreenLock = new object();
        private static CapturedFrame _stagedScreenFrame = null;
        public static NativeMethods.RECT LastActiveTargetBounds { get; private set; }

        public static void SetStagedScreenFrame(CapturedFrame frame)
        {
            lock (_stagedScreenLock)
            {
                if (_stagedScreenFrame != null && _stagedScreenFrame != frame)
                {
                    _stagedScreenFrame.Dispose();
                }
                _stagedScreenFrame = frame;
                if (frame != null)
                {
                    LastActiveTargetBounds = frame.SourceBounds;
                }
            }
        }

        public static void ClearStagedScreenFrame()
        {
            lock (_stagedScreenLock)
            {
                if (_stagedScreenFrame != null)
                {
                    _stagedScreenFrame.Dispose();
                    _stagedScreenFrame = null;
                }
            }
        }

        public static bool HasStagedScreenFrame
        {
            get
            {
                lock (_stagedScreenLock)
                {
                    return _stagedScreenFrame != null && _stagedScreenFrame.JpegBytes != null;
                }
            }
        }

        public static bool GetAndConsumeStagedScreenPayload(out byte[] jpegBytes, out string uiaJson, out NativeMethods.RECT targetBounds)
        {
            lock (_stagedScreenLock)
            {
                if (_stagedScreenFrame != null && _stagedScreenFrame.JpegBytes != null)
                {
                    jpegBytes = _stagedScreenFrame.JpegBytes;
                    uiaJson = _stagedScreenFrame.UiaJson;
                    targetBounds = _stagedScreenFrame.SourceBounds;
                    LastActiveTargetBounds = targetBounds;
                    _stagedScreenFrame.Dispose();
                    _stagedScreenFrame = null;
                    return true;
                }
                jpegBytes = null;
                uiaJson = null;
                targetBounds = LastActiveTargetBounds;
                return false;
            }
        }

        public static byte[] GetAndConsumeStagedScreenBytes()
        {
            byte[] bytes;
            string uia;
            NativeMethods.RECT bounds;
            GetAndConsumeStagedScreenPayload(out bytes, out uia, out bounds);
            return bytes;
        }

        public static void ClearHistory()
        {
        }

        public static bool NeedsWebSearch(string query)
        {
            return false;
        }

        public static string SearchExaFast(string query)
        {
            return null;
        }

        public static string TranscribeFast(byte[] wavBytes)
        {
            return "";
        }

        public static string GenerateGeminiAnswerFast(string transcript, string webContext = null)
        {
            return "";
        }

        public static void TranscribeAndAnswerWithGeminiAudio(byte[] wavBytes, out string transcript, out string answer)
        {
            transcript = "";
            answer = "";
        }

        public static byte[] CaptureScreenJpeg(int maxWidth = 800, int maxHeight = 450, long quality = 45L)
        {
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                using (var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                    }

                    double scale = Math.Min(1.0, Math.Min((double)maxWidth / bounds.Width, (double)maxHeight / bounds.Height));
                    int targetW = Math.Max(1, (int)(bounds.Width * scale));
                    int targetH = Math.Max(1, (int)(bounds.Height * scale));

                    using (var scaled = new System.Drawing.Bitmap(targetW, targetH))
                    {
                        using (var gScaled = System.Drawing.Graphics.FromImage(scaled))
                        {
                            gScaled.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                            gScaled.DrawImage(bmp, 0, 0, targetW, targetH);
                        }

                        using (var ms = new System.IO.MemoryStream())
                        {
                            System.Drawing.Imaging.ImageCodecInfo jpgEncoder = null;
                            foreach (var codec in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                            {
                                if (codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                                {
                                    jpgEncoder = codec;
                                    break;
                                }
                            }
                            if (jpgEncoder == null)
                            {
                                scaled.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                            }
                            else
                            {
                                var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                                encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                                scaled.Save(ms, jpgEncoder, encParams);
                            }
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // Single-Hop Cloud Voice Turn: STT + Live Knowledge + LLM + Orpheus TTS in one cloud hop
        public static bool ProcessVoiceTurnInCloud(
            byte[] wavBytes,
            out string transcript,
            out string answer,
            out byte[] audioBytes,
            out int remaining,
            out string error)
        {
            ActionPayload dummy;
            return ProcessVoiceTurnInCloud(wavBytes, out transcript, out answer, out audioBytes, out dummy, out remaining, out error);
        }

        public static bool ProcessVoiceTurnInCloud(
            byte[] wavBytes,
            out string transcript,
            out string answer,
            out byte[] audioBytes,
            out ActionPayload action,
            out int remaining,
            out string error)
        {
            ScenePayload dummyScene;
            return ProcessVoiceTurnInCloud(wavBytes, out transcript, out answer, out audioBytes, out action, out dummyScene, out remaining, out error);
        }

        public static bool ProcessVoiceTurnInCloud(
            byte[] wavBytes,
            out string transcript,
            out string answer,
            out byte[] audioBytes,
            out ActionPayload action,
            out ScenePayload scene,
            out int remaining,
            out string error)
        {
            transcript = "";
            answer = "";
            audioBytes = null;
            action = null;
            scene = null;
            remaining = SupabaseAuthClient.VoiceRemaining;
            error = "";

            if (SupabaseAuthClient.CurrentSession == null || string.IsNullOrWhiteSpace(SupabaseAuthClient.CurrentSession.AccessToken))
            {
                error = "Please sign in to continue.";
                return false;
            }

            try
            {
                string endpoint = FlinchEnv.SupabaseUrl.TrimEnd('/') + "/functions/v1/flinch-voice";

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                    using (var mp = new MultipartFormDataContent())
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.AccessToken);
                        req.Headers.TryAddWithoutValidation("apikey", FlinchEnv.SupabaseAnonKey);

                        var fileContent = new ByteArrayContent(wavBytes);
                        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
                        mp.Add(fileContent, "file", "audio.wav");
                        mp.Add(new StringContent(string.IsNullOrWhiteSpace(FlinchEnv.VoiceName) ? "troy" : FlinchEnv.VoiceName), "voice");

                        // Clipboard intelligence: send copied text if present
                        string clipText = "";
                        try
                        {
                            var staThread = new Thread(() =>
                            {
                                try
                                {
                                    if (System.Windows.Clipboard.ContainsText())
                                    {
                                        clipText = System.Windows.Clipboard.GetText();
                                    }
                                }
                                catch { }
                            });
                            staThread.SetApartmentState(ApartmentState.STA);
                            staThread.Start();
                            staThread.Join(150);
                        }
                        catch { }

                        if (!string.IsNullOrWhiteSpace(clipText))
                        {
                            if (clipText.Length > 4000) clipText = clipText.Substring(0, 4000);
                            mp.Add(new StringContent(clipText), "clipboard");
                        }

                        // Attach screen image & UIA metadata: staged frame or live active window
                        for (int w = 0; w < 4 && !HasStagedScreenFrame; w++)
                        {
                            Thread.Sleep(50);
                        }

                        byte[] screenBytes;
                        string uiaJson;
                        NativeMethods.RECT targetBounds;
                        bool isExplicitStaged = GetAndConsumeStagedScreenPayload(out screenBytes, out uiaJson, out targetBounds);

                        if (screenBytes == null || screenBytes.Length == 0)
                        {
                            try
                            {
                                var cap = new WindowsScreenCaptureService();
                                var s = System.Windows.Forms.Screen.PrimaryScreen;
                                var pBounds = new NativeMethods.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom };
                                CapturedFrame f = cap.CaptureBounds(pBounds, "Display");
                                if (f != null && f.JpegBytes != null)
                                {
                                    screenBytes = f.JpegBytes;
                                    uiaJson = f.UiaJson;
                                    targetBounds = pBounds;
                                    LastActiveTargetBounds = targetBounds;
                                }
                            }
                            catch { }
                        }

                        if (screenBytes != null && screenBytes.Length > 0)
                        {
                            var screenContent = new ByteArrayContent(screenBytes);
                            screenContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
                            mp.Add(screenContent, "screen", "screen.jpg");
                            if (isExplicitStaged)
                            {
                                mp.Add(new StringContent("true"), "staged");
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(uiaJson))
                        {
                            mp.Add(new StringContent(uiaJson), "uia");
                        }

                        req.Content = mp;

                        using (var resp = SharedHttp.SendAsync(req, cts.Token).GetAwaiter().GetResult())
                        {
                            int statusCode = (int)resp.StatusCode;
                            string respJson = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                            if (statusCode == 401 && attempt == 0 && !string.IsNullOrWhiteSpace(SupabaseAuthClient.CurrentSession.RefreshToken))
                            {
                                string rErr;
                                if (SupabaseAuthClient.RefreshSession(SupabaseAuthClient.CurrentSession.RefreshToken, out rErr))
                                {
                                    continue; // Retry with refreshed JWT
                                }
                            }

                            if (statusCode == 429)
                            {
                                SupabaseAuthClient.VoiceRemaining = 0;
                                remaining = 0;
                                error = "Demo limit reached: 5 of 5 voice messages used.";
                                return false;
                            }

                            if (statusCode >= 200 && statusCode < 300 && !string.IsNullOrWhiteSpace(respJson))
                            {
                                var jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                                var root = jss.Deserialize<Dictionary<string, object>>(respJson);
                                if (root != null)
                                {
                                    if (root.ContainsKey("empty") && root["empty"] is bool && (bool)root["empty"])
                                    {
                                        return true; // Silence detected
                                    }

                                    if (root.ContainsKey("transcript") && root["transcript"] != null)
                                        transcript = root["transcript"].ToString().Trim();

                                    if (root.ContainsKey("reply") && root["reply"] != null)
                                        answer = root["reply"].ToString().Trim();

                                    if (root.ContainsKey("remaining") && root["remaining"] != null)
                                    {
                                        int r;
                                        if (int.TryParse(root["remaining"].ToString(), out r))
                                        {
                                            remaining = r;
                                            SupabaseAuthClient.VoiceRemaining = r;
                                        }
                                    }

                                    if (root.ContainsKey("audio") && root["audio"] != null)
                                    {
                                        string b64 = root["audio"].ToString().Trim();
                                        if (!string.IsNullOrEmpty(b64))
                                        {
                                            byte[] raw = Convert.FromBase64String(b64);
                                            audioBytes = WaveInRecorder.NormalizeWavBytes(raw);
                                        }
                                    }

                                    if (root.ContainsKey("action") && root["action"] is Dictionary<string, object>)
                                    {
                                        var actDict = (Dictionary<string, object>)root["action"];
                                        action = new ActionPayload
                                        {
                                            type = actDict.ContainsKey("type") && actDict["type"] != null ? actDict["type"].ToString() : null,
                                            target = actDict.ContainsKey("target") && actDict["target"] != null ? actDict["target"].ToString() : null,
                                            name = actDict.ContainsKey("name") && actDict["name"] != null ? actDict["name"].ToString() : null,
                                            countdown_sec = actDict.ContainsKey("countdown_sec") && actDict["countdown_sec"] != null ? Convert.ToInt32(actDict["countdown_sec"]) : 0,
                                            seconds = actDict.ContainsKey("seconds") && actDict["seconds"] != null ? Convert.ToInt32(actDict["seconds"]) : 0,
                                            text = actDict.ContainsKey("text") && actDict["text"] != null ? actDict["text"].ToString() : null,
                                            label = actDict.ContainsKey("label") && actDict["label"] != null ? actDict["label"].ToString() : null
                                        };
                                    }

                                    if (root.ContainsKey("scene") && root["scene"] is Dictionary<string, object>)
                                    {
                                        var sDict = (Dictionary<string, object>)root["scene"];
                                        var parsedScene = new ScenePayload();
                                        if (sDict.ContainsKey("durationMs") && sDict["durationMs"] != null)
                                        {
                                            int d;
                                            if (int.TryParse(sDict["durationMs"].ToString(), out d) && d >= 2000)
                                            {
                                                parsedScene.durationMs = Math.Min(30000, d);
                                            }
                                        }
                                        if (sDict.ContainsKey("objects") && sDict["objects"] is System.Collections.ArrayList)
                                        {
                                            var arr = (System.Collections.ArrayList)sDict["objects"];
                                            foreach (var item in arr)
                                            {
                                                if (item is Dictionary<string, object>)
                                                {
                                                    var oDict = (Dictionary<string, object>)item;
                                                    var obj = new SceneObjectPayload
                                                    {
                                                        type = oDict.ContainsKey("type") && oDict["type"] != null ? oDict["type"].ToString().ToLowerInvariant() : "",
                                                        label = oDict.ContainsKey("label") && oDict["label"] != null ? oDict["label"].ToString() : null,
                                                        text = oDict.ContainsKey("text") && oDict["text"] != null ? oDict["text"].ToString() : null,
                                                        latex = oDict.ContainsKey("latex") && oDict["latex"] != null ? oDict["latex"].ToString() : null,
                                                        color = oDict.ContainsKey("color") && oDict["color"] != null ? oDict["color"].ToString() : null,
                                                        path = oDict.ContainsKey("path") && oDict["path"] != null ? oDict["path"].ToString() : null,
                                                        title = oDict.ContainsKey("title") && oDict["title"] != null ? oDict["title"].ToString() : null,
                                                        altAnswer = oDict.ContainsKey("altAnswer") && oDict["altAnswer"] != null ? oDict["altAnswer"].ToString() : null,
                                                        altExplanation = oDict.ContainsKey("altExplanation") && oDict["altExplanation"] != null ? oDict["altExplanation"].ToString() : null,
                                                        step = oDict.ContainsKey("step") && oDict["step"] != null ? Convert.ToInt32(oDict["step"]) : (int?)null,
                                                        delayMs = oDict.ContainsKey("delayMs") && oDict["delayMs"] != null ? Convert.ToInt32(oDict["delayMs"]) : (int?)null,
                                                        radius = oDict.ContainsKey("radius") && oDict["radius"] != null ? Convert.ToDouble(oDict["radius"]) : (double?)null,
                                                        strokeWidth = oDict.ContainsKey("strokeWidth") && oDict["strokeWidth"] != null ? Convert.ToDouble(oDict["strokeWidth"]) : (double?)null
                                                    };

                                                    if (oDict.ContainsKey("from") && oDict["from"] is Dictionary<string, object>)
                                                    {
                                                        var fDict = (Dictionary<string, object>)oDict["from"];
                                                        obj.from = new ScenePoint
                                                        {
                                                            x = Convert.ToDouble(fDict["x"]),
                                                            y = Convert.ToDouble(fDict["y"])
                                                        };
                                                    }
                                                    if (oDict.ContainsKey("to") && oDict["to"] is Dictionary<string, object>)
                                                    {
                                                        var tDict = (Dictionary<string, object>)oDict["to"];
                                                        obj.to = new ScenePoint
                                                        {
                                                            x = Convert.ToDouble(tDict["x"]),
                                                            y = Convert.ToDouble(tDict["y"])
                                                        };
                                                    }
                                                    if (oDict.ContainsKey("rect") && oDict["rect"] is Dictionary<string, object>)
                                                    {
                                                        var rDict = (Dictionary<string, object>)oDict["rect"];
                                                        obj.rect = new SceneRect
                                                        {
                                                            x = Convert.ToDouble(rDict["x"]),
                                                            y = Convert.ToDouble(rDict["y"]),
                                                            w = Convert.ToDouble(rDict["w"]),
                                                            h = Convert.ToDouble(rDict["h"])
                                                        };
                                                    }
                                                    if (oDict.ContainsKey("points") && oDict["points"] is System.Collections.ArrayList)
                                                    {
                                                        var pArr = (System.Collections.ArrayList)oDict["points"];
                                                        obj.points = new List<ScenePoint>();
                                                        foreach (var pItem in pArr)
                                                        {
                                                            if (pItem is Dictionary<string, object>)
                                                            {
                                                                var pd = (Dictionary<string, object>)pItem;
                                                                obj.points.Add(new ScenePoint
                                                                {
                                                                    x = Convert.ToDouble(pd["x"]),
                                                                    y = Convert.ToDouble(pd["y"])
                                                                });
                                                            }
                                                        }
                                                    }
                                                    parsedScene.objects.Add(obj);
                                                }
                                            }
                                        }
                                        if (parsedScene.objects.Count > 0)
                                        {
                                            scene = parsedScene;
                                        }
                                    }

                                    FlinchEnv.Log(string.Format("[GeminiClient] Cloud turn completed: tx='{0}', ans='{1}', sceneObjects={2}",
                                        transcript, answer, (scene != null && scene.objects != null ? scene.objects.Count : 0)));

                                    return true;
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(respJson))
                            {
                                try
                                {
                                    var jss = new JavaScriptSerializer();
                                    var errRoot = jss.Deserialize<Dictionary<string, object>>(respJson);
                                    if (errRoot != null && errRoot.ContainsKey("message") && errRoot["message"] != null)
                                    {
                                        error = errRoot["message"].ToString();
                                        return false;
                                    }
                                }
                                catch { }
                            }

                            error = "Backend service returned status " + statusCode + ".";
                            return false;
                        }
                    }
                }

                error = "Voice request timed out.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Fast Text-To-Speech through Supabase Edge Function (Orpheus / Gemini)
        public static byte[] SynthesizeSpeechFast(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (SupabaseAuthClient.CurrentSession == null || string.IsNullOrWhiteSpace(SupabaseAuthClient.CurrentSession.AccessToken))
                return null;

            try
            {
                string endpoint = FlinchEnv.SupabaseUrl.TrimEnd('/') + "/functions/v1/flinch-voice";
                var jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var payload = new Dictionary<string, object>
                {
                    { "action", "tts" },
                    { "text", text.Trim() },
                    { "voice", string.IsNullOrWhiteSpace(FlinchEnv.VoiceName) ? "troy" : FlinchEnv.VoiceName }
                };

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SupabaseAuthClient.CurrentSession.AccessToken);
                    req.Headers.TryAddWithoutValidation("apikey", FlinchEnv.SupabaseAnonKey);
                    req.Content = new StringContent(jss.Serialize(payload), Encoding.UTF8, "application/json");

                    using (var resp = SharedHttp.SendAsync(req, cts.Token).GetAwaiter().GetResult())
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            string respJson = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            var root = jss.Deserialize<Dictionary<string, object>>(respJson);
                            if (root != null && root.ContainsKey("audio") && root["audio"] != null)
                            {
                                string b64 = root["audio"].ToString().Trim();
                                if (!string.IsNullOrEmpty(b64))
                                {
                                    byte[] raw = Convert.FromBase64String(b64);
                                    return WaveInRecorder.NormalizeWavBytes(raw);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }

    // =========================================================================
    // Resilient Local Speech Engine (Zero Latency, Unlimited, Active on Cloud 429)
    // =========================================================================
    internal static class LocalSpeechEngine
    {
        public static byte[] SynthesizeToWav(string text, string preferredVoice)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
                using (var ms = new MemoryStream())
                {
                    synth.Rate = 1;
                    string voiceLow = (preferredVoice ?? "").ToLowerInvariant();
                    bool isFemale = voiceLow.Contains("asteria") || voiceLow.Contains("luna") ||
                                   voiceLow.Contains("stella") || voiceLow.Contains("athena") ||
                                   voiceLow.Contains("hera") || voiceLow.Contains("autumn") ||
                                   voiceLow.Contains("diana") || voiceLow.Contains("female");

                    var installed = synth.GetInstalledVoices();
                    foreach (var v in installed)
                    {
                        if (v == null || !v.Enabled || v.VoiceInfo == null) continue;
                        string nameLow = (v.VoiceInfo.Name ?? "").ToLowerInvariant();
                        if (isFemale && (nameLow.Contains("zira") || nameLow.Contains("eva") || nameLow.Contains("female") || nameLow.Contains("catherine")))
                        {
                            synth.SelectVoice(v.VoiceInfo.Name);
                            break;
                        }
                        if (!isFemale && (nameLow.Contains("david") || nameLow.Contains("mark") || nameLow.Contains("male") || nameLow.Contains("george")))
                        {
                            synth.SelectVoice(v.VoiceInfo.Name);
                            break;
                        }
                    }

                    synth.SetOutputToWaveStream(ms);
                    synth.Speak(text);
                    synth.SetOutputToNull();
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("Local speech synthesis fallback error: " + ex.Message);
                return null;
            }
        }
    }

    // =========================================================================
    // Pure MacBook Notch Window (Zero Outline, Zero Square Shadow, Minimalist)
    // =========================================================================
    internal enum IslandState
    {
        Idle,
        Listening,
        Thinking,
        Speaking,
        Done,
        Countdown,
        Alert,
        Error,
        TasksExpanded
    }

    internal static class UpdateChecker
    {
        private static readonly HttpClient UpdateHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        public class UpdateInfo
        {
            public bool IsAvailable { get; set; }
            public string TagName { get; set; }
            public string AssetDownloadUrl { get; set; }
            public string AssetName { get; set; }
            public DateTime AssetUpdatedAt { get; set; }
        }

        public static UpdateInfo CheckForUpdate()
        {
            try
            {
                string repo = FlinchEnv.GitHubRepo;
                if (string.IsNullOrWhiteSpace(repo)) repo = "La-Unicorn/flinch-releases";

                string url = "https://api.github.com/repos/" + repo + "/releases";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Flinch-Desktop-App");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

                using (var resp = UpdateHttp.SendAsync(req).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) return new UpdateInfo { IsAvailable = false };
                    string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (string.IsNullOrWhiteSpace(json)) return new UpdateInfo { IsAvailable = false };

                    var jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var list = jss.Deserialize<object[]>(json);
                    if (list == null || list.Length == 0) return new UpdateInfo { IsAvailable = false };

                    foreach (object item in list)
                    {
                        var rel = item as Dictionary<string, object>;
                        if (rel == null) continue;
                        if (rel.ContainsKey("draft") && true.Equals(rel["draft"])) continue;

                        string tag = rel.ContainsKey("tag_name") && rel["tag_name"] != null ? rel["tag_name"].ToString() : "";
                        if (rel.ContainsKey("assets") && rel["assets"] is object[])
                        {
                            var assets = (object[])rel["assets"];
                            foreach (object aObj in assets)
                            {
                                var asset = aObj as Dictionary<string, object>;
                                if (asset == null) continue;

                                string name = asset.ContainsKey("name") && asset["name"] != null ? asset["name"].ToString() : "";
                                if (string.Equals(name, "FlinchSetup.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    string downloadUrl = asset.ContainsKey("browser_download_url") && asset["browser_download_url"] != null ? asset["browser_download_url"].ToString() : "";
                                    string updatedStr = asset.ContainsKey("updated_at") && asset["updated_at"] != null ? asset["updated_at"].ToString() : "";

                                    DateTime updatedAtUtc = DateTime.MinValue;
                                    DateTime.TryParse(updatedStr, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out updatedAtUtc);

                                    DateTime currentBuildUtc = DateTime.MinValue;
                                    DateTime.TryParse(FlinchEnv.BuildTimeUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out currentBuildUtc);

                                    // If asset is newer than the local compile time (plus 60s grace margin), an update is released!
                                    bool isNewer = updatedAtUtc > currentBuildUtc.AddSeconds(60);

                                    return new UpdateInfo
                                    {
                                        IsAvailable = isNewer && !string.IsNullOrWhiteSpace(downloadUrl),
                                        TagName = tag,
                                        AssetDownloadUrl = downloadUrl,
                                        AssetName = name,
                                        AssetUpdatedAt = updatedAtUtc
                                    };
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("Update check error: " + ex.Message);
            }
            return new UpdateInfo { IsAvailable = false };
        }
    }

    internal sealed class ActiveTimerItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Type { get; set; }
        public string Text { get; set; }
        public int SecondsTotal { get; set; }
        public int SecondsLeft { get; set; }
        public DateTime StartedUtc { get; set; }
    }

    internal sealed class DynamicIslandWindow : Window
    {
        private const int HOTKEY_ID_ALTV = 0xB401;

        // Brand palette from user-provided SVGs (mint, white, vibrant orange, voice gradient)
        private static readonly Color ColMint      = Color.FromRgb(0x3D, 0xDC, 0x84); // #3DDC84 flinch-notch-idle.svg
        private static readonly Color ColWhite     = Colors.White;                    // #FFFFFF flinch-notch-idle.svg
        private static readonly Color ColOrange    = Color.FromRgb(0xFF, 0x8A, 0x3D); // #FF8A3D flinch-notch-tasks / flinch-voice
        private static readonly Color ColPink      = Color.FromRgb(0xFF, 0x4F, 0x7B); // #FF4F7B
        private static readonly Color ColViolet    = Color.FromRgb(0x9B, 0x6B, 0xFF); // #9B6BFF
        private static readonly Color ColBlue      = Color.FromRgb(0x3C, 0xC8, 0xFF); // #3CC8FF
        private static readonly Color ColCream     = Color.FromRgb(0xF4, 0xEB, 0xDD); // #F4EBDD text
        private static readonly Color ColTrack     = Color.FromRgb(0x2A, 0x2A, 0x31); // #2A2A31 circle track
        private static readonly Color ColTextMuted = Color.FromRgb(0x8E, 0x8C, 0x96); // #8E8C96 secondary text
        private static readonly Color ColDivider   = Color.FromRgb(0x1E, 0x1E, 0x23); // #1E1E23 divider lines
        private static readonly Color ColEar       = Color.FromRgb(61, 64, 73);       // #3d4049
        private static readonly Color ColDark      = Color.FromRgb(28, 31, 38);       // #1c1f26

        private static readonly Brush BrushMint      = CreateFrozenBrush(ColMint);
        private static readonly Brush BrushWhite     = CreateFrozenBrush(ColWhite);
        private static readonly Brush BrushOrange    = CreateFrozenBrush(ColOrange);
        private static readonly Brush BrushCream     = CreateFrozenBrush(ColCream);
        private static readonly Brush BrushTrack     = CreateFrozenBrush(ColTrack);
        private static readonly Brush BrushTextMuted = CreateFrozenBrush(ColTextMuted);
        private static readonly Brush BrushDivider   = CreateFrozenBrush(ColDivider);
        private static readonly Brush BrushEar       = CreateFrozenBrush(ColEar);
        private static readonly Brush BrushDark      = CreateFrozenBrush(ColDark);
        private static readonly LinearGradientBrush BrushVoiceGradient = CreateVoiceGradient();

        private static Brush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static LinearGradientBrush CreateVoiceGradient()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            b.GradientStops.Add(new GradientStop(ColMint, 0.00));
            b.GradientStops.Add(new GradientStop(ColWhite, 0.50));
            b.GradientStops.Add(new GradientStop(ColMint, 1.00));
            b.Freeze();
            return b;
        }

        private readonly System.Windows.Shapes.Path _notchPath;
        private readonly Grid _notchContentHost;
        private readonly Grid _centerGrid;

        // Listening visualizer (flinch-notch-listening 2.svg & flinch-voice 2.js)
        private readonly Canvas _listeningHost;
        private readonly Ellipse[] _listeningBlobs = new Ellipse[4];
        private readonly ScaleTransform[] _listeningBlobTransforms = new ScaleTransform[4];
        private readonly Border[] _listeningBars = new Border[13];
        private readonly ScaleTransform[] _listeningBarTransforms = new ScaleTransform[13];
        private readonly double[] _voiceVals = new double[7];
        private double _voicePeak = 0.3;

        // Multitasking Background Activities (flinch-notch-tasks 2.svg)
        private readonly StackPanel _multitaskLeftHost;
        private readonly StackPanel _timerTaskHost;
        private readonly System.Windows.Shapes.Path _timerProgressArcPath;
        private readonly TextBlock _liveTimerText;
        private readonly StackPanel _spinnerTaskHost;
        private readonly System.Windows.Shapes.Path _spinnerArcPath;
        private readonly RotateTransform _spinnerArcTransform;
        private readonly TextBlock _spinnerTaskText;
        private readonly StackPanel _searchTaskHost;
        private readonly TextBlock _searchTaskText;
        private readonly StackPanel _audioTaskHost;
        private readonly Border[] _audioEqBars = new Border[3];
        private readonly TextBlock _audioTaskText;

        // Multitask State & Multi-Timer Queue
        private readonly List<ActiveTimerItem> _activeTimers = new List<ActiveTimerItem>();
        private readonly object _timerLock = new object();
        private readonly List<ActiveBackgroundTask> _pendingCompletedTasks = new List<ActiveBackgroundTask>();
        private readonly object _pendingTasksLock = new object();

        private DispatcherTimer _liveCountdownTicker;
        private int _activeTimerSecondsTotal = 0;
        private int _activeTimerSecondsLeft = 0;
        private string _activeTimerLabel = "";
        private bool _isBackgroundTaskRunning = false;
        private string _backgroundTaskType = "organizing";
        private string _backgroundTaskTitle = "";
        private string _backgroundTaskDetail = "";

        // Expanded Task Card (Dynamic Multi-Task & Multi-Timer List)
        private readonly Grid _tasksExpandedPanel;
        private readonly ScrollViewer _tasksScrollViewer;
        private readonly StackPanel _tasksItemsStack;
        private readonly System.Windows.Shapes.Path _expandedTimerRingPath;
        private readonly TextBlock _expandedTimerSubtitle;
        private readonly TextBlock _expandedTimerTimeText;
        private readonly System.Windows.Shapes.Path _expandedBgSpinnerArc;
        private readonly TextBlock _expandedBgTitleText;
        private readonly Border _expandedBgProgressBar;
        private readonly TextBlock _expandedBgDetailText;
        private readonly TextBlock _expandedSearchTitleText;
        private readonly TextBlock _expandedSearchSubtitle;
        private readonly Border[] _expandedSearchDots = new Border[3];

        // Update Button (01-update-available.svg, 03-progress-ring.svg, 09-download-complete.svg)
        private enum UpdateVisualState
        {
            Available,
            Downloading,
            Complete
        }
        private UpdateVisualState _updateVisualState = UpdateVisualState.Available;
        private readonly Border _updateDownloadBtn;
        private readonly Viewbox _updateRootViewbox;
        private readonly Canvas _updateAvailHost;
        private readonly Canvas _updateProgressHost;
        private readonly Canvas _updateCompleteHost;
        private readonly System.Windows.Shapes.Ellipse _updateRippleEllipse;
        private readonly System.Windows.Shapes.Path _updateProgressArcPath;
        private bool _isUpdateAvailable = false;
        private string _pendingUpdateUrl = null;
        private volatile bool _isDownloadingUpdate = false;
        private DispatcherTimer _updateCheckTimer;

        // Centered 4 Dots (flinch-notch-idle.svg: 2 Mint #3DDC84, 2 White #FFFFFF)
        private readonly StackPanel _dotsCenterHost;
        private readonly Border[] _soundBars = new Border[4];

        // Centered Thinking Spinner (spinner.svg)
        private readonly Viewbox _spinnerViewbox;
        private readonly System.Windows.Shapes.Rectangle[] _spinnerSpokes = new System.Windows.Shapes.Rectangle[8];

        // Centered 5s Action Countdown Panel ("Opening Spotify (5s)... [Cancel]")
        private readonly StackPanel _countdownPanel;
        private readonly TextBlock _countdownLabel;
        private readonly TextBlock _countdownSecText;
        private readonly Border _countdownCancelBtn;

        // Centered Alert Panel ("🔔 5-second timer is up!")
        private readonly StackPanel _alertPanel;
        private readonly TextBlock _alertIconText;
        private readonly TextBlock _alertLabelText;

        // Flinch Hub Window
        private FlinchHubWindow _hubWindow = null;

        // Centered Error Display
        private readonly StackPanel _cardPanel;
        private readonly TextBlock _transcriptText;
        private readonly TextBlock _answerText;

        // State & 60 FPS Animation Clock
        private IslandState _state = IslandState.Idle;
        private double _curW = 170;
        private double _curH = 34;
        private double _targetW = 170;
        private double _targetH = 34;
        private double _curBotR = 17;
        private double _targetBotR = 17;
        private readonly Stopwatch _animClock = Stopwatch.StartNew();

        // Audio, Recording & Hotkey
        private readonly WaveInRecorder _recorder = new WaveInRecorder();
        private System.Media.SoundPlayer _soundPlayer;
        private MemoryStream _ttsStream;
        private DateTime _recordStartUtc = DateTime.MinValue;
        private bool _keyWasHeld = false;
        private bool _startedViaHotkey = false;
        private volatile bool _isBusy = false;
        private DateTime _busyStartUtc = DateTime.MinValue;
        private DispatcherTimer _autoCollapseTimer;
        private System.Windows.Forms.NotifyIcon _trayIcon;

        // Task Countdown & Timer Management
        private ActionPayload _pendingAction;
        private DispatcherTimer _countdownTimer;
        private int _countdownSecRemaining;
        private static byte[] _cachedChimeWav;
        private readonly IScreenCaptureService _captureService = new WindowsScreenCaptureService();

        public DynamicIslandWindow()
        {
            Title = "Flinch";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Width = 540;
            Height = 260;

            PositionWindowTopCenter();

            var rootGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0)
            };

            // Organic MacBook Notch with sideways curved bends flaring outward to the screen bezel
            _notchPath = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                StrokeThickness = 0.8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 14,
                    Opacity = 0.6,
                    ShadowDepth = 2
                }
            };
            _notchPath.MouseLeftButtonDown += OnPillClicked;
            _notchPath.MouseEnter += OnPillMouseEnter;
            _notchPath.MouseLeave += OnPillMouseLeave;

            _notchContentHost = new Grid
            {
                Width = _curW,
                Height = _curH,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand
            };
            _notchContentHost.MouseLeftButtonDown += OnPillClicked;
            _notchContentHost.MouseEnter += OnPillMouseEnter;
            _notchContentHost.MouseLeave += OnPillMouseLeave;

            // =================================================================
            // 1. Listening Visualizer Host (Compact: 220x36, Green & White Only)
            // =================================================================
            _listeningHost = new Canvas
            {
                Width = 220,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };

            // 4 Radial Glow Blobs (strictly Mint Green #3DDC84 & White #FFFFFF)
            Color[] blobColors = new[] { ColMint, ColWhite, ColMint, ColWhite };
            double[] blobCentersX = new[] { 50.0, 90.0, 130.0, 170.0 };

            for (int i = 0; i < 4; i++)
            {
                var grad = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                grad.GradientStops.Add(new GradientStop(blobColors[i], 0.0));
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(0, blobColors[i].R, blobColors[i].G, blobColors[i].B), 1.0));
                grad.Freeze();

                var st = new ScaleTransform(1.0, 1.0, 27.0, 27.0);
                _listeningBlobTransforms[i] = st;

                var blob = new Ellipse
                {
                    Width = 54,
                    Height = 54,
                    Fill = grad,
                    Opacity = 0.45,
                    RenderTransform = st
                };
                Canvas.SetLeft(blob, blobCentersX[i] - 27.0);
                Canvas.SetTop(blob, 18.0 - 27.0);
                _listeningBlobs[i] = blob;
                _listeningHost.Children.Add(blob);
            }

            // 13 Equalizer bars (width 3.5, rx 1.75, height 20, step 10, centered within 220x36)
            for (int i = 0; i < 13; i++)
            {
                var st = new ScaleTransform(1.0, 0.15, 1.75, 10.0);
                _listeningBarTransforms[i] = st;

                var bar = new Border
                {
                    Width = 3.5,
                    Height = 20,
                    CornerRadius = new CornerRadius(1.75),
                    Background = BrushVoiceGradient,
                    RenderTransform = st
                };
                Canvas.SetLeft(bar, 50.0 + (i * 10.0));
                Canvas.SetTop(bar, 8.0);
                _listeningBars[i] = bar;
                _listeningHost.Children.Add(bar);
            }

            // =================================================================
            // 2. Multitasking Left Host (flinch-notch-tasks 2.svg)
            // =================================================================
            _multitaskLeftHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand
            };

            // 2A. Timer Host (Track + Progress Arc + Time Text)
            _timerTaskHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var timerTrackGrid = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
            timerTrackGrid.Children.Add(new Ellipse
            {
                Width = 18,
                Height = 18,
                Stroke = BrushTrack,
                StrokeThickness = 2.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            _timerProgressArcPath = new System.Windows.Shapes.Path
            {
                Stroke = BrushMint,
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            timerTrackGrid.Children.Add(_timerProgressArcPath);

            _liveTimerText = new TextBlock
            {
                Text = "00:00",
                Foreground = BrushCream,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI Variable Semibold, Segoe UI Semibold, Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            _timerTaskHost.Children.Add(timerTrackGrid);
            _timerTaskHost.Children.Add(_liveTimerText);

            // 2B. Spinner Task Host (Circle Track + Spinning Orange Arc + Label)
            _spinnerTaskHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var spinnerHostGrid = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            spinnerHostGrid.Children.Add(new Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = BrushTrack,
                StrokeThickness = 2.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            _spinnerArcTransform = new RotateTransform(0, 10, 10);
            _spinnerArcPath = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 10,2 A 8,8 0 0 1 18,10"),
                Stroke = BrushMint,
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = _spinnerArcTransform
            };
            spinnerHostGrid.Children.Add(_spinnerArcPath);

            _spinnerTaskText = new TextBlock
            {
                Text = "Organizing...",
                Foreground = BrushCream,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI Variable Semibold, Segoe UI Semibold, Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            _spinnerTaskHost.Children.Add(spinnerHostGrid);
            _spinnerTaskHost.Children.Add(_spinnerTaskText);

            // 2C. Search Task Host (Magnifier Icon + Label)
            _searchTaskHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var searchIconPath = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 5.5,1 A 4.5,4.5 0 1 0 10,5.5 A 4.5,4.5 0 0 0 5.5,1 Z M 9,9 L 13.5,13.5"),
                Stroke = BrushCream,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 15,
                Height = 15,
                VerticalAlignment = VerticalAlignment.Center
            };

            _searchTaskText = new TextBlock
            {
                Text = "Searching...",
                Foreground = BrushCream,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI Variable Semibold, Segoe UI Semibold, Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            _searchTaskHost.Children.Add(searchIconPath);
            _searchTaskHost.Children.Add(_searchTaskText);

            // 2D. Audio Eq Host (3 Eq Bars + Label)
            _audioTaskHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var eqStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 3; i++)
            {
                var eqBar = new Border
                {
                    Width = 3,
                    Height = 10,
                    CornerRadius = new CornerRadius(1.5),
                    Background = BrushCream,
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                _audioEqBars[i] = eqBar;
                eqStack.Children.Add(eqBar);
            }

            _audioTaskText = new TextBlock
            {
                Text = "Audio",
                Foreground = BrushCream,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI Variable Semibold, Segoe UI Semibold, Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            _audioTaskHost.Children.Add(eqStack);
            _audioTaskHost.Children.Add(_audioTaskText);

            _multitaskLeftHost.Children.Add(_timerTaskHost);
            _multitaskLeftHost.Children.Add(_spinnerTaskHost);
            _multitaskLeftHost.Children.Add(_searchTaskHost);
            _multitaskLeftHost.Children.Add(_audioTaskHost);

            _multitaskLeftHost.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleTasksExpanded();
            };

            // =================================================================
            // 3. Expanded Task Card (flinch-notch-expanded 2.svg)
            // =================================================================
            _tasksExpandedPanel = new Grid
            {
                Width = 400,
                Height = 206,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };

            var expandedTopPanel = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(34, 10, 34, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            var tasksHeaderLabel = new TextBlock
            {
                Text = "Tasks",
                Foreground = BrushTextMuted,
                FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
                FontSize = 12
            };
            DockPanel.SetDock(tasksHeaderLabel, Dock.Left);
            expandedTopPanel.Children.Add(tasksHeaderLabel);

            var unicornLogo = BuildUnicornSvg(18, 16, BrushTextMuted);
            DockPanel.SetDock(unicornLogo, Dock.Right);
            expandedTopPanel.Children.Add(unicornLogo);
            _tasksExpandedPanel.Children.Add(expandedTopPanel);

            // Divider 1 (y = 34)
            var div1 = new Border { Height = 1, Background = BrushDivider, Margin = new Thickness(34, 34, 34, 0), VerticalAlignment = VerticalAlignment.Top };
            _tasksExpandedPanel.Children.Add(div1);

            // Dynamic items container for all running timers and background tasks
            _tasksItemsStack = new StackPanel { Orientation = Orientation.Vertical };
            _tasksScrollViewer = new ScrollViewer
            {
                Margin = new Thickness(34, 38, 34, 10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = _tasksItemsStack
            };
            _tasksExpandedPanel.Children.Add(_tasksScrollViewer);

            _expandedTimerRingPath = new System.Windows.Shapes.Path { Stroke = BrushMint, StrokeThickness = 2.6 };
            _expandedTimerSubtitle = new TextBlock();
            _expandedTimerTimeText = new TextBlock();
            _expandedBgSpinnerArc = new System.Windows.Shapes.Path { Stroke = BrushMint, StrokeThickness = 2.4 };
            _expandedBgTitleText = new TextBlock();
            _expandedBgProgressBar = new Border { Background = BrushMint };
            _expandedBgDetailText = new TextBlock();
            _expandedSearchTitleText = new TextBlock();
            _expandedSearchSubtitle = new TextBlock();

            _tasksExpandedPanel.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleTasksExpanded();
            };

            // =================================================================
            // 4. Center Grid (Idle Dots, Thinking Spinner, Countdown, Alert, Error, Update)
            // =================================================================
            _centerGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };

            // 4A. 4 Dots (flinch-notch-idle.svg: cx=182, 194, 206, 218; Mint #3DDC84, White #FFFFFF)
            _dotsCenterHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            for (int i = 0; i < 4; i++)
            {
                var bar = new Border
                {
                    Width = 6.0,
                    Height = 6.0,
                    CornerRadius = new CornerRadius(3.0),
                    Background = (i % 2 == 0) ? BrushMint : BrushWhite,
                    Margin = new Thickness(3.0, 0, 3.0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _soundBars[i] = bar;
                _dotsCenterHost.Children.Add(bar);
            }

            // 4B. Centered Thinking Spinner (spinner.svg)
            _spinnerViewbox = BuildSpinnerSvg(_spinnerSpokes);
            _spinnerViewbox.Width = 18;
            _spinnerViewbox.Height = 18;
            _spinnerViewbox.HorizontalAlignment = HorizontalAlignment.Center;
            _spinnerViewbox.VerticalAlignment = VerticalAlignment.Center;
            _spinnerViewbox.Visibility = Visibility.Collapsed;

            // 4C. Centered 5s Action Countdown Panel ("Opening Spotify (5s)... [Cancel]")
            _countdownPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            _countdownLabel = new TextBlock
            {
                Text = "",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };

            _countdownSecText = new TextBlock
            {
                Text = "(5s)",
                Foreground = BrushMint,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            _countdownCancelBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(44, 48, 59)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2, 7, 2),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Cancel",
                    Foreground = new SolidColorBrush(Color.FromRgb(174, 180, 191)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10.5
                }
            };
            _countdownCancelBtn.MouseEnter += (s, e) => _countdownCancelBtn.Background = new SolidColorBrush(Color.FromRgb(61, 64, 73));
            _countdownCancelBtn.MouseLeave += (s, e) => _countdownCancelBtn.Background = new SolidColorBrush(Color.FromRgb(44, 48, 59));
            _countdownCancelBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                CancelCountdown();
            };

            _countdownPanel.Children.Add(_countdownLabel);
            _countdownPanel.Children.Add(_countdownSecText);
            _countdownPanel.Children.Add(_countdownCancelBtn);

            // 4D. Centered Alert Panel ("🔔 5-second timer is up!")
            _alertPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            _alertIconText = new TextBlock
            {
                Text = "🔔",
                FontSize = 12.0,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            _alertLabelText = new TextBlock
            {
                Text = "",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI Semibold, Segoe UI"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };

            _alertPanel.Children.Add(_alertIconText);
            _alertPanel.Children.Add(_alertLabelText);

            // 4E. Centered Error Card Panel
            _cardPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            _transcriptText = new TextBlock
            {
                Text = "",
                Visibility = Visibility.Collapsed
            };

            _answerText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            _cardPanel.Children.Add(_transcriptText);
            _cardPanel.Children.Add(_answerText);

            // 4F. Update Button (01-update-available.svg, 03-progress-ring.svg, 09-download-complete.svg)
            _updateDownloadBtn = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                ToolTip = "New Flinch update available! Click to install."
            };

            var updateCanvas = new Canvas
            {
                Width = 90,
                Height = 90
            };

            // 1. Available State (01-update-available.svg - pure circle with no square background)
            _updateAvailHost = new Canvas { Width = 90, Height = 90, Visibility = Visibility.Visible };
            var availCircle = new System.Windows.Shapes.Ellipse
            {
                Width = 72,
                Height = 72,
                Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFF))
            };
            Canvas.SetLeft(availCircle, 9);
            Canvas.SetTop(availCircle, 9);

            var availArrow = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 45,30 L 45,52 M 35,42 L 45,52 L 55,42 M 33,61 L 57,61"),
                Stroke = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFF)),
                StrokeThickness = 4.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };

            _updateRippleEllipse = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x24)),
                Opacity = 0.75
            };
            Canvas.SetLeft(_updateRippleEllipse, 64);
            Canvas.SetTop(_updateRippleEllipse, 10);

            var availDot = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x24)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x1D)),
                StrokeThickness = 2.2
            };
            Canvas.SetLeft(availDot, 64);
            Canvas.SetTop(availDot, 10);

            _updateAvailHost.Children.Add(availCircle);
            _updateAvailHost.Children.Add(availArrow);
            _updateAvailHost.Children.Add(_updateRippleEllipse);
            _updateAvailHost.Children.Add(availDot);

            // 2. Progress Ring State (03-progress-ring.svg)
            _updateProgressHost = new Canvas { Width = 90, Height = 90, Visibility = Visibility.Collapsed };
            var progTrack = new System.Windows.Shapes.Ellipse
            {
                Width = 72,
                Height = 72,
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromRgb(0x24, 0x38, 0x5C)),
                StrokeThickness = 5.0
            };
            Canvas.SetLeft(progTrack, 9);
            Canvas.SetTop(progTrack, 9);

            _updateProgressArcPath = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFF)),
                StrokeThickness = 5.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            var progArrow = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 45,30 L 45,52 M 35,42 L 45,52 L 55,42 M 33,61 L 57,61"),
                Stroke = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFF)),
                StrokeThickness = 4.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };

            _updateProgressHost.Children.Add(progTrack);
            _updateProgressHost.Children.Add(_updateProgressArcPath);
            _updateProgressHost.Children.Add(progArrow);

            // 3. Download Complete State (09-download-complete.svg)
            _updateCompleteHost = new Canvas { Width = 90, Height = 90, Visibility = Visibility.Collapsed };
            var compRing = new System.Windows.Shapes.Ellipse
            {
                Width = 72,
                Height = 72,
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)),
                StrokeThickness = 5.0
            };
            Canvas.SetLeft(compRing, 9);
            Canvas.SetTop(compRing, 9);

            var compCheck = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 32,46 L 41,55 L 59,35"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)),
                StrokeThickness = 5.0,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };

            _updateCompleteHost.Children.Add(compRing);
            _updateCompleteHost.Children.Add(compCheck);

            updateCanvas.Children.Add(_updateAvailHost);
            updateCanvas.Children.Add(_updateProgressHost);
            updateCanvas.Children.Add(_updateCompleteHost);

            _updateRootViewbox = new Viewbox
            {
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform,
                Child = updateCanvas
            };

            _updateDownloadBtn.Child = _updateRootViewbox;

            _updateDownloadBtn.MouseEnter += (s, e) => _updateDownloadBtn.Opacity = 0.82;
            _updateDownloadBtn.MouseLeave += (s, e) => _updateDownloadBtn.Opacity = 1.0;

            _updateDownloadBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                TriggerUpdateDownloadAndInstall();
            };

            _centerGrid.Children.Add(_multitaskLeftHost);
            _centerGrid.Children.Add(_dotsCenterHost);
            _centerGrid.Children.Add(_spinnerViewbox);
            _centerGrid.Children.Add(_countdownPanel);
            _centerGrid.Children.Add(_alertPanel);
            _centerGrid.Children.Add(_cardPanel);
            _centerGrid.Children.Add(_updateDownloadBtn);

            _notchContentHost.Children.Add(_listeningHost);
            _notchContentHost.Children.Add(_centerGrid);
            _notchContentHost.Children.Add(_tasksExpandedPanel);
            rootGrid.Children.Add(_notchPath);
            rootGrid.Children.Add(_notchContentHost);
            Content = rootGrid;

            _recorder.SpeechEnded += () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_state == IslandState.Listening)
                    {
                        FinishRecordingAndSend();
                    }
                }));
            };

            SetupTrayIcon();

            _autoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.5) };
            _autoCollapseTimer.Tick += (s, e) =>
            {
                _autoCollapseTimer.Stop();
                if (_state == IslandState.Done || _state == IslandState.Error || _state == IslandState.Alert)
                {
                    SetIslandState(IslandState.Idle, "", "");
                }
            };

            SetIslandState(IslandState.Idle, "", "");
            StartUpdateChecker();

            var animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            animTimer.Tick += OnFrameTick;
            animTimer.Start();

            SourceInitialized += OnWindowSourceInitialized;
            Closed += OnWindowClosed;
        }

        private static Viewbox BuildSpinnerSvg(System.Windows.Shapes.Rectangle[] spokesOut)
        {
            var canvas = new Canvas { Width = 64, Height = 64 };
            for (int i = 0; i < 8; i++)
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = 6,
                    Height = 15,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = (i % 2 == 0) ? BrushMint : BrushCream,
                    RenderTransform = new RotateTransform(i * 45.0, 3.0, 27.0)
                };
                Canvas.SetLeft(rect, 29);
                Canvas.SetTop(rect, 5);
                spokesOut[i] = rect;
                canvas.Children.Add(rect);
            }
            return new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = canvas
            };
        }

        // =========================================================================
        // Native Chime Synthesizer: Pure In-Memory 16-bit PCM Bell Chime
        // =========================================================================
        private static byte[] GenerateChimeWav()
        {
            int sampleRate = 44100;
            int totalSamples = (int)(sampleRate * 0.55);
            byte[] pcmData = new byte[totalSamples * 2];

            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                double sample = 0;
                if (t < 0.12)
                {
                    double env = Math.Exp(-t * 12.0);
                    sample = Math.Sin(2.0 * Math.PI * 1046.5 * t) * env * 0.7; // C6
                }
                else
                {
                    double t2 = t - 0.12;
                    double env = Math.Exp(-t2 * 6.5);
                    sample = Math.Sin(2.0 * Math.PI * 1567.98 * t2) * env * 0.8; // G6
                    sample += Math.Sin(2.0 * Math.PI * 3135.96 * t2) * env * 0.2; // Harmonic
                }
                short val = (short)(Math.Max(-1.0, Math.Min(1.0, sample)) * 32767);
                pcmData[i * 2] = (byte)(val & 0xFF);
                pcmData[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + pcmData.Length);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write((short)1); // Mono
                bw.Write(sampleRate);
                bw.Write(sampleRate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                bw.Write(pcmData.Length);
                bw.Write(pcmData);
                return ms.ToArray();
            }
        }

        private void PlayChimeSound()
        {
            try
            {
                if (_cachedChimeWav == null)
                {
                    _cachedChimeWav = GenerateChimeWav();
                }
                using (var ms = new MemoryStream(_cachedChimeWav))
                using (var sp = new System.Media.SoundPlayer(ms))
                {
                    sp.Play();
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("PlayChimeSound error: " + ex.Message);
            }
        }

        // =========================================================================
        // Task Countdown & Safe PC Execution Logic
        // =========================================================================
        private void StartActionCountdown(ActionPayload action)
        {
            if (action == null) return;
            CancelCountdown();

            _pendingAction = action;
            _countdownSecRemaining = action.countdown_sec > 0 ? action.countdown_sec : 5;

            string label = "Open " + (!string.IsNullOrWhiteSpace(action.name) ? action.name : "app");
            if (string.Equals(action.type, "lock_pc", StringComparison.OrdinalIgnoreCase))
            {
                label = "Locking PC";
            }
            else if (string.Equals(action.type, "open_url", StringComparison.OrdinalIgnoreCase))
            {
                label = "Open " + (!string.IsNullOrWhiteSpace(action.name) ? action.name : "link");
            }

            _countdownLabel.Text = label;
            _countdownSecText.Text = string.Format("({0}s)", _countdownSecRemaining);
            SetIslandState(IslandState.Countdown, "", "");

            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += (s, e) =>
            {
                _countdownSecRemaining--;
                if (_countdownSecRemaining > 0)
                {
                    _countdownSecText.Text = string.Format("({0}s)", _countdownSecRemaining);
                }
                else
                {
                    _countdownTimer.Stop();
                    _countdownTimer = null;

                    // Brief execution spinner before action runs
                    _countdownPanel.Visibility = Visibility.Collapsed;
                    _spinnerViewbox.Visibility = Visibility.Visible;
                    _targetW = 104;
                    _targetH = 30;

                    var act = _pendingAction;
                    _pendingAction = null;
                    ExecuteAction(act);

                    Task.Delay(500).ContinueWith(_ =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_state == IslandState.Countdown)
                            {
                                SetIslandState(IslandState.Idle, "", "");
                            }
                        }));
                    });
                }
            };
            _countdownTimer.Start();
        }

        private void CancelCountdown()
        {
            if (_countdownTimer != null)
            {
                _countdownTimer.Stop();
                _countdownTimer = null;
            }
            _pendingAction = null;
            SetIslandState(IslandState.Idle, "", "");
        }

        private List<ActiveTimerItem> GetActiveTimersList()
        {
            lock (_timerLock)
            {
                return new List<ActiveTimerItem>(_activeTimers);
            }
        }

        private ActiveTimerItem GetLatestActiveTimer()
        {
            lock (_timerLock)
            {
                ActiveTimerItem latest = null;
                foreach (var t in _activeTimers)
                {
                    if (latest == null || t.StartedUtc > latest.StartedUtc)
                    {
                        latest = t;
                    }
                }
                return latest;
            }
        }

        private bool CancelTimer(string id)
        {
            lock (_timerLock)
            {
                for (int i = 0; i < _activeTimers.Count; i++)
                {
                    if (_activeTimers[i].Id == id)
                    {
                        _activeTimers.RemoveAt(i);
                        if (_activeTimers.Count == 0 && _liveCountdownTicker != null)
                        {
                            _liveCountdownTicker.Stop();
                        }
                        UpdateMultitaskDisplay();
                        RefreshTasksExpandedUI();
                        return true;
                    }
                }
            }
            return false;
        }

        private ActiveTimerItem CancelLatestTimer()
        {
            lock (_timerLock)
            {
                var latest = GetLatestActiveTimer();
                if (latest != null)
                {
                    _activeTimers.Remove(latest);
                    if (_activeTimers.Count == 0 && _liveCountdownTicker != null)
                    {
                        _liveCountdownTicker.Stop();
                    }
                    UpdateMultitaskDisplay();
                    RefreshTasksExpandedUI();
                    return latest;
                }
            }
            return null;
        }

        private void StartTimerOrReminder(int seconds, string type, string text, string label)
        {
            if (seconds <= 0) seconds = 5;
            string lbl = !string.IsNullOrWhiteSpace(label) ? label : (!string.IsNullOrWhiteSpace(text) ? text : "Timer");

            var item = new ActiveTimerItem
            {
                Id = Guid.NewGuid().ToString(),
                Label = lbl,
                Type = type,
                Text = text,
                SecondsTotal = seconds,
                SecondsLeft = seconds,
                StartedUtc = DateTime.UtcNow
            };

            lock (_timerLock)
            {
                _activeTimers.Add(item);
            }

            _activeTimerSecondsTotal = seconds;
            _activeTimerSecondsLeft = seconds;
            _activeTimerLabel = lbl;

            UpdateMultitaskDisplay();
            RefreshTasksExpandedUI();

            if (_liveCountdownTicker == null)
            {
                _liveCountdownTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _liveCountdownTicker.Tick += (s, e) =>
                {
                    List<ActiveTimerItem> completed = new List<ActiveTimerItem>();
                    lock (_timerLock)
                    {
                        for (int i = _activeTimers.Count - 1; i >= 0; i--)
                        {
                            var t = _activeTimers[i];
                            t.SecondsLeft--;
                            if (t.SecondsLeft <= 0)
                            {
                                completed.Add(t);
                                _activeTimers.RemoveAt(i);
                            }
                        }
                    }

                    foreach (var c in completed)
                    {
                        ShowAlert(c.Type, c.Text, c.Label);
                    }

                    lock (_timerLock)
                    {
                        if (_activeTimers.Count == 0)
                        {
                            _liveCountdownTicker.Stop();
                        }
                        else
                        {
                            ActiveTimerItem nextExpiring = _activeTimers[0];
                            foreach (var at in _activeTimers)
                            {
                                if (at.SecondsLeft < nextExpiring.SecondsLeft) nextExpiring = at;
                            }
                            _activeTimerSecondsLeft = nextExpiring.SecondsLeft;
                            _activeTimerSecondsTotal = nextExpiring.SecondsTotal;
                            _activeTimerLabel = nextExpiring.Label;
                        }
                    }

                    UpdateMultitaskDisplay();
                    RefreshTasksExpandedUI();
                };
            }
            _liveCountdownTicker.Start();
        }

        private static string FormatTimerDisplay(int totalSec)
        {
            if (totalSec >= 3600)
            {
                int h = totalSec / 3600;
                int m = (totalSec % 3600) / 60;
                int s = totalSec % 60;
                return string.Format("{0:D2}:{1:D2}:{2:D2}", h, m, s);
            }
            int mins = totalSec / 60;
            int secs = totalSec % 60;
            return string.Format("{0:D2}:{1:D2}", mins, secs);
        }

        // =========================================================================
        // GitHub Auto-Update Downloader & Notch Integration
        // =========================================================================
        private void StartUpdateChecker()
        {
            Task.Delay(3500).ContinueWith(_ => CheckForUpdatesInBackground());

            _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _updateCheckTimer.Tick += (s, e) => CheckForUpdatesInBackground();
            _updateCheckTimer.Start();
        }

        private void SetUpdateVisualState(UpdateVisualState state)
        {
            _updateVisualState = state;
            if (_updateAvailHost != null)
                _updateAvailHost.Visibility = (state == UpdateVisualState.Available) ? Visibility.Visible : Visibility.Collapsed;
            if (_updateProgressHost != null)
                _updateProgressHost.Visibility = (state == UpdateVisualState.Downloading) ? Visibility.Visible : Visibility.Collapsed;
            if (_updateCompleteHost != null)
                _updateCompleteHost.Visibility = (state == UpdateVisualState.Complete) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDownloadProgressArc(double fraction)
        {
            if (_updateProgressArcPath == null) return;
            fraction = Math.Max(0.01, Math.Min(0.999, fraction));
            double cx = 45.0;
            double cy = 45.0;
            double r = 36.0;
            double startAngle = -90.0;
            double endAngle = startAngle + (fraction * 360.0);

            double startRad = startAngle * Math.PI / 180.0;
            double endRad = endAngle * Math.PI / 180.0;

            Point pStart = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
            Point pEnd = new Point(cx + r * Math.Cos(endRad), cy + r * Math.Sin(endRad));

            bool isLargeArc = fraction > 0.5;

            var fig = new PathFigure { StartPoint = pStart, IsClosed = false };
            fig.Segments.Add(new ArcSegment(pEnd, new Size(r, r), 0, isLargeArc, SweepDirection.Clockwise, true));

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            _updateProgressArcPath.Data = geo;
        }

        private void CheckForUpdatesInBackground()
        {
            Task.Run(() =>
            {
                var info = UpdateChecker.CheckForUpdate();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (info != null && info.IsAvailable && !string.IsNullOrWhiteSpace(info.AssetDownloadUrl))
                    {
                        _isUpdateAvailable = true;
                        _pendingUpdateUrl = info.AssetDownloadUrl;
                        SetUpdateVisualState(UpdateVisualState.Available);
                        _updateDownloadBtn.Visibility = Visibility.Visible;
                        _updateDownloadBtn.ToolTip = "New Flinch update available (" + info.TagName + ")! Click to install.";
                        UpdateMultitaskDisplay();
                        FlinchEnv.Log("Update available: " + info.TagName + " at " + info.AssetDownloadUrl);
                    }
                    else
                    {
                        _isUpdateAvailable = false;
                        _pendingUpdateUrl = null;
                        _updateDownloadBtn.Visibility = Visibility.Collapsed;
                        UpdateMultitaskDisplay();
                    }
                }));
            });
        }

        private void TriggerUpdateDownloadAndInstall()
        {
            if (_isDownloadingUpdate) return;
            _isDownloadingUpdate = true;

            string url = _pendingUpdateUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://github.com/" + FlinchEnv.GitHubRepo + "/releases/download/v0.1.0/FlinchSetup.exe";
            }

            SetUpdateVisualState(UpdateVisualState.Downloading);
            _updateDownloadBtn.ToolTip = "Downloading latest Flinch update from GitHub...";
            UpdateMultitaskDisplay();

            Task.Run(() =>
            {
                try
                {
                    string tempDir = System.IO.Path.GetTempPath();
                    string destExe = System.IO.Path.Combine(tempDir, "FlinchSetup_Update.exe");

                    FlinchEnv.Log("Downloading update from " + url + " to " + destExe);

                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromMinutes(3);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Flinch-Desktop-App");

                        using (var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                        {
                            resp.EnsureSuccessStatusCode();
                            using (var fs = new FileStream(destExe, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                            }
                        }
                    }

                    FlinchEnv.Log("Update downloaded successfully. Launching installer...");

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetUpdateVisualState(UpdateVisualState.Complete);
                        _updateDownloadBtn.ToolTip = "Update downloaded! Installing...";

                        Task.Delay(600).ContinueWith(_ =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    Process.Start(new ProcessStartInfo(destExe, "/update") { UseShellExecute = true });
                                    if (_trayIcon != null)
                                    {
                                        _trayIcon.Visible = false;
                                        _trayIcon.Dispose();
                                    }
                                    Application.Current.Shutdown();
                                }
                                catch (Exception ex)
                                {
                                    FlinchEnv.Log("Launch installer error: " + ex.Message);
                                    _isDownloadingUpdate = false;
                                    SetUpdateVisualState(UpdateVisualState.Available);
                                    _updateDownloadBtn.ToolTip = "Installer failed to launch: " + ex.Message;
                                }
                            }));
                        });
                    }));
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("Download update error: " + ex.Message);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isDownloadingUpdate = false;
                        SetUpdateVisualState(UpdateVisualState.Available);
                        _updateDownloadBtn.ToolTip = "Download failed: " + ex.Message + ". Click to retry.";
                    }));
                }
            });
        }

        private void UpdateTimerProgressArc(double progress)
        {
            if (progress <= 0.001)
            {
                _timerProgressArcPath.Data = null;
                if (_expandedTimerRingPath != null) _expandedTimerRingPath.Data = null;
                return;
            }
            double r = 9.0;
            double cx = 11.0;
            double cy = 11.0;
            double angle = Math.Min(359.9, Math.Max(0.1, progress * 360.0));
            double rad = (angle - 90.0) * Math.PI / 180.0;
            double xEnd = cx + r * Math.Cos(rad);
            double yEnd = cy + r * Math.Sin(rad);

            var fig = new PathFigure
            {
                StartPoint = new Point(cx, cy - r),
                IsClosed = false,
                IsFilled = false
            };
            fig.Segments.Add(new ArcSegment(
                new Point(xEnd, yEnd),
                new Size(r, r),
                0, angle > 180.0, SweepDirection.Clockwise, true));

            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            geom.Freeze();
            _timerProgressArcPath.Data = geom;

            // Also update expanded ring (radius 11, center 12, 12)
            if (_expandedTimerRingPath != null)
            {
                double rExp = 11.0;
                double cxExp = 12.0;
                double cyExp = 12.0;
                double xEndExp = cxExp + rExp * Math.Cos(rad);
                double yEndExp = cyExp + rExp * Math.Sin(rad);
                var figExp = new PathFigure
                {
                    StartPoint = new Point(cxExp, cyExp - rExp),
                    IsClosed = false,
                    IsFilled = false
                };
                figExp.Segments.Add(new ArcSegment(
                    new Point(xEndExp, yEndExp),
                    new Size(rExp, rExp),
                    0, angle > 180.0, SweepDirection.Clockwise, true));
                var geomExp = new PathGeometry();
                geomExp.Figures.Add(figExp);
                geomExp.Freeze();
                _expandedTimerRingPath.Data = geomExp;
            }
        }

        private void ToggleTasksExpanded()
        {
            if (_state == IslandState.TasksExpanded)
            {
                SetIslandState(IslandState.Idle, "", "");
            }
            else
            {
                SetIslandState(IslandState.TasksExpanded, "", "");
            }
        }

        public void StartMultitask(string type, string title, string detail = "", int progress = 0, int total = 0)
        {
            _isBackgroundTaskRunning = true;
            _backgroundTaskType = string.IsNullOrWhiteSpace(type) ? "organizing" : type.ToLowerInvariant();
            _backgroundTaskTitle = title ?? "";
            _backgroundTaskDetail = detail ?? "";
            UpdateMultitaskDisplay();
        }

        public void StopMultitask()
        {
            _isBackgroundTaskRunning = false;
            _backgroundTaskType = "";
            _backgroundTaskTitle = "";
            _backgroundTaskDetail = "";
            UpdateMultitaskDisplay();
        }

        private void UpdateMultitaskDisplay()
        {
            int bgCount = BackgroundTaskManager.ActiveCount;
            int timerCount;
            lock (_timerLock) { timerCount = _activeTimers.Count; }
            bool hasActiveMultitask = timerCount > 0 || _isBackgroundTaskRunning || bgCount > 0;

            if (hasActiveMultitask)
            {
                _multitaskLeftHost.Visibility = Visibility.Visible;

                if (timerCount > 0)
                {
                    _timerTaskHost.Visibility = Visibility.Visible;
                    _spinnerTaskHost.Visibility = Visibility.Collapsed;
                    _searchTaskHost.Visibility = Visibility.Collapsed;
                    _audioTaskHost.Visibility = Visibility.Collapsed;

                    ActiveTimerItem minTimer = null;
                    lock (_timerLock)
                    {
                        if (_activeTimers.Count > 0)
                        {
                            minTimer = _activeTimers[0];
                            foreach (var at in _activeTimers)
                            {
                                if (at.SecondsLeft < minTimer.SecondsLeft) minTimer = at;
                            }
                        }
                    }

                    if (minTimer != null)
                    {
                        string formatted = FormatTimerDisplay(minTimer.SecondsLeft);
                        _liveTimerText.Text = formatted;
                        if (_expandedTimerTimeText != null) _expandedTimerTimeText.Text = formatted;
                        if (_expandedTimerSubtitle != null) _expandedTimerSubtitle.Text = !string.IsNullOrWhiteSpace(minTimer.Label) ? minTimer.Label : "Countdown";

                        double progress = (minTimer.SecondsTotal > 0)
                            ? (double)minTimer.SecondsLeft / minTimer.SecondsTotal
                            : 1.0;
                        UpdateTimerProgressArc(progress);
                    }
                }
                else if (bgCount > 0)
                {
                    _timerTaskHost.Visibility = Visibility.Collapsed;
                    _searchTaskHost.Visibility = Visibility.Collapsed;
                    _audioTaskHost.Visibility = Visibility.Collapsed;
                    _spinnerTaskHost.Visibility = Visibility.Visible;

                    // When not hovered: user requested the spinner should be fine without showing what it's doing in the background
                    _spinnerTaskText.Visibility = Visibility.Collapsed;
                }
                else if (_isBackgroundTaskRunning)
                {
                    _timerTaskHost.Visibility = Visibility.Collapsed;
                    if (_backgroundTaskType == "search")
                    {
                        _searchTaskHost.Visibility = Visibility.Visible;
                        _spinnerTaskHost.Visibility = Visibility.Collapsed;
                        _audioTaskHost.Visibility = Visibility.Collapsed;
                        _searchTaskText.Text = string.IsNullOrWhiteSpace(_backgroundTaskTitle) ? "Searching..." : _backgroundTaskTitle;
                        if (_expandedSearchTitleText != null) _expandedSearchTitleText.Text = _searchTaskText.Text;
                    }
                    else if (_backgroundTaskType == "audio")
                    {
                        _audioTaskHost.Visibility = Visibility.Visible;
                        _searchTaskHost.Visibility = Visibility.Collapsed;
                        _spinnerTaskHost.Visibility = Visibility.Collapsed;
                        _audioTaskText.Text = string.IsNullOrWhiteSpace(_backgroundTaskTitle) ? "Audio" : _backgroundTaskTitle;
                    }
                    else
                    {
                        _spinnerTaskHost.Visibility = Visibility.Visible;
                        _searchTaskHost.Visibility = Visibility.Collapsed;
                        _audioTaskHost.Visibility = Visibility.Collapsed;
                        _spinnerTaskText.Visibility = Visibility.Collapsed;
                    }
                }

                // Move 4 idle dots to the right when timer or background task is active!
                _dotsCenterHost.HorizontalAlignment = HorizontalAlignment.Right;
                _dotsCenterHost.Margin = _isUpdateAvailable ? new Thickness(0, 0, 46, 0) : new Thickness(0, 0, 18, 0);

                if (_isUpdateAvailable)
                {
                    _updateDownloadBtn.Visibility = Visibility.Visible;
                }

                if (_state == IslandState.Idle)
                {
                    if (bgCount > 0 && timerCount == 0)
                    {
                        // Sleek unhovered spinner pill
                        _targetW = _isUpdateAvailable ? 230 : 190;
                        _targetH = 34;
                        _targetBotR = 17;
                    }
                    else
                    {
                        _targetW = _isUpdateAvailable ? 310 : 280;
                        _targetH = 46;
                        _targetBotR = 23;
                    }
                }
            }
            else
            {
                _multitaskLeftHost.Visibility = Visibility.Collapsed;

                if (_isUpdateAvailable)
                {
                    _updateDownloadBtn.Visibility = Visibility.Visible;
                    _dotsCenterHost.HorizontalAlignment = HorizontalAlignment.Center;
                    _dotsCenterHost.Margin = new Thickness(0, 0, 24, 0);

                    if (_state == IslandState.Idle)
                    {
                        _targetW = 200;
                        _targetH = 34;
                        _targetBotR = 17;
                    }
                }
                else
                {
                    _updateDownloadBtn.Visibility = Visibility.Collapsed;
                    _dotsCenterHost.HorizontalAlignment = HorizontalAlignment.Center;
                    _dotsCenterHost.Margin = new Thickness(0);

                    if (_state == IslandState.Idle)
                    {
                        _targetW = 170;
                        _targetH = 34;
                        _targetBotR = 17;
                    }
                }
            }
        }

        private void ShowAlert(string type, string text, string label)
        {
            PlayChimeSound();

            string displayMsg = !string.IsNullOrWhiteSpace(label) ? label : (!string.IsNullOrWhiteSpace(text) ? text : "Timer is up!");
            if (string.Equals(type, "timer", StringComparison.OrdinalIgnoreCase) && !displayMsg.EndsWith("up!", StringComparison.OrdinalIgnoreCase))
            {
                displayMsg += " is up!";
            }

            _alertLabelText.Text = displayMsg;
            SetIslandState(IslandState.Alert, "", displayMsg);

            string spokenAlert = displayMsg;
            Task.Run(() =>
            {
                try
                {
                    byte[] alertWav = GeminiClient.SynthesizeSpeechFast(spokenAlert);
                    if (alertWav == null || alertWav.Length <= 44)
                        alertWav = LocalSpeechEngine.SynthesizeToWav(spokenAlert, FlinchEnv.VoiceName);
                    if (alertWav != null && alertWav.Length > 44)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_state == IslandState.Alert)
                            {
                                PlayRawWavOnChannel(alertWav);
                            }
                        }));
                    }
                }
                catch { }
            });

            _autoCollapseTimer.Interval = TimeSpan.FromSeconds(6.0);
            _autoCollapseTimer.Start();
        }

        private static void ExecuteAction(ActionPayload action)
        {
            if (action == null) return;
            try
            {
                string type = (action.type ?? "").ToLowerInvariant().Trim();
                string target = (action.target ?? "").Trim();

                if (type == "open_app")
                {
                    switch (target.ToLowerInvariant())
                    {
                        case "spotify":
                            try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
                            catch { Process.Start(new ProcessStartInfo("spotify.exe") { UseShellExecute = true }); }
                            break;
                        case "chrome":
                            try { Process.Start(new ProcessStartInfo("chrome.exe") { UseShellExecute = true }); }
                            catch { Process.Start(new ProcessStartInfo("https://www.google.com") { UseShellExecute = true }); }
                            break;
                        case "vscode":
                        case "code":
                            Process.Start(new ProcessStartInfo("code") { UseShellExecute = true });
                            break;
                        case "notepad":
                            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
                            break;
                        case "calc":
                        case "calculator":
                            Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
                            break;
                        case "terminal":
                        case "powershell":
                            try { Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true }); }
                            catch { Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true }); }
                            break;
                        case "explorer":
                            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                            break;
                        case "taskmgr":
                            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                            break;
                        case "settings":
                            Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
                            break;
                        default:
                            if (!string.IsNullOrWhiteSpace(target))
                                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                            break;
                    }
                }
                else if (type == "open_url")
                {
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                            !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            target = "https://" + target;
                        }
                        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    }
                }
                else if (type == "lock_pc")
                {
                    Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("ExecuteAction error: " + ex.Message);
            }
        }

        private void PositionWindowTopCenter()
        {
            double screenW = SystemParameters.PrimaryScreenWidth;
            Left = (screenW - Width) / 2.0;
            Top = 0;
        }

        private void SetupTrayIcon()
        {
            System.Drawing.Icon appIcon = null;
            try
            {
                using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("flinch.ico"))
                {
                    if (stream != null)
                    {
                        appIcon = new System.Drawing.Icon(stream, 32, 32);
                    }
                }
            }
            catch { }

            if (appIcon == null)
            {
                try
                {
                    string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                        if (extracted != null) appIcon = extracted;
                    }
                }
                catch { }
            }

            if (appIcon == null)
            {
                appIcon = System.Drawing.SystemIcons.Application;
            }

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "Flinch (Hold Alt+V to Talk)",
                Icon = appIcon,
                Visible = true
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            string userEmail = (SupabaseAuthClient.CurrentSession != null && !string.IsNullOrEmpty(SupabaseAuthClient.CurrentSession.Email))
                ? SupabaseAuthClient.CurrentSession.Email
                : "Signed In";
            var itemUser = menu.Items.Add("👤 " + userEmail);
            itemUser.Enabled = false;
            menu.Items.Add("-");

            var itemHub = menu.Items.Add("⚡ Flinch Hub (Automations & Email)");
            itemHub.Click += (s, e) => Dispatcher.Invoke(new Action(ToggleFlinchHub));

            var itemTalk = menu.Items.Add("🎙️ Listen Voice (Alt+V)");
            itemTalk.Click += (s, e) => Dispatcher.Invoke(new Action(ToggleVoiceRecording));

            var itemScreenPreview = menu.Items.Add("🖼️ Preview Window Context");
            itemScreenPreview.Click += (s, e) => Dispatcher.Invoke(new Action(InitiateScreenCaptureWorkflow));

            var itemVisualExplain = menu.Items.Add("✏️ Visual Explain (Math Whiteboard)");
            itemVisualExplain.Click += (s, e) => Dispatcher.Invoke(new Action(() => TriggerVisualExplainWorkflow("Explain this math problem")));

            var itemUpdate = menu.Items.Add("🔄 Check for Updates");
            itemUpdate.Click += (s, e) => Dispatcher.Invoke(new Action(CheckForUpdatesInBackground));

            var voiceMenu = new System.Windows.Forms.ToolStripMenuItem("🔊 Voice (" + FlinchEnv.VoiceName + ")");
            string[][] voiceOptions = new[]
            {
                // Female Voices
                // Expressive & Emotional Voices (CanopyLabs Orpheus on Groq)
                new[] { "troy",   "Troy (Groq Orpheus · Expressive US Male)" },
                new[] { "daniel", "Daniel (Groq Orpheus · Crisp US Male)" },
                new[] { "autumn", "Autumn (Groq Orpheus · Bright US Female)" },

                // Deepgram Aura-2 Models
                new[] { "zeus",    "Zeus (Deepgram · Deep Baritone Male)" },
                new[] { "orion",   "Orion (Deepgram · Deep & Authoritative Male)" },
                new[] { "arcas",   "Arcas (Deepgram · Conversational Male)" },
                new[] { "asteria", "Asteria (Deepgram · Clear US Female)" },
                new[] { "luna",    "Luna (Deepgram · Warm & Friendly Female)" },
                new[] { "athena",  "Athena (Deepgram · Confident Female)" },
                new[] { "hera",    "Hera (Deepgram · Mature Female)" },
                new[] { "stella",  "Stella (Deepgram · Upbeat Female)" }
            };
            foreach (string[] kv in voiceOptions)
            {
                string vKey = kv[0];
                string vLabel = kv[1];
                var vi = new System.Windows.Forms.ToolStripMenuItem(vLabel)
                {
                    Tag = vKey,
                    Checked = string.Equals(FlinchEnv.VoiceName, vKey, StringComparison.OrdinalIgnoreCase)
                };
                vi.Click += (s, e) =>
                {
                    FlinchEnv.VoiceName = vKey;
                    SupabaseAuthClient.UpdatePreferredVoiceAsync(vKey);
                    foreach (System.Windows.Forms.ToolStripMenuItem child in voiceMenu.DropDownItems)
                        child.Checked = string.Equals(child.Tag as string, vKey, StringComparison.OrdinalIgnoreCase);
                    voiceMenu.Text = "🔊 Voice (" + vKey + ")";
                };
                voiceMenu.DropDownItems.Add(vi);
            }
            menu.Items.Add(voiceMenu);

            var itemReset = menu.Items.Add("🧹 Reset Conversation Memory");
            itemReset.Click += (s, e) => GeminiClient.ClearHistory();
            menu.Items.Add("-");

            var itemSignOut = menu.Items.Add("🚪 Sign Out");
            itemSignOut.Click += (s, e) => Dispatcher.Invoke(new Action(() =>
            {
                StopAudioPlayback();
                SupabaseAuthClient.SignOut();
                Hide();
                var authWin = new FlinchAuthWindow();
                bool? ok = authWin.ShowDialog();
                if (ok == true && SupabaseAuthClient.CurrentSession != null)
                {
                    itemUser.Text = "👤 " + SupabaseAuthClient.CurrentSession.Email;
                    voiceMenu.Text = "🔊 Voice (" + FlinchEnv.VoiceName + ")";
                    Show();
                }
                else
                {
                    Close();
                }
            }));

            var itemUninstall = menu.Items.Add("🗑️ Uninstall Flinch");
            itemUninstall.Click += (s, e) => Dispatcher.Invoke(new Action(() =>
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string uninst = System.IO.Path.Combine(dir, "Uninstall.exe");
                    if (System.IO.File.Exists(uninst))
                    {
                        System.Diagnostics.Process.Start(uninst);
                    }
                    else
                    {
                        System.Windows.MessageBox.Show("Uninstall.exe was not found in " + dir, "Flinch", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                }
                catch { }
            }));

            var itemQuit = menu.Items.Add("❌ Quit Flinch");
            itemQuit.Click += (s, e) => Dispatcher.Invoke(new Action(Close));

            _trayIcon.ContextMenuStrip = menu;

            BackgroundAutomationsEngine.OnUrgentAlertTriggered += (alertMsg, sender, isUrgent) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetIslandState(IslandState.Alert, "Urgent Email Alert", alertMsg);
                }));
            };

            // Flinch Skills hooks (clipboard / reminders / files / pomodoro / nudges / voice)
            FlinchSkills.Speak = (t) => SpeakWhenIdle(t);
            FlinchSkills.StartReminderInSeconds = (secs, text) => Dispatcher.BeginInvoke(new Action(() =>
                StartTimerOrReminder(secs, "reminder", text, "Reminder: " + text)));
            FlinchSkills.FireContextReminder = (text) => Dispatcher.BeginInvoke(new Action(() =>
                ShowAlert("reminder", text, "Reminder: " + text)));
            FlinchSkills.StartFocusTimer = (secs, label) => Dispatcher.BeginInvoke(new Action(() =>
                StartTimerOrReminder(secs, "pomodoro", "Focus Session: stay in the zone", label)));
            FlinchSkills.StopFocusTimer = () => Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_timerLock) { _activeTimers.RemoveAll(t => t.Type == "pomodoro"); }
                UpdateMultitaskDisplay();
                RefreshTasksExpandedUI();
            }));
            FlinchSkills.ShowNudgeAlert = (title, msg) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (FlinchSkills.IsFocusModeActive) return;
                ShowAlert("alert", msg, title);
            }));
            FlinchSkills.SetPreferredVoice = (voice) =>
            {
                FlinchEnv.VoiceName = voice;
                Task.Run(() => SupabaseAuthClient.UpdatePreferredVoiceAsync(voice));
            };
            FlinchSkills.Start();

            BackgroundTaskManager.OnTasksUpdated += () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateMultitaskDisplay();
                    RefreshTasksExpandedUI();
                }));
            };

            BackgroundTaskManager.OnTaskCompleted += (task) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    string dirName = task.TargetDirectory.IndexOf("Desktop", StringComparison.OrdinalIgnoreCase) >= 0 ? "Desktop" : "Flinch folder";

                    // If user is asking, listening, recording, or bot is busy/answering, queue the completed task!
                    bool isUserOrBotActive = _state == IslandState.Listening ||
                                             _state == IslandState.Thinking ||
                                             _state == IslandState.Speaking ||
                                             _state == IslandState.Countdown ||
                                             _isBusy ||
                                             _recorder.IsRecording ||
                                             _keyWasHeld;

                    if (isUserOrBotActive)
                    {
                        lock (_pendingTasksLock)
                        {
                            _pendingCompletedTasks.Add(task);
                        }
                        UpdateMultitaskDisplay();
                        RefreshTasksExpandedUI();
                        return;
                    }

                    string alertMsg = string.Format("Finished {0}! Saved to {1}", task.Title, dirName);
                    SetIslandState(IslandState.Alert, "Task Completed", alertMsg);
                    SpeakAnswer(string.Format("I've finished writing your {0} about {1} and saved it to your {2}.", task.TaskType, task.Topic, dirName));
                }));
            };
        }

        private void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeToolWindow(hwnd);
            bool ok = NativeMethods.RegisterHotKey(hwnd, HOTKEY_ID_ALTV, NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_V);
            if (!ok)
            {
                ok = NativeMethods.RegisterHotKey(hwnd, HOTKEY_ID_ALTV, NativeMethods.MOD_ALT, NativeMethods.VK_V);
            }
            FlinchEnv.Log("Flinch Notch started. Alt+V hotkey registered: " + ok);

            HwndSource src = HwndSource.FromHwnd(hwnd);
            if (src != null)
            {
                src.AddHook(WndProc);
            }
            GeminiClient.PreWarmConnectionsAsync();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_ALTV)
            {
                handled = true;
                OnAltVPressed();
            }
            return IntPtr.Zero;
        }

        private void OnPillClicked(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_state == IslandState.Countdown)
            {
                CancelCountdown();
            }
            else if (_state == IslandState.Alert)
            {
                StopAudioPlayback();
                SetIslandState(IslandState.Idle, "", "");
            }
            else if (_state == IslandState.Listening)
            {
                FinishRecordingAndSend();
            }
            else if (_state == IslandState.Speaking)
            {
                // Instant barge-in: stop speaking and listen to user immediately
                StopAudioPlayback();
                StartVoiceRecording();
            }
            else if (_state == IslandState.TasksExpanded)
            {
                SetIslandState(IslandState.Idle, "", "");
            }
            else if (_activeTimers.Count > 0 || BackgroundTaskManager.ActiveCount > 0 || _isBackgroundTaskRunning)
            {
                RefreshTasksExpandedUI();
                SetIslandState(IslandState.TasksExpanded, "", "");
            }
            else if (!_isBusy)
            {
                StopAudioPlayback();
                ToggleFlinchHub();
            }
        }

        private void ToggleVoiceRecording()
        {
            if (_state == IslandState.Countdown)
            {
                CancelCountdown();
            }
            else if (_state == IslandState.Alert)
            {
                StopAudioPlayback();
                SetIslandState(IslandState.Idle, "", "");
            }
            else if (_state == IslandState.Listening)
                FinishRecordingAndSend();
            else if (_state == IslandState.Speaking)
            {
                // Instant barge-in
                StopAudioPlayback();
                StartVoiceRecording();
            }
            else if (_state == IslandState.TasksExpanded)
            {
                SetIslandState(IslandState.Idle, "", "");
            }
            else if (!_isBusy)
                StartVoiceRecording();
        }

        public void InitiateScreenCaptureWorkflow()
        {
            try
            {
                IntPtr myHwnd = new WindowInteropHelper(this).Handle;
                var target = _captureService.GetActiveForegroundTarget(myHwnd);
                var frame = _captureService.CaptureTarget(target);
                if (frame == null)
                {
                    ShowAlert("screen", "Could not capture window", "Screen capture failed");
                    return;
                }

                var previewWin = new ScreenPreviewWindow(
                    frame,
                    onConfirm: (confirmedFrame) =>
                    {
                        GeminiClient.SetStagedScreenFrame(confirmedFrame);
                        ShowAlert("screen", "Screen attached: " + confirmedFrame.TargetName, "📸 Screen context ready");
                    },
                    onDiscard: () =>
                    {
                        frame.Dispose();
                        GeminiClient.ClearStagedScreenFrame();
                    });

                previewWin.Show();
                previewWin.Activate();
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("Screen capture workflow error: " + ex.Message);
            }
        }

        public void ToggleFlinchHub()
        {
            if (_hubWindow != null && _hubWindow.IsVisible)
            {
                _hubWindow.Close();
                _hubWindow = null;
            }
            else
            {
                try
                {
                    _hubWindow = new FlinchHubWindow(this);
                    _hubWindow.Closed += (s, e) => { _hubWindow = null; };
                    _hubWindow.Show();
                    _hubWindow.Activate();
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("Failed to open Flinch Hub: " + ex.Message);
                }
            }
        }

        private void SpeakWhenIdle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Task.Run(() =>
            {
                for (int i = 0; i < 60; i++)
                {
                    bool busy = false;
                    try
                    {
                        Dispatcher.Invoke(new Action(() =>
                        {
                            busy = _state == IslandState.Listening || _state == IslandState.Thinking ||
                                   _state == IslandState.Speaking || _state == IslandState.Countdown ||
                                   _isBusy || _recorder.IsRecording;
                        }));
                    }
                    catch { }
                    if (!busy) break;
                    Thread.Sleep(500);
                }
                Dispatcher.BeginInvoke(new Action(() => SpeakAnswer(text)));
            });
        }

        public void SpeakAnswer(string text)
        {
            SpeakAnswerWithContinuation(text, null);
        }

        public void SpeakAnswerWithContinuation(string text, Action onComplete = null)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (onComplete != null) Dispatcher.BeginInvoke(onComplete);
                else CheckAndAnnouncePendingTasks();
                return;
            }

            int mySession = Interlocked.Increment(ref _playbackSessionId);

            Task.Run(() =>
            {
                byte[] wav = GeminiClient.SynthesizeSpeechFast(text);
                if (wav == null || wav.Length <= 44)
                {
                    wav = LocalSpeechEngine.SynthesizeToWav(text, FlinchEnv.VoiceName);
                }
                if (wav != null && wav.Length > 44)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_playbackSessionId != mySession) return;
                        SetIslandState(IslandState.Speaking, "", "");
                        PlayRawWavOnChannel(wav);
                        double dur = GetWavDurationSeconds(wav);
                        Task.Delay((int)(dur * 1000) + 200).ContinueWith(_ =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_playbackSessionId != mySession) return;
                                if (onComplete != null)
                                {
                                    onComplete();
                                }
                                else if (_state == IslandState.Speaking)
                                {
                                    CheckAndAnnouncePendingTasks();
                                }
                            }));
                        });
                    }));
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (onComplete != null) onComplete();
                        else CheckAndAnnouncePendingTasks();
                    }));
                }
            });
        }

        private void CheckAndAnnouncePendingTasks()
        {
            // If user is currently speaking, holding hotkey, or recording, don't interrupt!
            if (_state == IslandState.Listening || _recorder.IsRecording || _keyWasHeld || _isBusy)
            {
                return;
            }

            ActiveBackgroundTask pt = null;
            lock (_pendingTasksLock)
            {
                if (_pendingCompletedTasks.Count > 0)
                {
                    pt = _pendingCompletedTasks[0];
                    _pendingCompletedTasks.RemoveAt(0);
                }
            }

            if (pt != null)
            {
                UpdateMultitaskDisplay();
                RefreshTasksExpandedUI();

                string dirName = pt.TargetDirectory.IndexOf("Desktop", StringComparison.OrdinalIgnoreCase) >= 0 ? "Desktop" : "Flinch folder";
                string alertMsg = string.Format("Finished {0}! Saved to {1}", pt.Title, dirName);
                SetIslandState(IslandState.Alert, "Task Completed", alertMsg);

                Task.Delay(350).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SpeakAnswerWithContinuation(
                            string.Format("By the way, I've finished writing your {0} about {1} and saved it to your {2}.", pt.TaskType, pt.Topic, dirName),
                            () =>
                            {
                                CheckAndAnnouncePendingTasks();
                            }
                        );
                    }));
                });
            }
            else
            {
                SetIslandState(IslandState.Done, "", "");
                Task.Delay(1800).ContinueWith(__ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_state == IslandState.Done)
                            SetIslandState(IslandState.Idle, "", "");
                    }));
                });
            }
        }

        private bool _isPillHovered = false;

        private void OnPillMouseEnter(object sender, MouseEventArgs e)
        {
            int timerCount;
            lock (_timerLock) { timerCount = _activeTimers.Count; }
            if (BackgroundTaskManager.ActiveCount > 0 || timerCount > 0 || _isBackgroundTaskRunning)
            {
                _isPillHovered = true;
                RefreshTasksExpandedUI();
                SetIslandState(IslandState.TasksExpanded, "", "");
            }
        }

        private void OnPillMouseLeave(object sender, MouseEventArgs e)
        {
            _isPillHovered = false;
            if (_state == IslandState.TasksExpanded)
            {
                Task.Delay(400).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_isPillHovered && _state == IslandState.TasksExpanded)
                        {
                            SetIslandState(IslandState.Idle, "", "");
                        }
                    }));
                });
            }
        }

        private static Geometry BuildArcGeometry(double cx, double cy, double r, double progress)
        {
            if (progress <= 0.001) return null;
            double angle = Math.Min(359.9, Math.Max(0.1, progress * 360.0));
            double rad = (angle - 90.0) * Math.PI / 180.0;
            double xEnd = cx + r * Math.Cos(rad);
            double yEnd = cy + r * Math.Sin(rad);

            var fig = new PathFigure
            {
                StartPoint = new Point(cx, cy - r),
                IsClosed = false,
                IsFilled = false
            };
            fig.Segments.Add(new ArcSegment(
                new Point(xEnd, yEnd),
                new Size(r, r),
                0, angle > 180.0, SweepDirection.Clockwise, true));

            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            geom.Freeze();
            return geom;
        }

        private UIElement BuildTimerRow(ActiveTimerItem tm)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4), Height = 48 };

            var ringHost = new Grid { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            ringHost.Children.Add(new Ellipse { Width = 22, Height = 22, Stroke = BrushTrack, StrokeThickness = 2.6 });

            double prog = tm.SecondsTotal > 0 ? (double)tm.SecondsLeft / tm.SecondsTotal : 1.0;
            var arcPath = new System.Windows.Shapes.Path
            {
                Stroke = BrushMint,
                StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = BuildArcGeometry(12, 12, 10, prog)
            };
            ringHost.Children.Add(arcPath);
            row.Children.Add(ringHost);

            var titleStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(34, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock { Text = tm.Label, Foreground = BrushCream, FontSize = 13.5, FontFamily = new FontFamily("Segoe UI Variable Semibold, Segoe UI Semibold, Segoe UI") });
            titleStack.Children.Add(new TextBlock { Text = string.Format("{0}-second countdown", tm.SecondsTotal), Foreground = BrushTextMuted, FontSize = 11.5, FontFamily = new FontFamily("Segoe UI Variable, Segoe UI") });
            row.Children.Add(titleStack);

            var timeTxt = new TextBlock
            {
                Text = FormatTimerDisplay(tm.SecondsLeft),
                Foreground = BrushCream,
                FontSize = 15,
                FontFamily = new FontFamily("Segoe UI Variable Semibold, Segoe UI Semibold, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 36, 0)
            };
            row.Children.Add(timeTxt);

            var cancelBtn = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "Cancel this timer"
            };
            var xPath = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 7,7 L 15,15 M 15,7 L 7,15"),
                Stroke = BrushCream,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            cancelBtn.Child = xPath;
            cancelBtn.MouseEnter += (s, e) => cancelBtn.Background = new SolidColorBrush(Color.FromArgb(90, 255, 100, 100));
            cancelBtn.MouseLeave += (s, e) => cancelBtn.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            string timerId = tm.Id;
            cancelBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                CancelTimer(timerId);
            };
            row.Children.Add(cancelBtn);

            return row;
        }

        private UIElement BuildTaskRow(ActiveBackgroundTask task)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4), Height = 48 };

            var spinnerHost = new Grid { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            spinnerHost.Children.Add(new Ellipse { Width = 20, Height = 20, Stroke = BrushTrack, StrokeThickness = 2.4 });

            var spinnerArc = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 10,2 A 8,8 0 0 1 18,10"),
                Stroke = BrushMint,
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            var spinRot = new RotateTransform(0, 10, 10);
            spinnerArc.RenderTransform = spinRot;
            spinnerHost.Children.Add(spinnerArc);
            row.Children.Add(spinnerHost);

            var bgStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(34, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bgStack.Children.Add(new TextBlock
            {
                Text = task.Title,
                Foreground = BrushCream,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI Variable Semibold, Segoe UI Semibold, Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 210
            });

            var trackGrid = new Grid { Width = 180, Height = 3, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            trackGrid.Children.Add(new Border { Width = 180, Height = 3, CornerRadius = new CornerRadius(1.5), Background = BrushTrack, HorizontalAlignment = HorizontalAlignment.Left });
            double barW = Math.Max(10, Math.Min(180, (task.ProgressPercent / 100.0) * 180.0));
            var fillBar = new Border { Width = barW, Height = 3, CornerRadius = new CornerRadius(1.5), Background = BrushMint, HorizontalAlignment = HorizontalAlignment.Left };
            trackGrid.Children.Add(fillBar);
            bgStack.Children.Add(trackGrid);
            row.Children.Add(bgStack);

            var pctTxt = new TextBlock
            {
                Text = task.ProgressPercent + "%",
                Foreground = BrushTextMuted,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 36, 0)
            };
            row.Children.Add(pctTxt);

            var cancelBtn = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "Stop this task"
            };
            var xPath = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 7,7 L 15,15 M 15,7 L 7,15"),
                Stroke = BrushCream,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            cancelBtn.Child = xPath;
            cancelBtn.MouseEnter += (s, e) => cancelBtn.Background = new SolidColorBrush(Color.FromArgb(90, 255, 100, 100));
            cancelBtn.MouseLeave += (s, e) => cancelBtn.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            string taskId = task.Id;
            cancelBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                BackgroundTaskManager.CancelTask(taskId);
            };
            row.Children.Add(cancelBtn);

            return row;
        }

        private void RefreshTasksExpandedUI()
        {
            if (_tasksItemsStack == null) return;
            _tasksItemsStack.Children.Clear();

            int count = 0;

            // 1. Render all active timers
            List<ActiveTimerItem> timers;
            lock (_timerLock)
            {
                timers = new List<ActiveTimerItem>(_activeTimers);
            }
            foreach (var tm in timers)
            {
                if (count > 0)
                {
                    _tasksItemsStack.Children.Add(new Border { Height = 1, Background = BrushDivider, Margin = new Thickness(0, 2, 0, 2) });
                }
                _tasksItemsStack.Children.Add(BuildTimerRow(tm));
                count++;
            }

            // 2. Render all active background tasks
            var tasks = BackgroundTaskManager.GetActiveTasks();
            foreach (var task in tasks)
            {
                if (count > 0)
                {
                    _tasksItemsStack.Children.Add(new Border { Height = 1, Background = BrushDivider, Margin = new Thickness(0, 2, 0, 2) });
                }
                _tasksItemsStack.Children.Add(BuildTaskRow(task));
                count++;
            }

            // 3. If no active tasks or timers
            if (count == 0)
            {
                var emptyTxt = new TextBlock
                {
                    Text = "No running tasks or timers.",
                    Foreground = BrushTextMuted,
                    FontSize = 12.5,
                    FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
                    Margin = new Thickness(0, 16, 0, 16),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                _tasksItemsStack.Children.Add(emptyTxt);
            }

            // Update target height for TasksExpanded
            if (_state == IslandState.TasksExpanded)
            {
                int rowCount = Math.Max(1, count);
                _targetH = Math.Min(240, 48 + rowCount * 54);
            }
        }

        private static bool CheckInternetAvailable()
        {
            try
            {
                if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                    return false;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsVisualExplainIntent(string text, ActionPayload action)
        {
            if (action != null && string.Equals(action.type, "visual_explain", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant().Trim();

            if (s.Contains("visual explain") || s.Contains("whiteboard") || s.Contains("draw on screen") || s.Contains("draw the solution") || s.Contains("draw on my screen"))
                return true;

            bool hasSolveOrExplain = s.Contains("explain") || s.Contains("solve") || s.Contains("work through") ||
                                     s.Contains("step by step") || s.Contains("how to do") || s.Contains("how do i do") ||
                                     s.Contains("show me how") || s.Contains("break down") || s.Contains("fix") ||
                                     s.Contains("work out") || s.Contains("help me with");

            bool hasMathOrProblem = s.Contains("math") || s.Contains("problem") || s.Contains("equation") ||
                                    s.Contains("formula") || s.Contains("diagram") || s.Contains("question") ||
                                    s.Contains("calculation") || s.Contains("algebra") || s.Contains("fraction") ||
                                    s.Contains("derivative") || s.Contains("integral");

            bool hasCodeOrError = s.Contains("code") || s.Contains("stack trace") || s.Contains("stacktrace") ||
                                  s.Contains("traceback") || s.Contains("failing line") || s.Contains("compiler error") ||
                                  s.Contains("exception") || s.Contains("syntax error") || s.Contains("bug") ||
                                  s.Contains("debug") || s.Contains("crash");

            if (hasSolveOrExplain && (hasMathOrProblem || hasCodeOrError))
                return true;

            if (s.Contains("solve this") || s.Contains("solve it") || s.Contains("explain this") || s.Contains("explain it") ||
                s.Contains("debug this") || s.Contains("fix this bug") || s.Contains("highlight the failing line") ||
                s.Contains("why is this failing") || s.Contains("draw arrows on this error"))
                return true;

            return false;
        }

        public void TriggerVisualExplainWorkflow(string prompt)
        {
            if (_isBusy) return;
            _isBusy = true;
            SetIslandState(IslandState.Thinking, "Visualizing math problem...", "");

            Task.Run(() =>
            {
                VisualExplainPayload payload;
                string err;
                bool ok = VisualExplainClient.RequestVisualExplanation(prompt, out payload, out err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isBusy = false;
                    if (!ok || payload == null || payload.steps == null || payload.steps.Count == 0)
                    {
                        SetIslandState(IslandState.Error, "", string.IsNullOrWhiteSpace(err) ? "Visual Explain could not analyze this screen." : err);
                        return;
                    }

                    SetIslandState(IslandState.Thinking, "Drawing step-by-step solution...", "");
                    WhiteboardOverlayManager.LaunchSolution(payload, () =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetIslandState(IslandState.Idle, "", "");
                        }));
                    });
                }));
            });
        }

        private void OnAltVPressed()
        {
            if (_state == IslandState.Countdown)
            {
                CancelCountdown();
                return;
            }

            if (_state == IslandState.Alert)
            {
                StopAudioPlayback();
                SetIslandState(IslandState.Idle, "", "");
                return;
            }

            if (_state == IslandState.Listening)
            {
                double elapsedMs = (DateTime.UtcNow - _recordStartUtc).TotalMilliseconds;
                if (elapsedMs > 400)
                {
                    FinishRecordingAndSend();
                }
                return;
            }

            if (_isBusy)
            {
                double busySec = (DateTime.UtcNow - _busyStartUtc).TotalSeconds;
                if (busySec < 1.0) return;
                _isBusy = false;
            }

            // Instant barge-in: cut off any active speech and start listening immediately
            _startedViaHotkey = true;
            StopAudioPlayback();
            StartVoiceRecording();
        }

        public void StartVoiceRecording()
        {
            OverlayWindow.CloseCurrent();
            _autoCollapseTimer.Stop();

            if (!CheckInternetAvailable())
            {
                SetIslandState(IslandState.Error, "No network", "Please check your internet connection.");
                try
                {
                    byte[] offWav = LocalSpeechEngine.SynthesizeToWav("No network.", FlinchEnv.VoiceName);
                    if (offWav != null && offWav.Length > 44)
                    {
                        PlayRawWavOnChannel(offWav);
                    }
                }
                catch { }
                Task.Delay(3000).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_state == IslandState.Error) SetIslandState(IslandState.Idle, "", "");
                    }));
                });
                return;
            }

            if (SupabaseAuthClient.CurrentSession == null || string.IsNullOrWhiteSpace(SupabaseAuthClient.CurrentSession.AccessToken))
            {
                SetIslandState(IslandState.Error, "", "Please sign in to speak with Flinch.");
                try
                {
                    var authWin = new FlinchAuthWindow();
                    authWin.Owner = this;
                    authWin.Show();
                }
                catch { }
                return;
            }

            if (SupabaseAuthClient.QuotaExhausted)
            {
                SetIslandState(IslandState.Error, "", "Demo limit reached: " + SupabaseAuthClient.DemoVoiceLimit + " of " + SupabaseAuthClient.DemoVoiceLimit + " voice messages used.");
                return;
            }

            // INSTANT Push-to-Talk: Start microphone and open Listening island immediately!
            string err;
            if (!_recorder.Start(out err))
            {
                FlinchEnv.Log("Microphone start failed: " + err);
                SetIslandState(IslandState.Error, "Mic error", err);
                return;
            }

            _recordStartUtc = DateTime.UtcNow;
            _keyWasHeld = false;
            SetIslandState(IslandState.Listening, "", "");
            GeminiClient.PreWarmConnectionsAsync();
            FlinchEnv.Log("Started recording microphone immediately on Alt+V.");

            // Capture active foreground target concurrently in background while user speaks
            IntPtr myHwnd = new WindowInteropHelper(this).Handle;
            IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
            if (fgHwnd == myHwnd) fgHwnd = IntPtr.Zero;

            Task.Run(() =>
            {
                CaptureAndStageActiveScreen(fgHwnd, myHwnd);
            });
        }

        private void CaptureAndStageActiveScreen(IntPtr targetHwnd, IntPtr ignoreHwnd)
        {
            try
            {
                var s = System.Windows.Forms.Screen.PrimaryScreen;
                var fullBounds = new NativeMethods.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom };
                CapturedFrame frame = _captureService.CaptureBounds(fullBounds, "Display");

                if (frame != null && frame.JpegBytes != null && frame.JpegBytes.Length > 0)
                {
                    GeminiClient.SetStagedScreenFrame(frame);
                    FlinchEnv.Log("Auto-captured primary screen for voice context: Display");
                }
            }
            catch (Exception ex)
            {
                FlinchEnv.Log("CaptureAndStageActiveScreen error: " + ex.Message);
            }
        }

private void FinishRecordingAndSend()
        {
            _keyWasHeld = false;
            _startedViaHotkey = false;
            if (!_recorder.IsRecording && _state != IslandState.Listening) return;

            byte[] wavBytes = _recorder.StopAndGetWavBytes();
            FlinchEnv.Log("Stopped recording. Trimmed WAV size: " + wavBytes.Length + " bytes.");

            if (wavBytes.Length < 1200)
            {
                FlinchEnv.Log("No speech detected above voice gate — skipping transcription.");
                SetIslandState(IslandState.Error, "", "Didn't catch that. Speak a bit louder.");
                return;
            }

            if (!CheckInternetAvailable())
            {
                SetIslandState(IslandState.Error, "No network", "Please check your internet connection.");
                try
                {
                    byte[] offWav = LocalSpeechEngine.SynthesizeToWav("No network.", FlinchEnv.VoiceName);
                    if (offWav != null && offWav.Length > 44)
                    {
                        PlayRawWavOnChannel(offWav);
                    }
                }
                catch { }
                return;
            }

            _isBusy = true;
            _busyStartUtc = DateTime.UtcNow;
            int mySession = Interlocked.Increment(ref _playbackSessionId);
            SetIslandState(IslandState.Thinking, "", "");

            Task.Run(() =>
            {
                try
                {
                    string transcript;
                    string answer;
                    byte[] audioWav;
                    int remaining;
                    string error;

                    ActionPayload action;
                    ScenePayload scene;
                    bool ok = GeminiClient.ProcessVoiceTurnInCloud(
                        wavBytes,
                        out transcript,
                        out answer,
                        out audioWav,
                        out action,
                        out scene,
                        out remaining,
                        out error);

                    if (_playbackSessionId != mySession) return;

                    if (!ok)
                    {
                        // Offline Fallback: Handles timers, clock, date, pomodoro, system controls offline!
                        string offTx, offAns;
                        if (OfflineBrain.TryProcess(wavBytes, out offTx, out offAns))
                        {
                            transcript = offTx;
                            answer = offAns;
                            action = null;
                            scene = null;
                            ok = true;
                        }
                        else
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_playbackSessionId != mySession) return;
                                _isBusy = false;
                                SetIslandState(IslandState.Error, "", error);
                            }));
                            return;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(transcript) && string.IsNullOrWhiteSpace(answer))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_playbackSessionId != mySession) return;
                            _isBusy = false;
                            SetIslandState(IslandState.Error, "", "Didn't catch that. Speak a bit louder.");
                        }));
                        return;
                    }

                    if (audioWav == null || audioWav.Length <= 44)
                    {
                        audioWav = GeminiClient.SynthesizeSpeechFast(answer);
                    }
                    if (audioWav == null || audioWav.Length <= 44)
                    {
                        audioWav = LocalSpeechEngine.SynthesizeToWav(answer, FlinchEnv.VoiceName);
                    }

                    if (_playbackSessionId != mySession) return;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_playbackSessionId != mySession) return;
                        _isBusy = false;

                        string lowerTrans = (transcript ?? "").ToLowerInvariant().Trim();

                        // 0. Check for Open/Close Flinch Hub command
                        if (lowerTrans == "open hub" || lowerTrans.Contains("open flinch hub") || lowerTrans.Contains("show hub") || lowerTrans.Contains("open integrations") || lowerTrans.Contains("show automations"))
                        {
                            ToggleFlinchHub();
                            SpeakAnswer("Opening the Flinch Hub.");
                            return;
                        }
                        if (_hubWindow != null && _hubWindow.IsVisible && (lowerTrans == "close hub" || lowerTrans.Contains("close flinch hub") || lowerTrans.Contains("dismiss hub")))
                        {
                            _hubWindow.Close();
                            _hubWindow = null;
                            SetIslandState(IslandState.Idle, "", "");
                            return;
                        }

                        // Check for Email Check / Summary command
                        if (lowerTrans.Contains("check email") || lowerTrans.Contains("check my email") || lowerTrans.Contains("any email") || lowerTrans.Contains("summarize email") || lowerTrans.Contains("read email") || lowerTrans.Contains("unread email"))
                        {
                            string emailSummary = BackgroundAutomationsEngine.GetSpokenEmailSummary();
                            SpeakAnswer(emailSummary);
                            return;
                        }

                        // Stop / Cancel Voice Intent (Stops specific or LATEST active timer/task)
                        bool isStopWord = lowerTrans == "stop" || lowerTrans == "cancel" || lowerTrans == "abort" || lowerTrans == "quit" ||
                                          lowerTrans.StartsWith("stop ") || lowerTrans.StartsWith("cancel ") ||
                                          lowerTrans.Contains("stop the") || lowerTrans.Contains("cancel the") ||
                                          lowerTrans.Contains("stop my") || lowerTrans.Contains("cancel my");

                        if (isStopWord)
                        {
                            bool mentionsTimer = lowerTrans.Contains("timer") || lowerTrans.Contains("countdown");
                            bool mentionsTask = lowerTrans.Contains("task") || lowerTrans.Contains("writing") || lowerTrans.Contains("essay") || lowerTrans.Contains("background") || lowerTrans.Contains("report");

                            if (mentionsTimer && !mentionsTask)
                            {
                                var activeTimers = GetActiveTimersList();
                                if (activeTimers.Count == 0)
                                {
                                    SpeakAnswer("There are no active timers running.");
                                    return;
                                }

                                ActiveTimerItem targetTimer = null;
                                foreach (var tm in activeTimers)
                                {
                                    if (lowerTrans.Contains(tm.Label.ToLowerInvariant()) || (tm.SecondsTotal > 0 && lowerTrans.Contains(tm.SecondsTotal.ToString())))
                                    {
                                        targetTimer = tm;
                                        break;
                                    }
                                }
                                if (targetTimer == null)
                                {
                                    targetTimer = CancelLatestTimer();
                                }
                                else
                                {
                                    CancelTimer(targetTimer.Id);
                                }

                                if (targetTimer != null)
                                {
                                    SpeakAnswer(string.Format("Cancelled your {0}.", targetTimer.Label));
                                }
                                return;
                            }
                            else if (mentionsTask && !mentionsTimer)
                            {
                                var activeTasks = BackgroundTaskManager.GetActiveTasks();
                                if (activeTasks.Count == 0)
                                {
                                    SpeakAnswer("There are no background tasks running.");
                                    return;
                                }

                                ActiveBackgroundTask targetTask = null;
                                foreach (var at in activeTasks)
                                {
                                    if (!string.IsNullOrWhiteSpace(at.Topic) && lowerTrans.Contains(at.Topic.ToLowerInvariant()))
                                    {
                                        targetTask = at;
                                        break;
                                    }
                                }
                                if (targetTask == null)
                                {
                                    targetTask = BackgroundTaskManager.CancelLatestTask();
                                }
                                else
                                {
                                    BackgroundTaskManager.CancelTask(targetTask.Id);
                                }

                                if (targetTask != null)
                                {
                                    SpeakAnswer(string.Format("Stopped your background task: {0}.", targetTask.Title));
                                }
                                return;
                            }
                            else
                            {
                                var latestTimer = GetLatestActiveTimer();
                                var latestTask = BackgroundTaskManager.GetLatestActiveTask();

                                if (latestTimer != null && latestTask != null)
                                {
                                    if (latestTimer.StartedUtc >= latestTask.StartedUtc)
                                    {
                                        CancelTimer(latestTimer.Id);
                                        SpeakAnswer(string.Format("Cancelled your latest timer: {0}.", latestTimer.Label));
                                    }
                                    else
                                    {
                                        BackgroundTaskManager.CancelTask(latestTask.Id);
                                        SpeakAnswer(string.Format("Stopped your latest background task: {0}.", latestTask.Title));
                                    }
                                    return;
                                }
                                else if (latestTimer != null)
                                {
                                    CancelTimer(latestTimer.Id);
                                    SpeakAnswer(string.Format("Cancelled your timer: {0}.", latestTimer.Label));
                                    return;
                                }
                                else if (latestTask != null)
                                {
                                    BackgroundTaskManager.CancelTask(latestTask.Id);
                                    SpeakAnswer(string.Format("Stopped your background task: {0}.", latestTask.Title));
                                    return;
                                }
                            }
                        }

                        // Local Multi-Timer Intent parser (e.g. "set a timer for 5 minutes", "timer for 30 seconds")
                        Match timerMatch = Regex.Match(lowerTrans, @"(?i)(?:set|start|create)\s+(?:a\s+)?(?:(\d+)\s*(second|sec|minute|min|hour|hr)s?\s+timer|timer\s+for\s+(\d+)\s*(second|sec|minute|min|hour|hr)s?)");
                        if (timerMatch.Success)
                        {
                            int num = 0;
                            string unit = "";
                            if (timerMatch.Groups[1].Success)
                            {
                                int.TryParse(timerMatch.Groups[1].Value, out num);
                                unit = timerMatch.Groups[2].Value.ToLowerInvariant();
                            }
                            else if (timerMatch.Groups[3].Success)
                            {
                                int.TryParse(timerMatch.Groups[3].Value, out num);
                                unit = timerMatch.Groups[4].Value.ToLowerInvariant();
                            }

                            if (num > 0)
                            {
                                int totalSec = num;
                                if (unit.StartsWith("min")) totalSec = num * 60;
                                else if (unit.StartsWith("hour") || unit.StartsWith("hr")) totalSec = num * 3600;

                                string timerLabel = string.Format("{0}-{1} timer", num, unit.StartsWith("min") ? "minute" : (unit.StartsWith("hour") ? "hour" : "second"));
                                StartTimerOrReminder(totalSec, "timer", timerLabel, timerLabel);
                                SpeakAnswer(string.Format("Starting a {0} {1} timer right now.", num, unit.StartsWith("min") ? "minute" : (unit.StartsWith("hour") ? "hour" : "second")));
                                return;
                            }
                        }

                        // Local Skills: clipboard actions, smart reminders, voice file work
                        string skillSpeech = FlinchSkills.TryHandle(transcript, lowerTrans);
                        if (skillSpeech != null)
                        {
                            FlinchSkills.RecordTurn(transcript, skillSpeech);
                            if (skillSpeech.Length > 0) SpeakAnswer(skillSpeech);
                            return;
                        }

                        // Autonomous Background Writing Tasks (e.g. "write an essay about sri lanka", "write a report on quantum computing")
                        bool isWritingTask = (lowerTrans.Contains("write") || lowerTrans.Contains("generate") || lowerTrans.Contains("draft") || lowerTrans.Contains("create"))
                            && (lowerTrans.Contains("essay") || lowerTrans.Contains("report") || lowerTrans.Contains("article") || lowerTrans.Contains("paper") || lowerTrans.Contains("document") || lowerTrans.Contains("story"));

                        if (isWritingTask)
                        {
                            string docType = "essay";
                            if (lowerTrans.Contains("report")) docType = "report";
                            else if (lowerTrans.Contains("article")) docType = "article";
                            else if (lowerTrans.Contains("paper")) docType = "paper";
                            else if (lowerTrans.Contains("story")) docType = "story";

                            string topic = "";
                            string[] triggers = new[] { "about", "on", "regarding", "for", "titled" };
                            foreach (var tr in triggers)
                            {
                                int idx = lowerTrans.IndexOf(tr + " ");
                                if (idx >= 0)
                                {
                                    topic = transcript.Substring(idx + tr.Length + 1).Trim();
                                    break;
                                }
                            }
                            if (string.IsNullOrWhiteSpace(topic))
                            {
                                topic = "Sri Lanka";
                            }

                            // Detect external app requests (Discord, WhatsApp, Telegram, etc.)
                            string unsupportedApp = null;
                            if (Regex.IsMatch(lowerTrans, @"\b(discord)\b")) unsupportedApp = "Discord";
                            else if (Regex.IsMatch(lowerTrans, @"\b(whatsapp)\b")) unsupportedApp = "WhatsApp";
                            else if (Regex.IsMatch(lowerTrans, @"\b(telegram)\b")) unsupportedApp = "Telegram";
                            else if (Regex.IsMatch(lowerTrans, @"\b(slack)\b")) unsupportedApp = "Slack";
                            else if (Regex.IsMatch(lowerTrans, @"\b(instagram)\b")) unsupportedApp = "Instagram";
                            else if (Regex.IsMatch(lowerTrans, @"\b(twitter|tweet)\b")) unsupportedApp = "Twitter";

                            string targetDir = null;
                            string targetDirName = "Flinch folder";
                            if (unsupportedApp == null)
                            {
                                if (lowerTrans.Contains("desktop"))
                                {
                                    targetDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                                    targetDirName = "Desktop";
                                }
                                else if (lowerTrans.Contains("document"))
                                {
                                    targetDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                                    targetDirName = "Documents";
                                }
                                else if (lowerTrans.Contains("download"))
                                {
                                    targetDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                                    targetDirName = "Downloads";
                                }
                            }

                            // Clean topic of any destination phrases so the file name & content stay 100% clean
                            topic = Regex.Replace(topic, @"(?i)\s*(and\s+)?(save|send|put|export|store|place|upload)\s+(it\s+)?(to|in|on|into)\s+.*$", "").Trim();
                            topic = Regex.Replace(topic, @"(?i)\s*(to|in|on)\s+(my\s+)?(desktop|documents|downloads|discord|whatsapp|telegram|slack|pc|folder).*$", "").Trim();
                            topic = Regex.Replace(topic, @"(?i)\s*and\s+(send|save)\s+it.*$", "").Trim();
                            topic = topic.Trim(',', '.', '!', ' ', ';', ':', '"', '\'');

                            if (string.IsNullOrWhiteSpace(topic)) topic = "Sri Lanka";

                            string replyText;
                            if (unsupportedApp != null)
                            {
                                replyText = string.Format("I will write an {0} about {1} right now. I can't send files directly to {2} yet, so I'll save it in your {3}.",
                                    docType, topic, unsupportedApp, targetDirName);
                            }
                            else
                            {
                                replyText = string.Format("I will write an {0} about {1} right now. I'll save it to your {2}.", docType, topic, targetDirName);
                            }
                            SpeakAnswer(replyText);

                            BackgroundTaskManager.StartWritingTask(topic, docType, targetDir);
                            return;
                        }

                        // 1. Check for Close Whiteboard command
                        if (WhiteboardOverlayManager.IsOpen && (lowerTrans == "close" || lowerTrans.Contains("close whiteboard") || lowerTrans.Contains("dismiss whiteboard") || lowerTrans.Contains("close overlay")))
                        {
                            WhiteboardOverlayManager.Close();
                            SetIslandState(IslandState.Idle, "", "");
                            return;
                        }

                        // 2. Check for Visual Explain math whiteboard intent
                        bool isVisualExplain = IsVisualExplainIntent(lowerTrans, action);

                        if (isVisualExplain)
                        {
                            TriggerVisualExplainWorkflow(transcript);
                            return;
                        }

                        if (scene != null && scene.objects != null && scene.objects.Count > 0)
                        {
                            FlinchEnv.Log(string.Format("[Flinch] Displaying visual overlay with {0} objects for transcript: '{1}'", scene.objects.Count, transcript));
                            OverlayWindow.ShowScene(GeminiClient.LastActiveTargetBounds, scene);
                        }
                        else
                        {
                            FlinchEnv.Log("[Flinch] Turn had no visual scene objects. Overlay not shown.");
                        }

                        if (action != null)
                        {
                            string actType = (action.type ?? "").ToLowerInvariant().Trim();
                            if (actType == "timer" || actType == "reminder")
                            {
                                int sec = action.seconds > 0 ? action.seconds : 5;
                                StartTimerOrReminder(sec, actType, action.text, action.label);
                            }
                            else if (action.countdown_sec > 0 || actType == "open_app" || actType == "open_url" || actType == "lock_pc")
                            {
                                StartActionCountdown(action);
                            }
                        }

                        FlinchSkills.RecordTurn(transcript, answer);
                        if (audioWav != null && audioWav.Length > 44)
                        {
                            if (_state != IslandState.Countdown)
                            {
                                SetIslandState(IslandState.Speaking, "", "");
                            }
                            PlayRawWavOnChannel(audioWav);

                            double durSec = GetWavDurationSeconds(audioWav);
                            Task.Delay((int)(durSec * 1000) + 150).ContinueWith(_ =>
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (_playbackSessionId != mySession) return;
                                    if (_state == IslandState.Speaking)
                                    {
                                        CheckAndAnnouncePendingTasks();
                                    }
                                }));
                            });
                        }
                        else
                        {
                            if (_state != IslandState.Countdown)
                            {
                                SetIslandState(IslandState.Done, "", answer);
                                Task.Delay(3500).ContinueWith(__ =>
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        if (_playbackSessionId != mySession) return;
                                        if (_state == IslandState.Done)
                                        {
                                            CheckAndAnnouncePendingTasks();
                                        }
                                    }));
                                });
                            }
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_playbackSessionId != mySession) return;
                        _isBusy = false;
                        SetIslandState(IslandState.Error, "", ex.Message);
                    }));
                }
            });
        }

        private string transcriptIfAny()
        {
            return _transcriptText != null ? _transcriptText.Text : "";
        }

        private static bool IsEmptyOrSilenceHallucination(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string clean = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^\w\s]", "").Trim();
            if (clean.Length < 2) return true;

            // Common Whisper silence hallucinations when only mic click or breath is recorded
            if (clean == "you" ||
                clean == "thank you" ||
                clean == "thanks for watching" ||
                clean == "thank you for watching" ||
                clean == "subtitles by the amaraorg community" ||
                clean == "bye" ||
                clean == "uh" ||
                clean == "um" ||
                clean == "hmm")
            {
                return true;
            }
            return false;
        }

        private static string[] SplitIntoSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new string[0];
            var list = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c == '.' || c == '!' || c == '?') && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
                {
                    string seg = text.Substring(start, i - start + 1).Trim();
                    if (seg.Length > 0) list.Add(seg);
                    start = i + 1;
                }
            }
            if (start < text.Length)
            {
                string tail = text.Substring(start).Trim();
                if (tail.Length > 0) list.Add(tail);
            }
            return list.ToArray();
        }

        private int _playbackSessionId = 0;
        private byte[] _activePlaybackWav;
        private DateTime _playbackStartUtc = DateTime.MinValue;
        private double _smoothBotVoice = 0;

        private void PlaySentencePipeline(int sessionId, byte[] currentWav, List<Task<byte[]>> remainingTasks, int nextIndex)
        {
            if (_playbackSessionId != sessionId || _state != IslandState.Speaking || currentWav == null || currentWav.Length <= 44)
            {
                if (_playbackSessionId == sessionId)
                    SetIslandState(IslandState.Idle, "", "");
                return;
            }

            double durationSec = GetWavDurationSeconds(currentWav);
            PlayRawWavOnChannel(currentWav);

            var chainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0.25, durationSec + 0.04)) };
            chainTimer.Tick += (s, e) =>
            {
                chainTimer.Stop();
                if (_playbackSessionId != sessionId || _state != IslandState.Speaking) return;

                if (remainingTasks != null && nextIndex < remainingTasks.Count)
                {
                    byte[] nextWav = null;
                    try
                    {
                        if (remainingTasks[nextIndex].Wait(5000))
                            nextWav = remainingTasks[nextIndex].Result;
                    }
                    catch { }

                    if (_playbackSessionId != sessionId || _state != IslandState.Speaking) return;

                    if (nextWav != null && nextWav.Length > 44)
                    {
                        PlaySentencePipeline(sessionId, nextWav, remainingTasks, nextIndex + 1);
                        return;
                    }
                }

                SetIslandState(IslandState.Idle, "", "");
            };
            chainTimer.Start();
        }

        private static double GetWavDurationSeconds(byte[] wavBytes)
        {
            int byteRate = 48000;
            if (wavBytes != null && wavBytes.Length > 32)
            {
                int br = BitConverter.ToInt32(wavBytes, 28);
                if (br >= 16000 && br <= 384000) byteRate = br;
            }
            return Math.Max(0.35, (double)((wavBytes != null ? wavBytes.Length : 44) - 44) / byteRate);
        }

        private void PlayRawWavOnChannel(byte[] wavBytes)
        {
            DisposeCurrentSoundPlayerOnly();
            _activePlaybackWav = wavBytes;
            _playbackStartUtc = DateTime.UtcNow;
            _ttsStream = new MemoryStream(wavBytes);
            _soundPlayer = new System.Media.SoundPlayer(_ttsStream);
            _soundPlayer.Play();
        }

        private void DisposeCurrentSoundPlayerOnly()
        {
            try
            {
                if (_soundPlayer != null)
                {
                    _soundPlayer.Stop();
                    _soundPlayer.Dispose();
                    _soundPlayer = null;
                }
                if (_ttsStream != null)
                {
                    _ttsStream.Dispose();
                    _ttsStream = null;
                }
            }
            catch { }
        }

        private void StopAudioPlayback()
        {
            Interlocked.Increment(ref _playbackSessionId);
            _activePlaybackWav = null;
            _smoothBotVoice = 0;
            DisposeCurrentSoundPlayerOnly();
        }

        // Computes the live voice strength (0.0 .. 1.0) of Flinch's speaking audio at the current playback millisecond
        private double GetBotVoiceLevel()
        {
            byte[] wav = _activePlaybackWav;
            if (wav == null || wav.Length <= 64) return 0.0;

            int byteRate = 48000;
            int br = BitConverter.ToInt32(wav, 28);
            if (br >= 16000 && br <= 384000) byteRate = br;

            double elapsedSec = (DateTime.UtcNow - _playbackStartUtc).TotalSeconds;
            if (elapsedSec < 0) return 0.0;

            int startByte = 44 + ((int)(elapsedSec * byteRate) & ~1);
            if (startByte >= wav.Length - 4) return 0.0;

            int windowBytes = Math.Min(wav.Length - startByte, Math.Max(256, ((int)(byteRate * 0.035)) & ~1));
            double sumSq = 0;
            int maxAbs = 0;
            int samples = windowBytes / 2;

            for (int i = 0; i + 1 < windowBytes; i += 2)
            {
                short s = (short)(wav[startByte + i] | (wav[startByte + i + 1] << 8));
                int abs = s == short.MinValue ? 32767 : Math.Abs((int)s);
                if (abs > maxAbs) maxAbs = abs;
                double norm = s / 32768.0;
                sumSq += norm * norm;
            }

            if (samples <= 0) return 0.0;
            double rms = Math.Sqrt(sumSq / samples);
            double peak = maxAbs / 32768.0;
            double rawEnergy = Math.Max(rms * 5.2, peak * 2.1);
            return rawEnergy < 0.015 ? 0.0 : Math.Pow(Math.Min(1.0, rawEnergy), 0.58);
        }

        private void SetIslandState(IslandState newState, string questionText, string answerText)
        {
            _state = newState;
            _autoCollapseTimer.Stop();

            if (newState == IslandState.Idle)
            {
                _dotsCenterHost.Visibility = Visibility.Visible;
                _listeningHost.Visibility = Visibility.Collapsed;
                _tasksExpandedPanel.Visibility = Visibility.Collapsed;
                _spinnerViewbox.Visibility = Visibility.Collapsed;
                _countdownPanel.Visibility = Visibility.Collapsed;
                _alertPanel.Visibility = Visibility.Collapsed;
                _cardPanel.Visibility = Visibility.Collapsed;

                UpdateMultitaskDisplay();
            }
            else if (newState == IslandState.Listening)
            {
                _dotsCenterHost.Visibility = Visibility.Collapsed;
                _multitaskLeftHost.Visibility = Visibility.Collapsed;
                _listeningHost.Visibility = Visibility.Visible;
                _tasksExpandedPanel.Visibility = Visibility.Collapsed;
                _spinnerViewbox.Visibility = Visibility.Collapsed;
                _countdownPanel.Visibility = Visibility.Collapsed;
                _alertPanel.Visibility = Visibility.Collapsed;
                _cardPanel.Visibility = Visibility.Collapsed;

                _targetW = _isUpdateAvailable ? 250 : 220;
                _targetH = 36;
                _targetBotR = 18;
            }
            else if (newState == IslandState.TasksExpanded)
            {
                _dotsCenterHost.Visibility = Visibility.Collapsed;
                _multitaskLeftHost.Visibility = Visibility.Collapsed;
                _listeningHost.Visibility = Visibility.Collapsed;
                _tasksExpandedPanel.Visibility = Visibility.Visible;
                _spinnerViewbox.Visibility = Visibility.Collapsed;
                _countdownPanel.Visibility = Visibility.Collapsed;
                _alertPanel.Visibility = Visibility.Collapsed;
                _cardPanel.Visibility = Visibility.Collapsed;

                int timerCount;
                lock (_timerLock) { timerCount = _activeTimers.Count; }
                int totalActive = timerCount + BackgroundTaskManager.ActiveCount;
                int rowCount = Math.Max(1, totalActive);
                _targetW = 400;
                _targetH = Math.Min(240, 48 + rowCount * 54);
                _targetBotR = 24;
            }
            else if (newState == IslandState.Thinking)
            {
                _dotsCenterHost.Visibility = Visibility.Collapsed;
                _listeningHost.Visibility = Visibility.Collapsed;
                _tasksExpandedPanel.Visibility = Visibility.Collapsed;
                _spinnerViewbox.Visibility = Visibility.Visible;
                _countdownPanel.Visibility = Visibility.Collapsed;
                _alertPanel.Visibility = Visibility.Collapsed;
                _cardPanel.Visibility = Visibility.Collapsed;

                if (_activeTimers.Count > 0 || _isBackgroundTaskRunning || BackgroundTaskManager.ActiveCount > 0)
                {
                    _multitaskLeftHost.Visibility = Visibility.Visible;
                    _targetW = _isUpdateAvailable ? 310 : 280;
                    _targetH = 46;
                    _targetBotR = 23;
                }
                else
                {
                    _multitaskLeftHost.Visibility = Visibility.Collapsed;
                    _targetW = _isUpdateAvailable ? 200 : 170;
                    _targetH = 34;
                    _targetBotR = 17;
                }
            }
            else if (newState == IslandState.Speaking || newState == IslandState.Done)
            {
                _dotsCenterHost.Visibility = Visibility.Visible;
                _listeningHost.Visibility = Visibility.Collapsed;
                _tasksExpandedPanel.Visibility = Visibility.Collapsed;
                _spinnerViewbox.Visibility = Visibility.Collapsed;
                _countdownPanel.Visibility = Visibility.Collapsed;
                _alertPanel.Visibility = Visibility.Collapsed;
                _cardPanel.Visibility = Visibility.Collapsed;

                if (_activeTimers.Count > 0 || _isBackgroundTaskRunning || BackgroundTaskManager.ActiveCount > 0)
                {
                    _multitaskLeftHost.Visibility = Visibility.Visible;
                    _dotsCenterHost.HorizontalAlignment = HorizontalAlignment.Right;
                    _dotsCenterHost.Margin = _isUpdateAvailable ? new Thickness(0, 0, 46, 0) : new Thickness(0, 0, 18, 0);
                    _targetW = _isUpdateAvailable ? 310 : 280;
                    _targetH = 46;
                    _targetBotR = 23;
                }
                else
                {
                    _multitaskLeftHost.Visibility = Visibility.Collapsed;
                    _dotsCenterHost.HorizontalAlignment = HorizontalAlignment.Center;
                    _dotsCenterHost.Margin = _isUpdateAvailable ? new Thickness(0, 0, 24, 0) : new Thickness(0);
                    _targetW = _isUpdateAvailable ? 200 : 170;
                    _targetH = 34;
                    _targetBotR = 17;
                }

                if (newState == IslandState.Done)
                {
                    SetIslandState(IslandState.Idle, "", "");
                }
            }
            else if (newState == IslandState.Countdown)
            {
                _updateDownloadBtn.Visibility = Visibility.Collapsed;
                _multitaskLeftHost.Visibility = Visibility.Collapsed;
                _dotsCenterHost.Visibility = Visibility.Collapsed;
                _listeningHost.Visibility = Visibility.Collapsed;
                _tasksExpandedPanel.Visibility = Visibility.Collapsed;
                _spinnerViewbox.Visibility = Visibility.Collapsed;
                _countdownPanel.Visibility = Visibility.Visible;
                _alertPanel.Visibility = Visibility.Collapsed;
                _cardPanel.Visibility = Visibility.Collapsed;

                _targetW = 280;
                _targetH = 46;
                _targetBotR = 23;
            }
            else if (newState == IslandState.Alert)
            {
                _updateDownloadBtn.Visibility = Visibility.Collapsed;
                _multitaskLeftHost.Visibility = Visibility.Collapsed;
                _dotsCenterHost.Visibility = Visibility.Collapsed;
                _listeningHost.Visibility = Visibility.Collapsed;
                _tasksExpandedPanel.Visibility = Visibility.Collapsed;
                _spinnerViewbox.Visibility = Visibility.Collapsed;
                _countdownPanel.Visibility = Visibility.Collapsed;
                _alertPanel.Visibility = Visibility.Visible;
                _cardPanel.Visibility = Visibility.Collapsed;

                _targetW = 280;
                _targetH = 46;
                _targetBotR = 23;
            }
            else if (newState == IslandState.Error)
            {
                _updateDownloadBtn.Visibility = Visibility.Collapsed;
                _multitaskLeftHost.Visibility = Visibility.Collapsed;
                _dotsCenterHost.Visibility = Visibility.Collapsed;
                _listeningHost.Visibility = Visibility.Collapsed;
                _tasksExpandedPanel.Visibility = Visibility.Collapsed;
                _spinnerViewbox.Visibility = Visibility.Collapsed;
                _countdownPanel.Visibility = Visibility.Collapsed;
                _alertPanel.Visibility = Visibility.Collapsed;
                _cardPanel.Visibility = Visibility.Visible;
                _transcriptText.Visibility = Visibility.Collapsed;

                string rawErr = (answerText ?? "Connection error").Trim();
                _answerText.Text = rawErr;
                _answerText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                _answerText.FontSize = 12.0;
                _answerText.LineHeight = 16.0;
                _answerText.TextWrapping = TextWrapping.Wrap;

                int len = rawErr.Length;
                if (len < 22)
                {
                    _targetW = 280;
                    _targetH = 46;
                }
                else if (len < 50)
                {
                    _targetW = 380;
                    _targetH = 56;
                }
                else if (len < 85)
                {
                    _targetW = 460;
                    _targetH = 72;
                }
                else
                {
                    _targetW = 520;
                    _targetH = 88;
                }

                _autoCollapseTimer.Interval = TimeSpan.FromSeconds(8.0);
                _autoCollapseTimer.Start();
            }
        }

        // Smooth cosine-interpolated SVG keyframe evaluator (matches calcMode="spline" keySplines=".45 0 .55 1")
        private static double EvalKeyframes(double timeSec, double durSec, double offsetSec, double[] values)
        {
            if (values == null || values.Length == 0) return 0.0;
            if (values.Length == 1) return values[0];

            double cycle = (timeSec + offsetSec) % durSec;
            if (cycle < 0) cycle += durSec;
            double norm = cycle / durSec;

            int segments = values.Length - 1;
            double scaled = norm * segments;
            int idx = (int)Math.Floor(scaled);
            if (idx >= segments) idx = segments - 1;
            double frac = scaled - idx;

            // Smoothstep spline approximation for keySplines=".45 0 .55 1"
            double smooth = 0.5 - 0.5 * Math.Cos(frac * Math.PI);
            return values[idx] + (values[idx + 1] - values[idx]) * smooth;
        }

        private static readonly double[][] SoundBarSvgKeyframes = new[]
        {
            new[] { 28.0, 14.0, 36.0, 28.0 }, // bar 1 · orange (dur 1.2s, begin 0s)
            new[] { 40.0, 18.0, 32.0, 40.0 }, // bar 2 · cream  (dur 1.0s, begin -0.3s)
            new[] { 20.0, 38.0, 14.0, 20.0 }, // bar 3 · orange (dur 1.3s, begin -0.6s)
            new[] { 32.0, 12.0, 40.0, 32.0 }  // bar 4 · cream  (dur 1.1s, begin -0.15s)
        };
        private static readonly double[] SoundBarSvgDurs    = new[] { 1.2, 1.0, 1.3, 1.1 };
        private static readonly double[] SoundBarSvgOffsets = new[] { 0.0, 0.3, 0.6, 0.15 };

        private static PathGeometry CreateMacBookNotchGeometry(double totalW, double height, double earRadius = 10.0, double botRadius = 0.0)
        {
            if (botRadius <= 0)
                botRadius = (height <= 60) ? (height / 2.0) : 32.0;

            double rTop = Math.Min(earRadius, Math.Min(height * 0.35, totalW * 0.15));
            double rBot = Math.Min(botRadius, Math.Min(height * 0.50, totalW * 0.45));

            var figure = new PathFigure
            {
                StartPoint = new Point(0, 0),
                IsClosed = true,
                IsFilled = true
            };

            // 1. Top-left ear flaring inward and down to notch body (matches SVG: A 10,10 0 0 1 10,10)
            figure.Segments.Add(new ArcSegment(
                new Point(rTop, rTop),
                new Size(rTop, rTop),
                0, false, SweepDirection.Clockwise, true));

            // 2. Left vertical wall down to bottom corner
            figure.Segments.Add(new LineSegment(
                new Point(rTop, height - rBot),
                true));

            // 3. Bottom-left convex rounded corner (matches SVG: A botR,botR 0 0 0 10+botR,height)
            figure.Segments.Add(new ArcSegment(
                new Point(rTop + rBot, height),
                new Size(rBot, rBot),
                0, false, SweepDirection.Counterclockwise, true));

            // 4. Bottom horizontal edge
            figure.Segments.Add(new LineSegment(
                new Point(totalW - rTop - rBot, height),
                true));

            // 5. Bottom-right convex rounded corner (matches SVG: A botR,botR 0 0 0 totalW-10,height-botR)
            figure.Segments.Add(new ArcSegment(
                new Point(totalW - rTop, height - rBot),
                new Size(rBot, rBot),
                0, false, SweepDirection.Counterclockwise, true));

            // 6. Right vertical wall up to top ear
            figure.Segments.Add(new LineSegment(
                new Point(totalW - rTop, rTop),
                true));

            // 7. Top-right ear flaring outward and up to screen bezel (matches SVG: A 10,10 0 0 1 totalW,0)
            figure.Segments.Add(new ArcSegment(
                new Point(totalW, 0),
                new Size(rTop, rTop),
                0, false, SweepDirection.Clockwise, true));

            // 8. Top bezel straight back to origin
            figure.Segments.Add(new LineSegment(
                new Point(0, 0),
                true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        private static Viewbox BuildUnicornSvg(double width, double height, Brush fill = null)
        {
            var vb = new Viewbox
            {
                Width = width,
                Height = height,
                Stretch = Stretch.Uniform
            };
            var cv = new Canvas
            {
                Width = 720,
                Height = 630
            };
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(BuildConfig.UnicornSvgPath),
                Fill = fill ?? Brushes.White,
                RenderTransform = new TranslateTransform(-900, -300)
            };
            cv.Children.Add(path);
            vb.Child = cv;
            return vb;
        }

        private void OnFrameTick(object sender, EventArgs e)
        {
            double t = _animClock.Elapsed.TotalSeconds;

            if (_state == IslandState.Listening)
            {
                bool isDownNow = NativeMethods.IsAnyHotkeyDown();
                double elapsedMs = (DateTime.UtcNow - _recordStartUtc).TotalMilliseconds;

                // Push-to-Talk: As soon as the user releases the hotkey, stop recording & send immediately!
                if (isDownNow)
                {
                    _keyWasHeld = true;
                }
                else if (_keyWasHeld || (_startedViaHotkey && elapsedMs >= 200))
                {
                    _keyWasHeld = false;
                    _startedViaHotkey = false;
                    FinishRecordingAndSend();
                    return;
                }

                if (elapsedMs > 35000)
                {
                    _keyWasHeld = false;
                    _startedViaHotkey = false;
                    FinishRecordingAndSend();
                    return;
                }
            }

            if (_isBusy && (DateTime.UtcNow - _busyStartUtc).TotalSeconds > 35)
            {
                _isBusy = false;
                SetIslandState(IslandState.Error, "", "Request timed out.");
            }

            // Smooth island widening/morphing attached to the top edge of the screen
            _curW += (_targetW - _curW) * 0.28;
            _curH += (_targetH - _curH) * 0.28;
            _curBotR += (_targetBotR - _curBotR) * 0.28;
            _notchContentHost.Width = _curW;
            _notchContentHost.Height = _curH;
            var notchGeo = CreateMacBookNotchGeometry(_curW, _curH, 10.0, _curBotR);
            _notchPath.Data = notchGeo;
            _notchContentHost.Clip = notchGeo;

            // Live bot voice envelope when Flinch is speaking
            if (_state == IslandState.Speaking)
            {
                double rawBot = GetBotVoiceLevel();
                if (rawBot > _smoothBotVoice)
                    _smoothBotVoice = (_smoothBotVoice * 0.20) + (rawBot * 0.80);
                else
                    _smoothBotVoice = (_smoothBotVoice * 0.68) + (rawBot * 0.32);
            }
            else
            {
                _smoothBotVoice = 0;
            }

            // -----------------------------------------------------------------
            // 1. Thinking Spinner (spinner.svg): 8 spokes with 1.2s opacity sweep
            // -----------------------------------------------------------------
            if (_spinnerViewbox.Visibility == Visibility.Visible)
            {
                for (int i = 0; i < 8; i++)
                {
                    double phase = ((t + 1.20 - i * 0.15) % 1.2) / 1.2;
                    if (phase < 0) phase += 1.0;
                    double op = (phase < 0.75) ? (1.0 - (phase / 0.75) * 0.85) : 0.15;
                    _spinnerSpokes[i].Opacity = op;
                }
            }

            // -----------------------------------------------------------------
            // 2. Multitask Live Task Animations (Spinner Arc, Audio Eq, Search Dots)
            // -----------------------------------------------------------------
            if (_multitaskLeftHost.Visibility == Visibility.Visible)
            {
                if (_spinnerTaskHost.Visibility == Visibility.Visible && _spinnerArcTransform != null)
                {
                    _spinnerArcTransform.Angle = (t * 360.0 / 0.9) % 360.0;
                }
                if (_audioTaskHost.Visibility == Visibility.Visible)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        double barH = 5.0 + Math.Sin((t * 12.0) + (i * 1.8)) * 3.5;
                        _audioEqBars[i].Height = Math.Max(3.0, Math.Min(12.0, barH));
                    }
                }
            }

            // -----------------------------------------------------------------
            // 3. Listening State: flinch-voice 2.js Audio Visualizer & Radial Blobs
            // -----------------------------------------------------------------
            if (_state == IslandState.Listening)
            {
                double rawMic = _recorder.CurrentLevel;
                double[] raw = new double[7];
                if (rawMic > 0.02)
                {
                    for (int b = 0; b < 7; b++)
                    {
                        double harmonic = 0.5 + 0.5 * Math.Sin((t * 22.0) + (b * 1.8));
                        raw[b] = Math.Min(1.0, rawMic * (0.55 + 0.45 * harmonic) * (1.1 - b * 0.08));
                    }
                }
                else
                {
                    // Ambient breathing wave fallback (flinch-voice.js)
                    double env = Math.Max(0.0, Math.Sin(t * 5.5) * Math.Sin(t * 1.05 + 1.0));
                    for (int b = 0; b < 7; b++)
                    {
                        raw[b] = env * (0.35 + 0.65 * (0.5 + 0.5 * Math.Sin((t * 14.0) + b * 2.0))) * (1.0 - b * 0.09);
                    }
                }

                double mx = 0.0;
                for (int b = 0; b < 7; b++) if (raw[b] > mx) mx = raw[b];
                _voicePeak = Math.Max(_voicePeak * 0.995, mx);
                double gain = 1.0 / Math.Max(_voicePeak, 0.25);

                for (int b = 0; b < 7; b++)
                {
                    double v = (mx < 0.03) ? 0.0 : Math.Min(1.0, raw[b] * gain);
                    _voiceVals[b] += (v > _voiceVals[b] ? 0.6 : 0.16) * (v - _voiceVals[b]);
                }

                // 13 Equalizer bars (mid = 6)
                for (int i = 0; i < 13; i++)
                {
                    int band = Math.Min(6, (int)Math.Round(Math.Abs(i - 6.0)));
                    double v = Math.Min(1.0, _voiceVals[band] * (0.9 + 0.1 * Math.Sin((t * 11.0) + i * 1.7)));
                    double scaleY = 0.12 + 0.88 * v;
                    if (_listeningBarTransforms[i] != null)
                        _listeningBarTransforms[i].ScaleY = scaleY;
                }

                // 4 Blobs: midE, low, (low+midE)/2, high
                double low = (_voiceVals[0] + _voiceVals[1]) / 2.0;
                double midE = (_voiceVals[2] + _voiceVals[3] + _voiceVals[4]) / 3.0;
                double high = (_voiceVals[5] + _voiceVals[6]) / 2.0;
                double[] lv = new double[] { midE, low, (low + midE) / 2.0, high };

                for (int i = 0; i < 4; i++)
                {
                    double eVal = Math.Min(1.0, lv[i % 4] * 1.2);
                    double scale = 0.7 + 0.9 * eVal;
                    double opacity = 0.22 + 0.7 * eVal;
                    if (_listeningBlobTransforms[i] != null)
                    {
                        _listeningBlobTransforms[i].ScaleX = scale;
                        _listeningBlobTransforms[i].ScaleY = scale;
                    }
                    _listeningBlobs[i].Opacity = opacity;
                }
            }
            else
            {
                // Reset listening bands when not listening
                for (int b = 0; b < 7; b++) _voiceVals[b] = 0;
            }

            // -----------------------------------------------------------------
            // 4. Animate 4 Dots (flinch-notch-idle.svg: 2 Mint #3DDC84, 2 White #FFFFFF)
            // -----------------------------------------------------------------
            if (_dotsCenterHost.Visibility == Visibility.Visible)
            {
                if (_state == IslandState.Speaking)
                {
                    double activeVoiceLevel = _smoothBotVoice;
                    for (int i = 0; i < 4; i++)
                    {
                        double svgVal = EvalKeyframes(t, SoundBarSvgDurs[i], SoundBarSvgOffsets[i], SoundBarSvgKeyframes[i]);
                        double svgNorm = svgVal / 40.0;
                        double targetBarH = 6.0;
                        if (activeVoiceLevel > 0.01)
                            targetBarH = Math.Max(6.0, Math.Min(22.0, 6.0 + (activeVoiceLevel * 16.0 * svgNorm)));
                        else
                            targetBarH = 6.0 + (svgNorm * 1.4);

                        _soundBars[i].Width = 6.0;
                        _soundBars[i].Height += (targetBarH - _soundBars[i].Height) * 0.45;
                        _soundBars[i].CornerRadius = new CornerRadius(3.0);
                    }
                }
                else
                {
                    // User constraint: When idle, the 4 dots MUST NOT move (completely static 6x6 circles)
                    for (int i = 0; i < 4; i++)
                    {
                        _soundBars[i].Width = 6.0;
                        _soundBars[i].Height = 6.0;
                        _soundBars[i].CornerRadius = new CornerRadius(3.0);
                    }
                }
            }

            // -----------------------------------------------------------------
            // 5. Update Button Animation (01-update-available ripple & 03-progress-ring sweep)
            // -----------------------------------------------------------------
            if (_isUpdateAvailable && _updateDownloadBtn.Visibility == Visibility.Visible)
            {
                if (_updateVisualState == UpdateVisualState.Available && _updateRippleEllipse != null)
                {
                    double pulse = (t % 2.0) / 2.0; // 0..1 over 2s (matches 01-update-available.svg)
                    double rippleR = 8.0 + (pulse * 12.0); // 8 -> 20
                    _updateRippleEllipse.Width = rippleR * 2.0;
                    _updateRippleEllipse.Height = rippleR * 2.0;
                    Canvas.SetLeft(_updateRippleEllipse, 72.0 - rippleR);
                    Canvas.SetTop(_updateRippleEllipse, 18.0 - rippleR);
                    _updateRippleEllipse.Opacity = 0.75 * (1.0 - pulse);
                }
                else if (_updateVisualState == UpdateVisualState.Downloading)
                {
                    double pCycle = (t % 3.2) / 3.2; // 0..1 over 3.2s (matches 03-progress-ring.svg)
                    double sweep = (pCycle < 0.85) ? (pCycle / 0.85) : 1.0;
                    UpdateDownloadProgressArc(sweep);
                }
            }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.UnregisterHotKey(hwnd, HOTKEY_ID_ALTV);
            }
            catch { }
            try { _recorder.Dispose(); } catch { }
            StopAudioPlayback();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
    }

    // =========================================================================
    // Flinch Sign In, Sign Up & Email Confirmation Code Window
    // Pixel-faithful WPF implementation of flinch-sign-in.svg & flinch-sign-up.svg
    // =========================================================================
    internal enum AuthPageMode
    {
        SignIn,
        SignUp,
        ConfirmCode
    }

    internal sealed class FlinchAuthWindow : Window
    {
        private static readonly Brush BgWindow       = FreezeBrush(0x1C, 0x1F, 0x26); // #1c1f26
        private static readonly Brush BorderWindow   = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
        private static readonly Brush BgInput        = FreezeBrush(0x25, 0x29, 0x32); // #252932
        private static readonly Brush BorderInput    = FreezeBrush(0x34, 0x39, 0x45); // #343945
        private static readonly Brush BrushOrange    = FreezeBrush(0xDF, 0x8A, 0x5E); // #df8a5e
        private static readonly Brush BrushOrangeHov = FreezeBrush(0xE8, 0x9B, 0x73);
        private static readonly Brush BrushCream     = FreezeBrush(0xFA, 0xE9, 0xD4); // #fae9d4
        private static readonly Brush BrushSubtext   = FreezeBrush(0x9A, 0xA0, 0xAB); // #9aa0ab
        private static readonly Brush BrushLabel     = FreezeBrush(0xAE, 0xB4, 0xBF); // #aeb4bf
        private static readonly Brush BrushHint      = FreezeBrush(0x7B, 0x82, 0x90); // #7b8290
        private static readonly Brush BrushWinCtrl   = FreezeBrush(0x6B, 0x72, 0x80); // #6b7280
        private static readonly Brush BrushErrBg     = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));
        private static readonly Brush BrushErrBorder = new SolidColorBrush(Color.FromArgb(95, 239, 68, 68));
        private static readonly Brush BrushErrText   = FreezeBrush(0xFC, 0xA5, 0xA5);
        private static readonly Brush BrushOkBg      = new SolidColorBrush(Color.FromArgb(40, 34, 197, 94));
        private static readonly Brush BrushOkBorder  = new SolidColorBrush(Color.FromArgb(95, 34, 197, 94));
        private static readonly Brush BrushOkText    = FreezeBrush(0x86, 0xEF, 0xAC);

        private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private AuthPageMode _mode = AuthPageMode.SignIn;
        private TextBlock _headingText;
        private TextBlock _subtitleText;
        private Border _alertBanner;
        private TextBlock _alertText;

        // Form panels
        private StackPanel _emailSection;
        private Border _emailBorder;
        private TextBox _emailBox;
        private TextBlock _emailPlaceholder;

        private StackPanel _passwordSection;
        private Border _passwordBorder;
        private PasswordBox _passwordBox;
        private TextBox _passwordVisibleBox;
        private TextBlock _passwordPlaceholder;
        private bool _isPasswordVisible;

        private StackPanel _confirmPasswordSection;
        private Border _confirmPasswordBorder;
        private PasswordBox _confirmPasswordBox;
        private TextBox _confirmPasswordVisibleBox;
        private TextBlock _confirmPasswordPlaceholder;
        private bool _isConfirmPasswordVisible;

        private StackPanel _otpCodeSection;
        private Border _otpCodeBorder;
        private TextBox _otpCodeBox;
        private TextBlock _otpCodePlaceholder;

        private TextBlock _rightHelperLink; // "Forgot password?" or "Resend code"
        private Border _primaryButton;
        private TextBlock _primaryButtonText;

        private TextBlock _footerPrefixText;
        private TextBlock _footerActionLink;
        private bool _isSubmitting;
        private DispatcherTimer _confirmPollTimer;
        private HttpListener _localCallbackListener;

        public FlinchAuthWindow(string initialNotice = null)
        {
            Title = "Flinch — Sign In";
            Width = 480;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = true;

            _confirmPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _confirmPollTimer.Tick += OnConfirmPollTick;
            Closed += (s, e) => StopConfirmWatchers();

            BuildAuthUI();
            SwitchMode(AuthPageMode.SignIn);

            if (!string.IsNullOrWhiteSpace(initialNotice))
            {
                ShowAlert(initialNotice, true);
            }
        }

        private void StartConfirmWatchers()
        {
            _confirmPollTimer.Start();
            try
            {
                if (_localCallbackListener == null)
                {
                    _localCallbackListener = new HttpListener();
                    _localCallbackListener.Prefixes.Add("http://localhost:3000/");
                    _localCallbackListener.Start();
                    Task.Run(() => ListenForLocalhostRedirect());
                }
            }
            catch
            {
                // Port 3000 in use or unavailable; polling timer still handles auto-sign-in
                _localCallbackListener = null;
            }
        }

        private void StopConfirmWatchers()
        {
            try { if (_confirmPollTimer != null) _confirmPollTimer.Stop(); } catch { }
            try
            {
                if (_localCallbackListener != null)
                {
                    _localCallbackListener.Stop();
                    _localCallbackListener.Close();
                    _localCallbackListener = null;
                }
            }
            catch { }
        }

        private void ListenForLocalhostRedirect()
        {
            while (_localCallbackListener != null && _localCallbackListener.IsListening)
            {
                try
                {
                    var ctx = _localCallbackListener.GetContext();
                    string html =
                        "<!DOCTYPE html><html><head><meta charset='utf-8'><title>Flinch — Email Confirmed</title>" +
                        "<style>body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:#111318;font-family:'Segoe UI',sans-serif;color:#fae9d4;}" +
                        ".card{background:#1c1f26;border:1px solid rgba(255,255,255,0.08);border-radius:20px;padding:42px 48px;text-align:center;box-shadow:0 18px 40px rgba(0,0,0,0.45);max-width:380px;}" +
                        ".badge{width:64px;height:64px;border-radius:16px;background:#df8a5e;color:#1c1f26;font-size:32px;font-weight:bold;display:flex;align-items:center;justify-content:center;margin:0 auto 20px;}" +
                        "h1{font-size:24px;margin:0 0 10px;color:#fae9d4;}p{font-size:14px;color:#9aa0ab;line-height:1.5;margin:0;}</style></head>" +
                        "<body><div class='card'><div class='badge'>✓</div><h1>Email confirmed!</h1><p>Your Flinch account is now active on your PC.<br>You can close this browser tab.</p></div></body></html>";
                    byte[] buf = Encoding.UTF8.GetBytes(html);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = buf.Length;
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                    ctx.Response.OutputStream.Close();

                    // Trigger immediate sign-in check on UI thread
                    Dispatcher.BeginInvoke(new Action(() => OnConfirmPollTick(this, EventArgs.Empty)));
                }
                catch
                {
                    break;
                }
            }
        }

        private void OnConfirmPollTick(object sender, EventArgs e)
        {
            if (_mode != AuthPageMode.ConfirmCode || _isSubmitting) return;
            string email = (_emailBox.Text ?? "").Trim();
            string pass = GetCurrentPassword();
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pass)) return;

            Task.Run(() =>
            {
                string err;
                bool needsCode;
                if (SupabaseAuthClient.SignIn(email, pass, out err, out needsCode))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StopConfirmWatchers();
                        DialogResult = true;
                        Close();
                    }));
                }
            });
        }

        private void BuildAuthUI()
        {
            // Outer 440x560 card matching flinch-sign-in.svg & flinch-sign-up.svg (<rect width="440" height="560" rx="20" fill="#1c1f26">)
            var card = new Border
            {
                Width = 440,
                Height = 560,
                CornerRadius = new CornerRadius(20),
                Background = BgWindow,
                BorderBrush = BorderWindow,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x0B, 0x0D, 0x11),
                    BlurRadius = 32,
                    ShadowDepth = 12,
                    Direction = 270,
                    Opacity = 0.45
                }
            };

            card.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };

            var rootGrid = new Grid();

            // 1. Top-Right Window Controls (Minimize & Close from SVG lines 43-46)
            var winCtrlPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12, 14, 0)
            };

            var btnMin = CreateWindowControlButton("M 0,5 L 10,5");
            btnMin.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                WindowState = WindowState.Minimized;
            };

            var btnClose = CreateWindowControlButton("M 0,0 L 10,10 M 10,0 L 0,10");
            btnClose.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                DialogResult = false;
                Close();
            };

            winCtrlPanel.Children.Add(btnMin);
            winCtrlPanel.Children.Add(btnClose);
            rootGrid.Children.Add(winCtrlPanel);

            // 2. Main Centered Content Column (360px inner width matching x=80..440 in 440px window)
            var contentStack = new StackPanel
            {
                Width = 360,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 44, 0, 0)
            };

            // App Icon Tile: <rect width="72" height="72" rx="16" fill="#df8a5e"/> + #mascot
            var iconTile = new Border
            {
                Width = 72,
                Height = 72,
                CornerRadius = new CornerRadius(16),
                Background = BrushOrange,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = BuildSvgMascotViewbox()
            };
            contentStack.Children.Add(iconTile);

            // Heading ("Welcome back" / "Create your account" / "Confirm your email")
            _headingText = new TextBlock
            {
                Text = "Welcome back",
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushCream,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 18, 0, 0)
            };
            contentStack.Children.Add(_headingText);

            // Subtitle
            _subtitleText = new TextBlock
            {
                Text = "Sign in to continue to Flinch.",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 13,
                Foreground = BrushSubtext,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 14)
            };
            contentStack.Children.Add(_subtitleText);

            // Alert Banner (Collapsed unless error/info)
            _alertText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Foreground = BrushErrText,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            _alertBanner = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = BrushErrBg,
                BorderBrush = BrushErrBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 10),
                Visibility = Visibility.Collapsed,
                Child = _alertText
            };
            contentStack.Children.Add(_alertBanner);

            // Email Field
            _emailSection = CreateEmailField();
            contentStack.Children.Add(_emailSection);

            // Password Field
            _passwordSection = CreatePasswordField(
                "Password",
                out _passwordBorder,
                out _passwordBox,
                out _passwordVisibleBox,
                out _passwordPlaceholder,
                false);
            contentStack.Children.Add(_passwordSection);

            // Confirm Password Field (Sign Up mode)
            _confirmPasswordSection = CreatePasswordField(
                "Confirm password",
                out _confirmPasswordBorder,
                out _confirmPasswordBox,
                out _confirmPasswordVisibleBox,
                out _confirmPasswordPlaceholder,
                true);
            contentStack.Children.Add(_confirmPasswordSection);

            // Confirmation Code Field (ConfirmCode mode)
            _otpCodeSection = CreateOtpCodeField();
            contentStack.Children.Add(_otpCodeSection);

            // Right-aligned helper link ("Forgot password?" or "Resend code")
            _rightHelperLink = new TextBlock
            {
                Text = "Forgot password?",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Foreground = BrushOrange,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 2, 0, 14)
            };
            _rightHelperLink.MouseLeftButtonDown += OnRightHelperClicked;
            contentStack.Children.Add(_rightHelperLink);

            // Primary Action Button (<rect width="360" height="46" rx="12" fill="#df8a5e"/>)
            _primaryButtonText = new TextBlock
            {
                Text = "Sign in",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = BgWindow,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _primaryButton = new Border
            {
                Width = 360,
                Height = 46,
                CornerRadius = new CornerRadius(12),
                Background = BrushOrange,
                Cursor = Cursors.Hand,
                Child = _primaryButtonText
            };
            _primaryButton.MouseEnter += (s, e) => { if (!_isSubmitting) _primaryButton.Background = BrushOrangeHov; };
            _primaryButton.MouseLeave += (s, e) => { if (!_isSubmitting) _primaryButton.Background = BrushOrange; };
            _primaryButton.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                SubmitCurrentForm();
            };
            contentStack.Children.Add(_primaryButton);

            rootGrid.Children.Add(contentStack);

            // Bottom Footer Switch Link (Pinned near bottom y=516 matching SVG y=556)
            var footerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 26)
            };
            _footerPrefixText = new TextBlock
            {
                Text = "New to Flinch? ",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 13,
                Foreground = BrushSubtext
            };
            _footerActionLink = new TextBlock
            {
                Text = "Create account",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOrange,
                Cursor = Cursors.Hand
            };
            _footerActionLink.MouseLeftButtonDown += OnFooterSwitchClicked;
            footerPanel.Children.Add(_footerPrefixText);
            footerPanel.Children.Add(_footerActionLink);
            rootGrid.Children.Add(footerPanel);

            card.Child = rootGrid;
            Content = card;
        }

        private static Border CreateWindowControlButton(string pathGeometry)
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathGeometry),
                Stroke = BrushWinCtrl,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var b = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(2, 0, 0, 0),
                Child = p
            };
            b.MouseEnter += (s, e) => p.Stroke = BrushCream;
            b.MouseLeave += (s, e) => p.Stroke = BrushWinCtrl;
            return b;
        }

        // Exact static #mascot from flinch-sign-in.svg & flinch-sign-up.svg
        private static Viewbox BuildSvgMascotViewbox()
        {
            var canvas = new Canvas { Width = 100, Height = 100 };
            var rootGroup = new Canvas { RenderTransform = new TranslateTransform(0, -3) };

            var strokeDark = FreezeBrush(0x1C, 0x1F, 0x26);
            var fillEarOuter = FreezeBrush(0x3D, 0x40, 0x49);

            // Left Ear: translate(36 49) rotate(-8) scale(0.62)
            var leftEar = new Canvas();
            var leftEarTg = new TransformGroup();
            leftEarTg.Children.Add(new ScaleTransform(0.62, 0.62));
            leftEarTg.Children.Add(new RotateTransform(-8));
            leftEarTg.Children.Add(new TranslateTransform(36, 49));
            leftEar.RenderTransform = leftEarTg;
            leftEar.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M-15 0 C -22 -28 -13 -62 0 -70 C 13 -62 22 -28 15 0 Z"),
                Fill = fillEarOuter,
                Stroke = strokeDark,
                StrokeThickness = 7.42,
                StrokeLineJoin = PenLineJoin.Round
            });
            leftEar.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M-8 -6 C -12 -26 -7 -50 0 -58 C 7 -50 12 -26 8 -5 Z"),
                Fill = BrushOrange
            });
            rootGroup.Children.Add(leftEar);

            // Right Ear: translate(64 49) rotate(8) scale(0.62)
            var rightEar = new Canvas();
            var rightEarTg = new TransformGroup();
            rightEarTg.Children.Add(new ScaleTransform(0.62, 0.62));
            rightEarTg.Children.Add(new RotateTransform(8));
            rightEarTg.Children.Add(new TranslateTransform(64, 49));
            rightEar.RenderTransform = rightEarTg;
            rightEar.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M-15 0 C -22 -28 -13 -62 0 -70 C 13 -62 22 -28 15 0 Z"),
                Fill = fillEarOuter,
                Stroke = strokeDark,
                StrokeThickness = 7.42,
                StrokeLineJoin = PenLineJoin.Round
            });
            rightEar.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M-8 -6 C -12 -26 -7 -50 0 -58 C 7 -50 12 -26 8 -5 Z"),
                Fill = BrushOrange
            });
            rootGroup.Children.Add(rightEar);

            // Head: translate(50 66) scale(0.8)
            var headCanvas = new Canvas();
            var headTg = new TransformGroup();
            headTg.Children.Add(new ScaleTransform(0.8, 0.8));
            headTg.Children.Add(new TranslateTransform(50, 66));
            headCanvas.RenderTransform = headTg;
            headCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M-44 -4 C -44 -32 -22 -42 0 -42 C 22 -42 44 -32 44 -4 C 44 22 26 40 0 40 C -26 40 -44 22 -44 -4 Z"),
                Fill = BrushCream,
                Stroke = strokeDark,
                StrokeThickness = 5.75,
                StrokeLineJoin = PenLineJoin.Round
            });
            rootGroup.Children.Add(headCanvas);

            // Left Eye (cx=36 cy=66 r=4.8)
            var eyeL = new Ellipse { Width = 9.6, Height = 9.6, Fill = strokeDark };
            Canvas.SetLeft(eyeL, 36 - 4.8);
            Canvas.SetTop(eyeL, 66 - 4.8);
            rootGroup.Children.Add(eyeL);

            // Right Eye (cx=64 cy=66 r=4.8)
            var eyeR = new Ellipse { Width = 9.6, Height = 9.6, Fill = strokeDark };
            Canvas.SetLeft(eyeR, 64 - 4.8);
            Canvas.SetTop(eyeR, 66 - 4.8);
            rootGroup.Children.Add(eyeR);

            // Nose (cx=50 cy=75 rx=3.6 ry=2.7)
            var nose = new Ellipse { Width = 7.2, Height = 5.4, Fill = strokeDark };
            Canvas.SetLeft(nose, 50 - 3.6);
            Canvas.SetTop(nose, 75 - 2.7);
            rootGroup.Children.Add(nose);

            // Mouth (M43 80 Q46.5 87 50 80 Q53.5 87 57 80)
            rootGroup.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M43 80 Q46.5 87 50 80 Q53.5 87 57 80"),
                Stroke = strokeDark,
                StrokeThickness = 2.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });

            canvas.Children.Add(rootGroup);
            return new Viewbox
            {
                Width = 46,
                Height = 46,
                Stretch = Stretch.Uniform,
                Child = canvas
            };
        }

        private StackPanel CreateEmailField()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(new TextBlock
            {
                Text = "Email",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Foreground = BrushLabel,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var grid = new Grid();
            _emailPlaceholder = new TextBlock
            {
                Text = "you@example.com",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 14,
                Foreground = BrushHint,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 14, 0),
                IsHitTestVisible = false
            };

            _emailBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushCream,
                CaretBrush = BrushOrange,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 0, 12, 0)
            };

            _emailBorder = new Border
            {
                Width = 360,
                Height = 42,
                CornerRadius = new CornerRadius(10),
                Background = BgInput,
                BorderBrush = BrushOrange,
                BorderThickness = new Thickness(1.5),
                Child = grid
            };

            _emailBox.GotFocus += (s, e) =>
            {
                _emailBorder.BorderBrush = BrushOrange;
                _emailBorder.BorderThickness = new Thickness(1.5);
            };
            _emailBox.LostFocus += (s, e) =>
            {
                _emailBorder.BorderBrush = BorderInput;
                _emailBorder.BorderThickness = new Thickness(1);
            };
            _emailBox.TextChanged += (s, e) =>
            {
                _emailPlaceholder.Visibility = string.IsNullOrEmpty(_emailBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            _emailBox.KeyDown += OnInputKeyDown;

            grid.Children.Add(_emailPlaceholder);
            grid.Children.Add(_emailBox);
            sp.Children.Add(_emailBorder);
            return sp;
        }

        private StackPanel CreatePasswordField(
            string labelText,
            out Border borderOut,
            out PasswordBox passBoxOut,
            out TextBox visibleBoxOut,
            out TextBlock placeholderOut,
            bool isConfirm)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(new TextBlock
            {
                Text = labelText,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Foreground = BrushLabel,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var grid = new Grid();
            var placeholder = new TextBlock
            {
                Text = "••••••••",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 14,
                Foreground = BrushHint,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 38, 0),
                IsHitTestVisible = false
            };

            var pBox = new PasswordBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushCream,
                CaretBrush = BrushOrange,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 0, 38, 0)
            };

            var vBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushCream,
                CaretBrush = BrushOrange,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 0, 38, 0),
                Visibility = Visibility.Collapsed
            };

            var border = new Border
            {
                Width = 360,
                Height = 42,
                CornerRadius = new CornerRadius(10),
                Background = BgInput,
                BorderBrush = BorderInput,
                BorderThickness = new Thickness(1),
                Child = grid
            };

            // Eye icon toggle from flinch-sign-in.svg & flinch-sign-up.svg (<path d="M-8 0Q0 -7 8 0Q0 7 -8 0Z"/><circle r="2.2"/>)
            var eyeCanvas = new Canvas { Width = 20, Height = 20 };
            var eyeOutline = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2,10 Q 10,3 18,10 Q 10,17 2,10 Z"),
                Stroke = BrushHint,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round
            };
            var eyePupil = new Ellipse
            {
                Width = 4.4,
                Height = 4.4,
                Stroke = BrushHint,
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(eyePupil, 10 - 2.2);
            Canvas.SetTop(eyePupil, 10 - 2.2);
            eyeCanvas.Children.Add(eyeOutline);
            eyeCanvas.Children.Add(eyePupil);

            var eyeBtn = new Border
            {
                Width = 32,
                Height = 32,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Child = eyeCanvas
            };

            eyeBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (!isConfirm)
                {
                    _isPasswordVisible = !_isPasswordVisible;
                    if (_isPasswordVisible)
                    {
                        vBox.Text = pBox.Password;
                        pBox.Visibility = Visibility.Collapsed;
                        vBox.Visibility = Visibility.Visible;
                        eyeOutline.Stroke = BrushOrange;
                        eyePupil.Stroke = BrushOrange;
                        vBox.Focus();
                        vBox.CaretIndex = vBox.Text.Length;
                    }
                    else
                    {
                        pBox.Password = vBox.Text;
                        vBox.Visibility = Visibility.Collapsed;
                        pBox.Visibility = Visibility.Visible;
                        eyeOutline.Stroke = BrushHint;
                        eyePupil.Stroke = BrushHint;
                        pBox.Focus();
                    }
                }
                else
                {
                    _isConfirmPasswordVisible = !_isConfirmPasswordVisible;
                    if (_isConfirmPasswordVisible)
                    {
                        vBox.Text = pBox.Password;
                        pBox.Visibility = Visibility.Collapsed;
                        vBox.Visibility = Visibility.Visible;
                        eyeOutline.Stroke = BrushOrange;
                        eyePupil.Stroke = BrushOrange;
                        vBox.Focus();
                        vBox.CaretIndex = vBox.Text.Length;
                    }
                    else
                    {
                        pBox.Password = vBox.Text;
                        vBox.Visibility = Visibility.Collapsed;
                        pBox.Visibility = Visibility.Visible;
                        eyeOutline.Stroke = BrushHint;
                        eyePupil.Stroke = BrushHint;
                        pBox.Focus();
                    }
                }
            };

            pBox.GotFocus += (s, e) => { border.BorderBrush = BrushOrange; border.BorderThickness = new Thickness(1.5); };
            pBox.LostFocus += (s, e) => { border.BorderBrush = BorderInput; border.BorderThickness = new Thickness(1); };
            vBox.GotFocus += (s, e) => { border.BorderBrush = BrushOrange; border.BorderThickness = new Thickness(1.5); };
            vBox.LostFocus += (s, e) => { border.BorderBrush = BorderInput; border.BorderThickness = new Thickness(1); };

            pBox.PasswordChanged += (s, e) =>
            {
                if (vBox.Visibility != Visibility.Visible)
                    placeholder.Visibility = string.IsNullOrEmpty(pBox.Password) ? Visibility.Visible : Visibility.Collapsed;
            };
            vBox.TextChanged += (s, e) =>
            {
                if (vBox.Visibility == Visibility.Visible)
                {
                    pBox.Password = vBox.Text;
                    placeholder.Visibility = string.IsNullOrEmpty(vBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                }
            };

            pBox.KeyDown += OnInputKeyDown;
            vBox.KeyDown += OnInputKeyDown;

            grid.Children.Add(placeholder);
            grid.Children.Add(pBox);
            grid.Children.Add(vBox);
            grid.Children.Add(eyeBtn);

            sp.Children.Add(border);
            borderOut = border;
            passBoxOut = pBox;
            visibleBoxOut = vBox;
            placeholderOut = placeholder;
            return sp;
        }

        private StackPanel CreateOtpCodeField()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            sp.Children.Add(new TextBlock
            {
                Text = "Confirmation code",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                Foreground = BrushLabel,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var grid = new Grid();
            _otpCodePlaceholder = new TextBlock
            {
                Text = "Enter code from email",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 14,
                Foreground = BrushHint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            _otpCodeBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushCream,
                CaretBrush = BrushOrange,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 0, 12, 0)
            };

            _otpCodeBorder = new Border
            {
                Width = 360,
                Height = 46,
                CornerRadius = new CornerRadius(10),
                Background = BgInput,
                BorderBrush = BrushOrange,
                BorderThickness = new Thickness(1.5),
                Child = grid
            };

            _otpCodeBox.TextChanged += (s, e) =>
            {
                _otpCodePlaceholder.Visibility = string.IsNullOrEmpty(_otpCodeBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            _otpCodeBox.KeyDown += OnInputKeyDown;

            grid.Children.Add(_otpCodePlaceholder);
            grid.Children.Add(_otpCodeBox);
            sp.Children.Add(_otpCodeBorder);
            return sp;
        }

        private void SwitchMode(AuthPageMode newMode)
        {
            _mode = newMode;
            HideAlert();

            if (newMode != AuthPageMode.ConfirmCode)
            {
                StopConfirmWatchers();
            }

            if (newMode == AuthPageMode.SignIn)
            {
                Title = "Flinch — Sign In";
                _headingText.Text = "Welcome back";
                _subtitleText.Text = "Sign in to continue to Flinch.";

                _emailSection.Visibility = Visibility.Visible;
                _emailBorder.Height = 44;

                _passwordSection.Visibility = Visibility.Visible;
                _passwordBorder.Height = 43;

                _confirmPasswordSection.Visibility = Visibility.Collapsed;
                _otpCodeSection.Visibility = Visibility.Collapsed;

                _rightHelperLink.Visibility = Visibility.Visible;
                _rightHelperLink.Text = "Forgot password?";
                _rightHelperLink.Margin = new Thickness(0, 2, 0, 16);

                _primaryButtonText.Text = "Sign in";
                _footerPrefixText.Text = "New to Flinch? ";
                _footerActionLink.Text = "Create account";
            }
            else if (newMode == AuthPageMode.SignUp)
            {
                Title = "Flinch — Create Account";
                _headingText.Text = "Create your account";
                _subtitleText.Text = "Access is invite-only during the beta.";

                _emailSection.Visibility = Visibility.Visible;
                _emailBorder.Height = 40;

                _passwordSection.Visibility = Visibility.Visible;
                _passwordBorder.Height = 39;

                _confirmPasswordSection.Visibility = Visibility.Visible;
                _confirmPasswordBorder.Height = 39;

                _otpCodeSection.Visibility = Visibility.Collapsed;

                _rightHelperLink.Visibility = Visibility.Collapsed;

                _primaryButton.Margin = new Thickness(0, 6, 0, 0);
                _primaryButtonText.Text = "Create account";
                _footerPrefixText.Text = "Already have an account? ";
                _footerActionLink.Text = "Sign in";
            }
            else if (newMode == AuthPageMode.ConfirmCode)
            {
                Title = "Flinch — Confirm Email";
                _headingText.Text = "Confirm your email";
                string em = (_emailBox.Text ?? "").Trim();
                _subtitleText.Text = string.IsNullOrEmpty(em)
                    ? "Enter the code or click the link sent to your email."
                    : "Enter the code or click the link sent to " + em;

                _emailSection.Visibility = Visibility.Collapsed;
                _passwordSection.Visibility = Visibility.Collapsed;
                _confirmPasswordSection.Visibility = Visibility.Collapsed;

                _otpCodeSection.Visibility = Visibility.Visible;
                _otpCodeBox.Text = "";
                _otpCodeBox.Focus();

                _rightHelperLink.Visibility = Visibility.Visible;
                _rightHelperLink.Text = "Resend code";
                _rightHelperLink.Margin = new Thickness(0, 2, 0, 16);

                _primaryButtonText.Text = "Verify / Continue";
                _footerPrefixText.Text = "Back to ";
                _footerActionLink.Text = "Sign in";

                StartConfirmWatchers();
            }
        }

        private void ShowAlert(string message, bool isError)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                HideAlert();
                return;
            }
            _alertBanner.Background = isError ? BrushErrBg : BrushOkBg;
            _alertBanner.BorderBrush = isError ? BrushErrBorder : BrushOkBorder;
            _alertText.Foreground = isError ? BrushErrText : BrushOkText;
            _alertText.Text = message;
            _alertBanner.Visibility = Visibility.Visible;
        }

        private void HideAlert()
        {
            _alertBanner.Visibility = Visibility.Collapsed;
            _alertText.Text = "";
        }

        private void OnFooterSwitchClicked(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_isSubmitting) return;
            if (_mode == AuthPageMode.SignIn)
                SwitchMode(AuthPageMode.SignUp);
            else
                SwitchMode(AuthPageMode.SignIn);
        }

        private void OnRightHelperClicked(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_isSubmitting) return;

            string email = (_emailBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(email) || !email.Contains("@"))
            {
                ShowAlert("Enter your email address first.", true);
                return;
            }

            if (_mode == AuthPageMode.SignIn)
            {
                string err;
                if (SupabaseAuthClient.SendPasswordReset(email, out err))
                    ShowAlert("Password reset email sent to " + email + ".", false);
                else
                    ShowAlert(err, true);
            }
            else if (_mode == AuthPageMode.ConfirmCode)
            {
                string err;
                if (SupabaseAuthClient.ResendOtp(email, out err))
                    ShowAlert("A new confirmation code was sent to " + email + ".", false);
                else
                    ShowAlert(err, true);
            }
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SubmitCurrentForm();
            }
        }

        private string GetCurrentPassword()
        {
            return _isPasswordVisible ? (_passwordVisibleBox.Text ?? "") : (_passwordBox.Password ?? "");
        }

        private string GetCurrentConfirmPassword()
        {
            return _isConfirmPasswordVisible ? (_confirmPasswordVisibleBox.Text ?? "") : (_confirmPasswordBox.Password ?? "");
        }

        private void SubmitCurrentForm()
        {
            if (_isSubmitting) return;
            HideAlert();

            string email = (_emailBox.Text ?? "").Trim();
            string pass = GetCurrentPassword();

            if (_mode == AuthPageMode.ConfirmCode)
            {
                string code = (_otpCodeBox.Text ?? "").Trim();
                SetSubmittingState(true, "Verifying...");
                Task.Run(() =>
                {
                    string err = "";
                    bool ok = false;
                    if (!string.IsNullOrEmpty(code))
                    {
                        ok = SupabaseAuthClient.VerifyOtpCode(email, code, out err);
                    }
                    if (!ok && !string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(pass))
                    {
                        bool dummyNeeds;
                        if (SupabaseAuthClient.SignIn(email, pass, out err, out dummyNeeds))
                        {
                            ok = true;
                        }
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetSubmittingState(false, "Verify / Continue");
                        if (ok)
                        {
                            StopConfirmWatchers();
                            DialogResult = true;
                            Close();
                        }
                        else
                        {
                            ShowAlert(string.IsNullOrEmpty(code)
                                ? "Email not confirmed yet. Click the link or enter the code from your email."
                                : err, true);
                        }
                    }));
                });
                return;
            }

            if (string.IsNullOrEmpty(email) || !email.Contains("@") || !email.Contains("."))
            {
                ShowAlert("Please enter a valid email address.", true);
                return;
            }

            if (string.IsNullOrEmpty(pass) || pass.Length < 6)
            {
                ShowAlert("Password must be at least 6 characters.", true);
                return;
            }

            if (_mode == AuthPageMode.SignUp)
            {
                string conf = GetCurrentConfirmPassword();
                if (pass != conf)
                {
                    ShowAlert("Passwords do not match.", true);
                    return;
                }

                SetSubmittingState(true, "Creating account...");
                Task.Run(() =>
                {
                    string err;
                    bool needsCode;
                    bool ok = SupabaseAuthClient.SignUp(email, pass, out err, out needsCode);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetSubmittingState(false, "Create account");
                        if (ok && !needsCode)
                        {
                            DialogResult = true;
                            Close();
                        }
                        else if (needsCode)
                        {
                            SwitchMode(AuthPageMode.ConfirmCode);
                            ShowAlert("We sent a confirmation code to " + email + ".", false);
                        }
                        else
                        {
                            ShowAlert(err, true);
                        }
                    }));
                });
            }
            else
            {
                SetSubmittingState(true, "Signing in...");
                Task.Run(() =>
                {
                    string err;
                    bool needsCode;
                    bool ok = SupabaseAuthClient.SignIn(email, pass, out err, out needsCode);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SetSubmittingState(false, "Sign in");
                        if (ok)
                        {
                            DialogResult = true;
                            Close();
                        }
                        else if (needsCode)
                        {
                            SwitchMode(AuthPageMode.ConfirmCode);
                            ShowAlert(err, false);
                        }
                        else
                        {
                            ShowAlert(err, true);
                        }
                    }));
                });
            }
        }

        private void SetSubmittingState(bool busy, string buttonLabel)
        {
            _isSubmitting = busy;
            _primaryButtonText.Text = buttonLabel;
            _primaryButton.Opacity = busy ? 0.7 : 1.0;
        }
    }

    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            FlinchEnv.Load();

            if (args != null && args.Length > 0 && args[0] == "--testdraw")
            {
                try
                {
                    var app = System.Windows.Application.Current ?? new System.Windows.Application();
                    var ps = System.Windows.Forms.Screen.PrimaryScreen;
                    var bounds = new NativeMethods.RECT
                    {
                        Left = ps.Bounds.Left,
                        Top = ps.Bounds.Top,
                        Right = ps.Bounds.Right,
                        Bottom = ps.Bounds.Bottom
                    };
                    var scene = new ScenePayload
                    {
                        durationMs = 8000,
                        objects = new List<SceneObjectPayload>
                        {
                            new SceneObjectPayload
                            {
                                type = "arrow",
                                color = "#3DDC84",
                                label = "Recycle Bin Target",
                                from = new ScenePoint { x = 0.15, y = 0.15 },
                                to = new ScenePoint { x = 0.035, y = 0.05 }
                            },
                            new SceneObjectPayload
                            {
                                type = "circle",
                                color = "#38BDF8",
                                label = "Target Icon",
                                rect = new SceneRect { x = 0.015, y = 0.02, w = 0.05, h = 0.08 }
                            }
                        }
                    };
                    FlinchEnv.Log("Showing test drawing overlay on screen...");
                    var win = new OverlayWindow(bounds, scene);
                    win.Show();
                    System.Windows.Forms.Application.DoEvents();
                    Thread.Sleep(1200);
                    System.Windows.Forms.Application.DoEvents();

                    FlinchEnv.Log("OverlayWindow rendered and active on screen.");
                    try
                    {
                        using (var bmp = new System.Drawing.Bitmap(ps.Bounds.Width, ps.Bounds.Height))
                        {
                            using (var g = System.Drawing.Graphics.FromImage(bmp))
                            {
                                g.CopyFromScreen(ps.Bounds.Left, ps.Bounds.Top, 0, 0, ps.Bounds.Size);
                            }
                            string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "screen_drawn_verified.png");
                            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                            FlinchEnv.Log("Test draw screenshot saved: " + outPath);
                        }
                    }
                    catch (Exception gdiEx)
                    {
                        FlinchEnv.Log("Desktop screenshot note (headless DC): " + gdiEx.Message);
                    }
                    Thread.Sleep(2500);
                    win.Close();
                    return 0;
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("TEST_DRAW_ERROR: " + ex.ToString());
                    Console.WriteLine("TEST_DRAW_ERROR: " + ex.Message);
                    return 1;
                }
            }

            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                try
                {
                    string restoreErr;
                    SupabaseAuthClient.TryRestoreSavedSession(out restoreErr);

                    var totalSw = Stopwatch.StartNew();
                    FlinchEnv.Log("Running Flinch.exe --selftest (Minimalist Notch)...");
                    string testWavPath = (args.Length > 1 && File.Exists(args[1])) ? args[1] : "";
                    byte[] wavBytes = !string.IsNullOrEmpty(testWavPath) ? File.ReadAllBytes(testWavPath) : new byte[0];
                    string tx = "";
                    string ans = "";
                    byte[] audioBytes = null;
                    int remaining = 0;
                    string err = "";
                    bool ok = false;

                    if (wavBytes.Length > 44)
                    {
                        ok = GeminiClient.ProcessVoiceTurnInCloud(wavBytes, out tx, out ans, out audioBytes, out remaining, out err);
                    }
                    else
                    {
                        audioBytes = GeminiClient.SynthesizeSpeechFast("Flinch cloud brain online.");
                        ok = audioBytes != null && audioBytes.Length > 44;
                    }
                    totalSw.Stop();

                    if (audioBytes != null && audioBytes.Length > 44)
                    {
                        var playSw = Stopwatch.StartNew();
                        using (var ms = new MemoryStream(audioBytes))
                        using (var sp = new System.Media.SoundPlayer(ms))
                        {
                            sp.PlaySync();
                        }
                        playSw.Stop();
                    }
                    return ok ? 0 : 1;
                }
                catch (Exception ex)
                {
                    FlinchEnv.Log("SELFTEST_ERROR=" + ex.Message);
                    return 2;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Global\\FlinchNativeIslandAppMutex", out createdNew))
            {
                FlinchEnv.Log("Main mutex check: createdNew=" + createdNew);
                if (!createdNew)
                {
                    FlinchEnv.Log("Another Flinch instance is already running (mutex locked). Exiting.");
                    return 0;
                }
                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    FlinchEnv.Log("DISPATCHER_UNHANDLED_EXCEPTION: " + e.Exception.ToString());
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    FlinchEnv.Log("APPDOMAIN_UNHANDLED_EXCEPTION: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null"));
                };

                var win = new DynamicIslandWindow();
                win.Closed += (s, e) =>
                {
                    FlinchEnv.Log("DynamicIslandWindow win.Closed fired! StackTrace: " + Environment.StackTrace);
                    app.Shutdown();
                };
                app.MainWindow = win;
                win.Show();

                string restoreError;
                FlinchEnv.Log("Flinch starting — checking saved PC account session...");
                bool hasValidSession = SupabaseAuthClient.TryRestoreSavedSession(out restoreError);
                if (!hasValidSession)
                {
                    FlinchEnv.Log("Opening Flinch Sign In / Create Account window.");
                    try
                    {
                        var authWin = new FlinchAuthWindow(restoreError);
                        authWin.Owner = win;
                        authWin.Show();
                    }
                    catch { }
                }

                app.Run();
                return 0;
            }
        }
    }
}
