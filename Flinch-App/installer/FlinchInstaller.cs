using System;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using Path = System.IO.Path;

[assembly: AssemblyTitle("Flinch Setup & Uninstaller")]
[assembly: AssemblyDescription("Flinch voice companion installer & uninstaller")]
[assembly: AssemblyCompany("Reech")]
[assembly: AssemblyProduct("Flinch")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace FlinchInstaller
{
    internal static class Paths
    {
        public static readonly string DefaultInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Flinch");
        public static string InstallDir = DefaultInstallDir;
        public static string AppExe { get { return Path.Combine(InstallDir, "Flinch.exe"); } }
        public static string UninstallExe { get { return Path.Combine(InstallDir, "Uninstall.exe"); } }
        public static readonly string StartMenuLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Flinch.lnk");
        public static readonly string StartMenuUninstallLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall Flinch.lnk");
        public static readonly string DesktopLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Flinch.lnk");
        public static readonly string SessionDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flinch");
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Flinch";
    }

    internal static class Installer
    {
        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public static bool Elevate(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception) { return false; }
        }

        public static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".w" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static ImageSource LoadCatLogo()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("cat_logo.png"))
                {
                    if (s != null)
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.StreamSource = s;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void LaunchApp()
        {
            try
            {
                if (IsAdmin()) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Paths.AppExe + "\"") { UseShellExecute = false });
                else Process.Start(new ProcessStartInfo(Paths.AppExe) { WorkingDirectory = Paths.InstallDir });
            }
            catch { }
        }

        public static void StopRunningFlinch()
        {
            foreach (Process p in Process.GetProcessesByName("Flinch"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        public static void CreateShortcut(string linkPath, string target)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                string icoPath = Path.Combine(Path.GetDirectoryName(target), "flinch.ico");
                string iconLocation = File.Exists(icoPath) ? icoPath + ",0" : target + ",0";
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { iconLocation });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Flinch voice companion" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch { }
        }

        public static void Install(Action<int, string> progress)
        {
            progress(10, "Closing any running Flinch…");
            StopRunningFlinch();

            progress(30, "Copying Flinch…");
            Directory.CreateDirectory(Paths.InstallDir);
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Flinch.exe"))
            {
                if (s == null) throw new InvalidOperationException("Installer payload is missing.");
                using (FileStream fs = new FileStream(Paths.AppExe, FileMode.Create, FileAccess.Write))
                    s.CopyTo(fs);
            }
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("flinch.ico"))
                {
                    if (s != null)
                    {
                        using (FileStream fs = new FileStream(Path.Combine(Paths.InstallDir, "flinch.ico"), FileMode.Create, FileAccess.Write))
                            s.CopyTo(fs);
                    }
                }
            }
            catch { }

            progress(60, "Adding uninstaller…");
            File.Copy(Assembly.GetExecutingAssembly().Location, Paths.UninstallExe, true);

            progress(75, "Creating shortcuts…");
            CreateShortcut(Paths.StartMenuLink, Paths.AppExe);
            CreateShortcut(Paths.StartMenuUninstallLink, Paths.UninstallExe);
            CreateShortcut(Paths.DesktopLink, Paths.AppExe);

            progress(90, "Registering with Windows…");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Paths.UninstallKey))
            {
                k.SetValue("DisplayName", "Flinch (Demo)");
                k.SetValue("Publisher", "Reech");
                k.SetValue("DisplayVersion", "1.0.0");
                k.SetValue("InstallLocation", Paths.InstallDir);
                string icoPath = Path.Combine(Paths.InstallDir, "flinch.ico");
                k.SetValue("DisplayIcon", File.Exists(icoPath) ? icoPath : Paths.AppExe);
                k.SetValue("UninstallString", "\"" + Paths.UninstallExe + "\"");
                k.SetValue("QuietUninstallString", "\"" + Paths.UninstallExe + "\" /silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            progress(100, "Ready!");
        }

        public static void UninstallDirect(Action<int, string> progress = null)
        {
            if (progress != null) progress(15, "Closing running Flinch…");
            StopRunningFlinch();

            if (progress != null) progress(40, "Removing shortcuts…");
            try { if (File.Exists(Paths.StartMenuLink)) File.Delete(Paths.StartMenuLink); } catch { }
            try { if (File.Exists(Paths.StartMenuUninstallLink)) File.Delete(Paths.StartMenuUninstallLink); } catch { }
            try { if (File.Exists(Paths.DesktopLink)) File.Delete(Paths.DesktopLink); } catch { }

            if (progress != null) progress(65, "Removing application files…");
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            try { File.Delete(Path.Combine(dir, "Flinch.exe")); } catch { }
            try { File.Delete(Path.Combine(dir, "flinch.ico")); } catch { }

            if (progress != null) progress(85, "Cleaning up session & registry…");
            try { Registry.CurrentUser.DeleteSubKeyTree(Paths.UninstallKey, false); } catch { }
            try { if (Directory.Exists(Paths.SessionDir)) Directory.Delete(Paths.SessionDir, true); } catch { }

            if (progress != null) progress(100, "Uninstalled successfully.");

            // Self-delete Uninstall.exe and cleanup dir
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + Path.Combine(dir, "Uninstall.exe") + "\" & rmdir \"" + dir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }
    }

    public class InstallerWindow : Window
    {
        private TextBlock _status;
        private Rectangle _bar;
        private StackPanel _choosePanel;
        private StackPanel _progressPanel;
        private TextBox _pathBox;
        private TextBlock _pathError;
        private const double BarWidth = 380;

        private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xDF, 0x8A, 0x5E));
        private static readonly Brush Cream = new SolidColorBrush(Color.FromRgb(0xFA, 0xE9, 0xD4));
        private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x26));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAB));
        private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x32));

        private readonly string _autoDir;

        public InstallerWindow(string autoDir)
        {
            _autoDir = autoDir;
            Title = "Flinch Setup (Demo Build)";
            Width = 520;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;

            Border card = new Border
            {
                Margin = new Thickness(14),
                CornerRadius = new CornerRadius(20),
                Background = Dark,
                BorderBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Opacity = 0.4 }
            };

            Grid root = new Grid();
            TextBlock close = new TextBlock
            {
                Text = "\u2715", Foreground = Muted, FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 18, 0)
            };
            close.MouseLeftButtonDown += delegate { Environment.Exit(0); };
            root.Children.Add(close);

            StackPanel sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = 380 };

            var catLogo = Installer.LoadCatLogo();
            if (catLogo != null)
            {
                sp.Children.Add(new Image
                {
                    Source = catLogo,
                    Width = 84,
                    Height = 84,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 16)
                });
            }
            else
            {
                sp.Children.Add(new Border
                {
                    Width = 72, Height = 72, CornerRadius = new CornerRadius(16), Background = Orange,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20),
                    Child = new TextBlock
                    {
                        Text = "🐱", FontSize = 38,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

            // Page 1: Choose Install Directory
            _choosePanel = new StackPanel();
            StackPanel titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            titleRow.Children.Add(new TextBlock
            {
                Text = "Install Flinch", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Cream,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
            });
            Border badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(50, 0xDF, 0x8A, 0x5E)),
                BorderBrush = Orange,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "DEMO BUILD",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Orange,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
                }
            };
            titleRow.Children.Add(badge);
            _choosePanel.Children.Add(titleRow);
            _choosePanel.Children.Add(new TextBlock
            {
                Text = "Where should Flinch be installed?", FontSize = 13, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
            });

            Grid pathRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _pathBox = new TextBox
            {
                Text = Paths.DefaultInstallDir, Height = 38, FontSize = 13, Foreground = Cream, Background = Field,
                CaretBrush = Orange, BorderBrush = Orange, BorderThickness = new Thickness(1.5),
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(_pathBox, 0);
            pathRow.Children.Add(_pathBox);

            Border browse = Button("Browse…", Field, Cream);
            browse.Width = 82; browse.Height = 38; browse.Margin = new Thickness(8, 0, 0, 0);
            browse.MouseLeftButtonDown += delegate { BrowseForFolder(); };
            Grid.SetColumn(browse, 1);
            pathRow.Children.Add(browse);
            _choosePanel.Children.Add(pathRow);

            TextBlock pf = new TextBlock
            {
                Text = "Use Program Files (asks for administrator permission)", Foreground = Orange, FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(2, 2, 0, 4)
            };
            pf.MouseLeftButtonDown += delegate
            {
                _pathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Flinch");
            };
            _choosePanel.Children.Add(pf);

            _pathError = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed
            };
            _choosePanel.Children.Add(_pathError);

            Border install = Button("Install Demo", Orange, Dark);
            install.Height = 44; install.Margin = new Thickness(0, 10, 0, 0);
            install.MouseLeftButtonDown += delegate { StartInstall(); };
            _choosePanel.Children.Add(install);

            _choosePanel.Children.Add(new TextBlock
            {
                Text = "Demo Build · 5 voice messages per account",
                FontSize = 11,
                Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
            });

            sp.Children.Add(_choosePanel);

            // Page 2: Progress
            _progressPanel = new StackPanel { Visibility = Visibility.Collapsed };
            _status = new TextBlock
            {
                Text = "Installing Flinch (Demo Build)…", FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 17, Foreground = Cream, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _progressPanel.Children.Add(_status);
            Grid track = new Grid { Width = BarWidth, Height = 8, HorizontalAlignment = HorizontalAlignment.Center };
            track.Children.Add(new Rectangle { RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) });
            _bar = new Rectangle { Width = 0, RadiusX = 4, RadiusY = 4, HorizontalAlignment = HorizontalAlignment.Left, Fill = Orange };
            track.Children.Add(_bar);
            _progressPanel.Children.Add(track);
            sp.Children.Add(_progressPanel);

            root.Children.Add(sp);
            card.Child = root;
            Content = card;

            MouseLeftButtonDown += delegate { try { DragMove(); } catch { } };
            Loaded += delegate
            {
                if (!string.IsNullOrEmpty(_autoDir))
                {
                    Paths.InstallDir = _autoDir;
                    _choosePanel.Visibility = Visibility.Collapsed;
                    _progressPanel.Visibility = Visibility.Visible;
                    Begin();
                }
            };
        }

        private static Border Button(string text, Brush bg, Brush fg)
        {
            TextBlock tb = new TextBlock
            {
                Text = text, Foreground = fg, FontSize = 14, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
            };
            return new Border
            {
                Background = bg, CornerRadius = new CornerRadius(10), Cursor = System.Windows.Input.Cursors.Hand, Child = tb
            };
        }

        private void BrowseForFolder()
        {
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Select folder to install Flinch into:";
                    dlg.ShowNewFolderButton = true;
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        string picked = dlg.SelectedPath.TrimEnd('\\');
                        _pathBox.Text = picked.EndsWith("\\Flinch", StringComparison.OrdinalIgnoreCase)
                            ? picked : Path.Combine(picked, "Flinch");
                    }
                }
            }
            catch { }
        }

        private void StartInstall()
        {
            string chosen = (_pathBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(chosen))
            {
                _pathError.Text = "Please enter an install folder.";
                _pathError.Visibility = Visibility.Visible;
                return;
            }

            try { chosen = Path.GetFullPath(chosen); }
            catch (Exception ex)
            {
                _pathError.Text = "Invalid folder path: " + ex.Message;
                _pathError.Visibility = Visibility.Visible;
                return;
            }

            Paths.InstallDir = chosen;
            if (!Installer.CanWrite(chosen))
            {
                if (!Installer.IsAdmin())
                {
                    bool launched = Installer.Elevate("/install \"" + chosen + "\"");
                    if (launched) Environment.Exit(0);
                    else
                    {
                        _pathError.Text = "Administrator permission was declined. Please pick a writable folder.";
                        _pathError.Visibility = Visibility.Visible;
                    }
                    return;
                }
            }

            _choosePanel.Visibility = Visibility.Collapsed;
            _progressPanel.Visibility = Visibility.Visible;
            Begin();
        }

        private void SetProgress(int pct, string msg)
        {
            Dispatcher.Invoke(new Action(delegate
            {
                _status.Text = msg;
                DoubleAnimation anim = new DoubleAnimation(pct / 100.0 * BarWidth, TimeSpan.FromMilliseconds(220));
                _bar.BeginAnimation(FrameworkElement.WidthProperty, anim);
            }));
        }

        private void Begin()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    Installer.Install(SetProgress);
                    Thread.Sleep(700);
                    Installer.LaunchApp();
                }
                catch (Exception ex)
                {
                    SetProgress(0, "Install failed: " + ex.Message);
                    Thread.Sleep(5000);
                }
                Dispatcher.Invoke(new Action(delegate { Close(); }));
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    public class UninstallerWindow : Window
    {
        private TextBlock _status;
        private Rectangle _bar;
        private StackPanel _confirmPanel;
        private StackPanel _progressPanel;
        private const double BarWidth = 380;

        private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xDF, 0x8A, 0x5E));
        private static readonly Brush Cream = new SolidColorBrush(Color.FromRgb(0xFA, 0xE9, 0xD4));
        private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x26));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAB));
        private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x25, 0x29, 0x32));
        private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xE0, 0x52, 0x52));

        public UninstallerWindow()
        {
            Title = "Uninstall Flinch (Demo Build)";
            Width = 500;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;

            Border card = new Border
            {
                Margin = new Thickness(14),
                CornerRadius = new CornerRadius(20),
                Background = Dark,
                BorderBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Opacity = 0.4 }
            };

            Grid root = new Grid();
            TextBlock close = new TextBlock
            {
                Text = "\u2715", Foreground = Muted, FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 18, 0)
            };
            close.MouseLeftButtonDown += delegate { Environment.Exit(0); };
            root.Children.Add(close);

            StackPanel sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Width = 380 };

            var catLogo = Installer.LoadCatLogo();
            if (catLogo != null)
            {
                sp.Children.Add(new Image
                {
                    Source = catLogo,
                    Width = 74,
                    Height = 74,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 16)
                });
            }
            else
            {
                sp.Children.Add(new Border
                {
                    Width = 64, Height = 64, CornerRadius = new CornerRadius(16), Background = Orange,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18),
                    Child = new TextBlock
                    {
                        Text = "🐱", FontSize = 34,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

            _confirmPanel = new StackPanel();
            _confirmPanel.Children.Add(new TextBlock
            {
                Text = "Uninstall Flinch (Demo Build)?", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Cream,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
            });
            _confirmPanel.Children.Add(new TextBlock
            {
                Text = "This will completely remove Flinch, desktop shortcuts, and local account credentials from this PC.",
                FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
            });

            Grid btnRow = new Grid();
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border btnCancel = Button("Cancel", Field, Cream);
            btnCancel.Height = 42;
            btnCancel.MouseLeftButtonDown += delegate { Environment.Exit(0); };
            Grid.SetColumn(btnCancel, 0);
            btnRow.Children.Add(btnCancel);

            Border btnUninstall = Button("Uninstall", Danger, Cream);
            btnUninstall.Height = 42;
            btnUninstall.MouseLeftButtonDown += delegate { StartUninstall(); };
            Grid.SetColumn(btnUninstall, 2);
            btnRow.Children.Add(btnUninstall);

            _confirmPanel.Children.Add(btnRow);
            sp.Children.Add(_confirmPanel);

            _progressPanel = new StackPanel { Visibility = Visibility.Collapsed };
            _status = new TextBlock
            {
                Text = "Uninstalling Flinch…", FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 16, Foreground = Cream, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _progressPanel.Children.Add(_status);
            Grid track = new Grid { Width = BarWidth, Height = 8, HorizontalAlignment = HorizontalAlignment.Center };
            track.Children.Add(new Rectangle { RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) });
            _bar = new Rectangle { Width = 0, RadiusX = 4, RadiusY = 4, HorizontalAlignment = HorizontalAlignment.Left, Fill = Danger };
            track.Children.Add(_bar);
            _progressPanel.Children.Add(track);
            sp.Children.Add(_progressPanel);

            root.Children.Add(sp);
            card.Child = root;
            Content = card;

            MouseLeftButtonDown += delegate { try { DragMove(); } catch { } };
        }

        private static Border Button(string text, Brush bg, Brush fg)
        {
            TextBlock tb = new TextBlock
            {
                Text = text, Foreground = fg, FontSize = 14, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
            };
            return new Border
            {
                Background = bg, CornerRadius = new CornerRadius(10), Cursor = System.Windows.Input.Cursors.Hand, Child = tb
            };
        }

        private void StartUninstall()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!Installer.IsAdmin() && !Installer.CanWrite(dir))
            {
                if (Installer.Elevate("/uninstall"))
                {
                    Environment.Exit(0);
                    return;
                }
            }

            _confirmPanel.Visibility = Visibility.Collapsed;
            _progressPanel.Visibility = Visibility.Visible;

            Thread t = new Thread(delegate()
            {
                try
                {
                    Installer.UninstallDirect(delegate(int pct, string msg)
                    {
                        Dispatcher.Invoke(new Action(delegate
                        {
                            _status.Text = msg;
                            DoubleAnimation anim = new DoubleAnimation(pct / 100.0 * BarWidth, TimeSpan.FromMilliseconds(200));
                            _bar.BeginAnimation(FrameworkElement.WidthProperty, anim);
                        }));
                    });
                    Thread.Sleep(1200);
                    Dispatcher.Invoke(new Action(delegate { Close(); }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(delegate { _status.Text = "Error: " + ex.Message; }));
                    Thread.Sleep(3000);
                    Dispatcher.Invoke(new Action(delegate { Close(); }));
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    internal static class EntryPoint
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string exeName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
            bool isUninstall = exeName.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
                              (args != null && args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

            if (isUninstall)
            {
                if (args != null && Array.Exists(args, a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)))
                {
                    Installer.UninstallDirect();
                    return 0;
                }

                new Application().Run(new UninstallerWindow());
                return 0;
            }

            string autoDir = null;
            if (args != null && args.Length > 1 && args[0].Equals("/install", StringComparison.OrdinalIgnoreCase))
                autoDir = args[1];
            new Application().Run(new InstallerWindow(autoDir));
            return 0;
        }
    }
}
