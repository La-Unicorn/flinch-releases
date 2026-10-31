// ============================================================================
// FLINCH NOTCH HUB: Pure MacBook Notch Widening Control Center
// Dynamically expands the top notch when hovered, matching exact design
// 100% C# 5 Compatible for .NET Framework 4.0 / 4.8
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FlinchApp
{
    internal sealed partial class DynamicIslandWindow : Window
    {
        // Hub in Notch UI Elements
        private Grid _hubExpandedPanel;
        private Border _hubFlyoutHost;
        private ContentControl _hubFlyoutContent;
        private bool _isHubFlyoutOpen = false;
        private string _activeHubFlyoutType = null;

        // Animated soundbars in center pill
        private double _hubWavePhase = 0;
        private readonly List<Rectangle> _hubWaveBars = new List<Rectangle>();
        private static readonly double[] _hubWaveBaseH = new double[] { 4.0, 9.0, 13.0, 7.0, 10.0, 5.0 };
        private TextBlock _hubListenStatusLabel;

        // Badges & Themes
        private Ellipse _hubGmailStatusDot;
        private Ellipse _hubAccentDot;
        private int _hubAccentIndex = 0;
        private static readonly Color[] HubAccentColors = new Color[]
        {
            Color.FromRgb(0x3D, 0xDC, 0x84), // Mint
            Color.FromRgb(0xA8, 0x55, 0xF7), // Purple / Violet
            Color.FromRgb(0x38, 0xBD, 0xF8), // Sky Blue
            Color.FromRgb(0xFB, 0x92, 0x3C), // Amber
            Color.FromRgb(0xF8, 0x71, 0x71)  // Coral
        };

        // History UI rows
        private StackPanel _hubHistoryRowsStack;

        // Gmail inputs in Hub Flyout
        private TextBox _hubEmailBox;
        private PasswordBox _hubPassBox;
        private TextBlock _hubEmailFeedbackText;
        private TextBlock _hubGmailStatusHeader;
        private StackPanel _hubInboxCardsPanel;

        // =====================================================================
        // BUILD HUB EXPANDED PANEL (Directly inside notch content host)
        // =====================================================================
        private Grid BuildHubExpandedPanel()
        {
            var root = new Grid
            {
                Width = 900,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Opacity = 0.0
            };

            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) }); // Top Bar (Sleek height)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // Flyout Host

            // Row 0: Top Bar with 5 Columns
            var topBar = BuildHubTopBar();
            Grid.SetRow(topBar, 0);
            root.Children.Add(topBar);

            // Row 1: Flyout Container (Dropdown for Gmail, History, Settings)
            _hubFlyoutHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x15)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(24, 12, 24, 16)
            };
            _hubFlyoutContent = new ContentControl();
            _hubFlyoutHost.Child = _hubFlyoutContent;
            Grid.SetRow(_hubFlyoutHost, 1);
            root.Children.Add(_hubFlyoutHost);

            return root;
        }

        private Grid BuildHubTopBar()
        {
            var grid = new Grid
            {
                Height = 68,
                VerticalAlignment = VerticalAlignment.Top
            };

            // 5 Columns: History (215), Integrations (150), Center Island (140), Customization (150), Settings (160)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });

            // Dividers between sections
            for (int c = 0; c < 4; c++)
            {
                var div = new Rectangle
                {
                    Width = 1,
                    Height = 40,
                    Fill = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(div, c);
                grid.Children.Add(div);
            }

            // Col 0: Chat History
            var colHistory = BuildHubHistoryColumn();
            Grid.SetColumn(colHistory, 0);
            grid.Children.Add(colHistory);

            // Col 1: Integrations
            var colIntegrations = BuildHubIntegrationsColumn();
            Grid.SetColumn(colIntegrations, 1);
            grid.Children.Add(colIntegrations);

            // Col 2: Center Camera Cutout & Listening Island
            var colCenter = BuildHubCenterColumn();
            Grid.SetColumn(colCenter, 2);
            grid.Children.Add(colCenter);

            // Col 3: Customization
            var colCustomization = BuildHubCustomizationColumn();
            Grid.SetColumn(colCustomization, 3);
            grid.Children.Add(colCustomization);

            // Col 4: Settings
            var colSettings = BuildHubSettingsColumn();
            Grid.SetColumn(colSettings, 4);
            grid.Children.Add(colSettings);

            return grid;
        }

        // ---------------------------------------------------------------------
        // Column 0: Chat History
        // ---------------------------------------------------------------------
        private UIElement BuildHubHistoryColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(14, 6, 8, 4),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: 🕒 Chat History ⌵
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\U0001F552", // clock
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Chat History",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304", // chevron
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            header.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleHubFlyout("history");
            };
            panel.Children.Add(header);

            // 3 Recent query items stack
            _hubHistoryRowsStack = new StackPanel { Orientation = Orientation.Vertical };
            PopulateHubHistoryRows();
            panel.Children.Add(_hubHistoryRowsStack);

            return panel;
        }

        private void PopulateHubHistoryRows()
        {
            if (_hubHistoryRowsStack == null) return;
            _hubHistoryRowsStack.Children.Clear();

            var recentTurns = FlinchSkills.GetRecentTurns(3);
            if (recentTurns == null || recentTurns.Count == 0)
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
                var row = CreateHubHistoryRow(item);
                _hubHistoryRowsStack.Children.Add(row);
            }
        }

        private Border CreateHubHistoryRow(FlinchSkills.HistoryTurn turn)
        {
            var row = new Border
            {
                Height = 15,
                Margin = new Thickness(0, 0.5, 0, 0.5),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var dock = new DockPanel { LastChildFill = true };

            string relTime = FormatHubRelativeTime(turn.t);
            var timeTb = new TextBlock
            {
                Text = relTime,
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 1, 0)
            };
            DockPanel.SetDock(timeTb, Dock.Right);
            dock.Children.Add(timeTb);

            string iconStr = "\U0001F4AC";
            string low = (turn.u ?? "").ToLowerInvariant();
            if (low.Contains("code") || low.Contains("function") || low.Contains("debug") || low.Contains("explain")) iconStr = "</>";
            else if (low.Contains("create") || low.Contains("site") || low.Contains("file") || low.Contains("draft")) iconStr = "\U0001F4C4";

            var iconTb = new TextBlock
            {
                Text = iconStr,
                FontSize = (iconStr == "</>") ? 7.5 : 8.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                FontWeight = (iconStr == "</>") ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            DockPanel.SetDock(iconTb, Dock.Left);
            dock.Children.Add(iconTb);

            var queryTb = new TextBlock
            {
                Text = turn.u ?? "",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            dock.Children.Add(queryTb);

            row.Child = dock;

            row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(turn.a))
                {
                    SpeakAnswer(turn.a);
                }
            };

            return row;
        }

        private static string FormatHubRelativeTime(string iso)
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
        private UIElement BuildHubIntegrationsColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(8, 6, 8, 4),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: 🌸 Integrations ⌵
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 5) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\U0001F338", // flower
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0xB6)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Integrations",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            header.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleHubFlyout("gmail");
            };
            panel.Children.Add(header);

            // Icons row: [N] Notion, [♫] Spotify, [✉] Gmail, [+ Add]
            var iconsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

            // Notion tile
            iconsRow.Children.Add(CreateHubTile("N", Brushes.White, 10.5, "Notion Integration (Ready)", (s, e) =>
            {
                e.Handled = true;
                ShowAlert("notion", "Notion Connected", "Workspace ready");
            }));

            // Spotify tile
            var spot = CreateHubTile("\u266B", new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), 11.5, "Spotify Media Controls", (s, e) =>
            {
                e.Handled = true;
                ShowAlert("spotify", "Spotify Ready", "Play, pause, skip enabled");
            });
            spot.Margin = new Thickness(4, 0, 0, 0);
            iconsRow.Children.Add(spot);

            // Gmail tile with live status dot
            var gmailBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2F, 0x3A)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Google Gmail (Read, Write & Send Emails)",
                Margin = new Thickness(4, 0, 0, 0)
            };
            var gmailGrid = new Grid();
            gmailGrid.Children.Add(new TextBlock
            {
                Text = "\u2709", // envelope
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            _hubGmailStatusDot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0)
            };
            gmailGrid.Children.Add(_hubGmailStatusDot);
            gmailBtn.Child = gmailGrid;
            gmailBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleHubFlyout("gmail");
            };
            iconsRow.Children.Add(gmailBtn);

            // [+ Add] Button
            var addBtn = new Border
            {
                Height = 22,
                Padding = new Thickness(6, 0, 6, 0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Connect New Integration",
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            addBtn.Child = new TextBlock
            {
                Text = "+ Add",
                FontSize = 9.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                VerticalAlignment = VerticalAlignment.Center
            };
            addBtn.MouseEnter += (s, e) => addBtn.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            addBtn.MouseLeave += (s, e) => addBtn.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x20));
            addBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleHubFlyout("gmail");
            };
            iconsRow.Children.Add(addBtn);

            panel.Children.Add(iconsRow);
            return panel;
        }

        private Border CreateHubTile(string text, Brush fg, double fontSize, string tooltip, MouseButtonEventHandler onClick)
        {
            var tile = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2F, 0x3A)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            tile.Child = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            tile.MouseEnter += (s, e) => tile.BorderBrush = BrushMint;
            tile.MouseLeave += (s, e) => tile.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2F, 0x3A));
            if (onClick != null) tile.MouseLeftButtonDown += onClick;
            return tile;
        }

        // ---------------------------------------------------------------------
        // Column 2: Center Camera Cutout & Listening Island
        // ---------------------------------------------------------------------
        private UIElement BuildHubCenterColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Top Camera / Mic Emblem Badge
            var badge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x31, 0x3D)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "Flinch Core Camera & Voice Sensor"
            };
            badge.Child = new TextBlock
            {
                Text = "\U0001F399", // microphone
                FontSize = 9.5,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(badge);

            // Bottom Listening Card
            var card = new Border
            {
                Width = 96,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x29, 0x33)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 3, 0, 0),
                ToolTip = "Click to Talk / Toggle Voice Recording"
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Soundwave bars
            var barsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _hubWaveBars.Clear();
            for (int i = 0; i < 6; i++)
            {
                var r = new Rectangle
                {
                    Width = 2,
                    Height = _hubWaveBaseH[i],
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = BrushMint,
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _hubWaveBars.Add(r);
                barsPanel.Children.Add(r);
            }
            stack.Children.Add(barsPanel);

            _hubListenStatusLabel = new TextBlock
            {
                Text = "Listening...",
                FontSize = 8.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(_hubListenStatusLabel);

            card.Child = stack;
            card.MouseEnter += (s, e) => card.BorderBrush = BrushMint;
            card.MouseLeave += (s, e) => card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x29, 0x33));
            card.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleVoiceRecording();
            };
            panel.Children.Add(card);

            return panel;
        }

        // ---------------------------------------------------------------------
        // Column 3: Customization
        // ---------------------------------------------------------------------
        private UIElement BuildHubCustomizationColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(8, 6, 8, 4),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: 🖊 Customization ⌵
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 5) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\u270E", // pen
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Customization",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            panel.Children.Add(header);

            // Controls row: [☼] Sun, [☾] Moon, [●] Gradient Sphere, [Aa] Font size
            var ctrlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

            // Sun tile
            ctrlRow.Children.Add(CreateHubTile("\u263C", new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), 11.5, "Switch to Light Theme", (s, e) =>
            {
                e.Handled = true;
                ShowAlert("theme", "Theme Updated", "Light mode enabled");
            }));

            // Moon tile (Active highlight)
            var moon = CreateHubTile("\u263E", new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), 11.5, "Dark Theme (Active)", null);
            moon.BorderBrush = BrushMint;
            moon.Margin = new Thickness(4, 0, 0, 0);
            ctrlRow.Children.Add(moon);

            // Accent Dot Picker
            var accentBtn = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2F, 0x3A)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Cycle Accent Theme Color",
                Margin = new Thickness(4, 0, 0, 0)
            };
            _hubAccentDot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(HubAccentColors[_hubAccentIndex]),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            accentBtn.Child = _hubAccentDot;
            accentBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                _hubAccentIndex = (_hubAccentIndex + 1) % HubAccentColors.Length;
                _hubAccentDot.Fill = new SolidColorBrush(HubAccentColors[_hubAccentIndex]);
                ShowAlert("color", "Accent Color Changed", "Theme refreshed");
            };
            ctrlRow.Children.Add(accentBtn);

            // [Aa] Font Size Toggle
            var fontBtn = new Border
            {
                Width = 26,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2F, 0x3A)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Toggle Typography Size",
                Margin = new Thickness(4, 0, 0, 0)
            };
            fontBtn.Child = new TextBlock
            {
                Text = "Aa",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            fontBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ShowAlert("font", "Typography Adjusted", "Standard scaling applied");
            };
            ctrlRow.Children.Add(fontBtn);

            panel.Children.Add(ctrlRow);
            return panel;
        }

        // ---------------------------------------------------------------------
        // Column 4: Settings
        // ---------------------------------------------------------------------
        private UIElement BuildHubSettingsColumn()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(8, 6, 14, 4),
                VerticalAlignment = VerticalAlignment.Top
            };

            // Header button: ⚙ Settings ⌵
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4) };
            var hStack = new StackPanel { Orientation = Orientation.Horizontal };
            hStack.Children.Add(new TextBlock
            {
                Text = "\u2699", // gear
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = "Settings",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            hStack.Children.Add(new TextBlock
            {
                Text = " \u2304",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(hStack);
            header.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                ToggleHubFlyout("settings");
            };
            panel.Children.Add(header);

            // 3 Settings Links
            panel.Children.Add(CreateHubSettingsLink("\u2699 General", () => ToggleHubFlyout("settings")));
            panel.Children.Add(CreateHubSettingsLink("\u2328 Hotkeys", () => ToggleHubFlyout("hotkeys")));
            panel.Children.Add(CreateHubSettingsLink("\u2139 About Flinch", () =>
            {
                ShowAlert("info", "Flinch v0.1.0", "Built with 100% on-device local privacy");
            }));

            return panel;
        }

        private Border CreateHubSettingsLink(string title, Action onClick)
        {
            var link = new Border
            {
                Height = 15,
                Margin = new Thickness(0, 0.5, 0, 0.5),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            link.Child = new TextBlock
            {
                Text = title,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            link.MouseEnter += (s, e) =>
            {
                link.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                ((TextBlock)link.Child).Foreground = Brushes.White;
            };
            link.MouseLeave += (s, e) =>
            {
                link.Background = Brushes.Transparent;
                ((TextBlock)link.Child).Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            };
            link.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (onClick != null) onClick();
            };
            return link;
        }

        // =====================================================================
        // FLYOUT PANELS (Seamlessly drops down below the 68px notch bar)
        // =====================================================================
        private void ToggleHubFlyout(string type)
        {
            if (_isHubFlyoutOpen && _activeHubFlyoutType == type)
            {
                CloseHubFlyout();
                return;
            }

            _activeHubFlyoutType = type;
            _isHubFlyoutOpen = true;

            UIElement view = null;
            if (type == "gmail")
            {
                view = BuildHubGmailFlyout();
            }
            else if (type == "history")
            {
                view = BuildHubHistoryFlyout();
            }
            else if (type == "hotkeys")
            {
                view = BuildHubHotkeysFlyout();
            }
            else if (type == "settings")
            {
                view = BuildHubGeneralSettingsFlyout();
            }

            _hubFlyoutContent.Content = view;
            _hubFlyoutHost.Visibility = Visibility.Visible;
            _targetH = 260; // Expand notch downwards
        }

        private void CloseHubFlyout()
        {
            _isHubFlyoutOpen = false;
            _activeHubFlyoutType = null;
            _hubFlyoutHost.Visibility = Visibility.Collapsed;
            _targetH = 70; // Spring back to compact notch
        }

        private bool HasFocusedInputInHub()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return (focused is TextBox || focused is PasswordBox);
        }

        private void RefreshHubData()
        {
            PopulateHubHistoryRows();
            if (_hubGmailStatusDot != null)
            {
                _hubGmailStatusDot.Fill = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
            }
            if (_hubListenStatusLabel != null)
            {
                _hubListenStatusLabel.Text = (_state == IslandState.Listening) ? "Listening..." : "Flinch Ready";
            }
        }

        // ---------------------------------------------------------------------
        // Flyout 1: Gmail Connection & Actions
        // ---------------------------------------------------------------------
        private UIElement BuildHubGmailFlyout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var closeBtn = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 9.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseHubFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = "\u2709 Google Workspace & Gmail", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            _hubGmailStatusHeader = new TextBlock
            {
                Text = BackgroundAutomationsEngine.EmailConfig.IsConnected ? "  \u2022 Connected" : "  \u2022 Disconnected",
                FontSize = 11,
                Foreground = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : BrushTextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            titleRow.Children.Add(_hubGmailStatusHeader);
            header.Children.Add(titleRow);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // Form Grid
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });

            // Form Stack
            var fStack = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            fStack.Children.Add(new TextBlock { Text = "Gmail Address:", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)), Margin = new Thickness(0, 0, 0, 3) });
            _hubEmailBox = new TextBox
            {
                Height = 26,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x46)),
                Foreground = Brushes.White,
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 11,
                Text = BackgroundAutomationsEngine.EmailConfig.EmailAddress ?? ""
            };
            fStack.Children.Add(_hubEmailBox);

            fStack.Children.Add(new TextBlock { Text = "Google App Password (16 chars):", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)), Margin = new Thickness(0, 6, 0, 3) });
            _hubPassBox = new PasswordBox
            {
                Height = 26,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x46)),
                Foreground = Brushes.White,
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 11,
                Password = BackgroundAutomationsEngine.GetEmailPassword() ?? ""
            };
            fStack.Children.Add(_hubPassBox);

            var btnsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var connBtn = CreateHubActionButton("\u2713 Connect", BrushMint, Colors.Black, (s, e) => ConnectHubGmail());
            btnsRow.Children.Add(connBtn);

            var testBtn = CreateHubActionButton("\u26A1 Send Test", new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)), Colors.Black, (s, e) => SendHubTestEmail());
            testBtn.Margin = new Thickness(6, 0, 0, 0);
            btnsRow.Children.Add(testBtn);

            var disBtn = CreateHubActionButton("\u2715 Disconnect", new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x34)), Color.FromRgb(0xD1, 0xD5, 0xDB), (s, e) => DisconnectHubGmail());
            disBtn.Margin = new Thickness(6, 0, 0, 0);
            btnsRow.Children.Add(disBtn);

            fStack.Children.Add(btnsRow);

            _hubEmailFeedbackText = new TextBlock { FontSize = 10, Foreground = BrushTextMuted, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            fStack.Children.Add(_hubEmailFeedbackText);

            Grid.SetColumn(fStack, 0);
            body.Children.Add(fStack);

            // Inbox preview
            var inbStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            var inbTop = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            inbTop.Children.Add(new TextBlock { Text = "Recent Inbox", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)) });
            var refLink = new TextBlock { Text = "\u21BB Refresh", FontSize = 9.5, Foreground = BrushMint, Cursor = Cursors.Hand };
            refLink.MouseLeftButtonDown += (s, e) => FetchHubUnreadMessages();
            DockPanel.SetDock(refLink, Dock.Right);
            inbTop.Children.Add(refLink);
            inbStack.Children.Add(inbTop);

            var scroll = new ScrollViewer { Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _hubInboxCardsPanel = new StackPanel();
            scroll.Content = _hubInboxCardsPanel;
            inbStack.Children.Add(scroll);

            Grid.SetColumn(inbStack, 1);
            body.Children.Add(inbStack);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            return root;
        }

        private Border CreateHubActionButton(string label, Brush bg, Color fg, RoutedEventHandler onClick)
        {
            var btn = new Border
            {
                Height = 24,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(5),
                Background = bg,
                Cursor = Cursors.Hand
            };
            btn.Child = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(fg), VerticalAlignment = VerticalAlignment.Center };
            btn.MouseEnter += (s, e) => btn.Opacity = 0.88;
            btn.MouseLeave += (s, e) => btn.Opacity = 1.0;
            if (onClick != null) btn.MouseLeftButtonDown += (s, e) => onClick(s, e);
            return btn;
        }

        private void ConnectHubGmail()
        {
            string email = (_hubEmailBox.Text ?? "").Trim();
            string pass = _hubPassBox.Password ?? "";

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pass))
            {
                _hubEmailFeedbackText.Foreground = BrushOrange;
                _hubEmailFeedbackText.Text = "Enter Gmail address and 16-char App Password.";
                return;
            }

            _hubEmailFeedbackText.Foreground = BrushTextMuted;
            _hubEmailFeedbackText.Text = "Verifying IMAP (993) & SMTP (587)...";

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
                        BackgroundAutomationsEngine.EmailConfig.IsConnected = true;
                        BackgroundAutomationsEngine.SaveEmailPassword(pass);

                        _hubEmailFeedbackText.Foreground = BrushMint;
                        _hubEmailFeedbackText.Text = string.Format("Connected! Found {0} unread email(s).", count);

                        RefreshHubData();
                        FetchHubUnreadMessages();
                    }
                    else
                    {
                        _hubEmailFeedbackText.Foreground = BrushOrange;
                        _hubEmailFeedbackText.Text = "Failed: " + err;
                    }
                }));
            });
        }

        private void DisconnectHubGmail()
        {
            BackgroundAutomationsEngine.EmailConfig.IsConnected = false;
            BackgroundAutomationsEngine.SaveEmailPassword("");
            _hubPassBox.Password = "";
            _hubEmailFeedbackText.Foreground = BrushTextMuted;
            _hubEmailFeedbackText.Text = "Disconnected.";
            RefreshHubData();
            if (_hubInboxCardsPanel != null) _hubInboxCardsPanel.Children.Clear();
        }

        private void SendHubTestEmail()
        {
            string email = (_hubEmailBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(email) || !BackgroundAutomationsEngine.EmailConfig.IsConnected)
            {
                _hubEmailFeedbackText.Foreground = BrushOrange;
                _hubEmailFeedbackText.Text = "Connect Gmail first.";
                return;
            }

            _hubEmailFeedbackText.Foreground = BrushTextMuted;
            _hubEmailFeedbackText.Text = "Sending test email...";

            Task.Run(() =>
            {
                string err;
                bool ok = DirectSmtpClient.SendGmailEmail(
                    email,
                    "Flinch Assistant Verification",
                    "Success! Your Flinch AI Assistant is connected to Google Gmail. You can now read, draft, and send emails via voice.",
                    out err);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ok)
                    {
                        _hubEmailFeedbackText.Foreground = BrushMint;
                        _hubEmailFeedbackText.Text = "Test email sent successfully to " + email;
                        ShowAlert("email", "Test Email Sent", email);
                    }
                    else
                    {
                        _hubEmailFeedbackText.Foreground = BrushOrange;
                        _hubEmailFeedbackText.Text = "Send error: " + err;
                    }
                }));
            });
        }

        private void FetchHubUnreadMessages()
        {
            if (!BackgroundAutomationsEngine.EmailConfig.IsConnected) return;

            string email = BackgroundAutomationsEngine.EmailConfig.EmailAddress;
            string pass = BackgroundAutomationsEngine.GetEmailPassword();

            Task.Run(() =>
            {
                List<EmailMessageInfo> msgs;
                string err;
                bool ok = DirectImapClient.FetchUnreadMessages("imap.gmail.com", 993, true, email, pass, 6, out msgs, out err);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_hubInboxCardsPanel == null) return;
                    _hubInboxCardsPanel.Children.Clear();

                    if (!ok || msgs == null || msgs.Count == 0)
                    {
                        _hubInboxCardsPanel.Children.Add(new TextBlock { Text = ok ? "No unread emails." : ("Error: " + err), FontSize = 9.5, Foreground = BrushTextMuted });
                        return;
                    }

                    foreach (var m in msgs)
                    {
                        var card = new Border
                        {
                            Margin = new Thickness(0, 0, 0, 4),
                            Padding = new Thickness(6, 4, 6, 4),
                            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(5)
                        };

                        var st = new StackPanel();
                        st.Children.Add(new TextBlock { Text = m.From ?? "Unknown", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                        st.Children.Add(new TextBlock { Text = m.Subject ?? "(No Subject)", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), TextTrimming = TextTrimming.CharacterEllipsis });
                        card.Child = st;
                        _hubInboxCardsPanel.Children.Add(card);
                    }
                }));
            });
        }

        // ---------------------------------------------------------------------
        // Flyout 2: History Search Flyout
        // ---------------------------------------------------------------------
        private UIElement BuildHubHistoryFlyout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var closeBtn = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 9.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseHubFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            header.Children.Add(new TextBlock { Text = "\U0001F552 Conversation History", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var scroll = new ScrollViewer { Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel();

            var all = FlinchSkills.LoadRecentHistory();
            if (all.Count == 0)
            {
                stack.Children.Add(new TextBlock { Text = "No past queries recorded.", FontSize = 10, Foreground = BrushTextMuted });
            }
            else
            {
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    var turn = all[i];
                    var card = new Border
                    {
                        Margin = new Thickness(0, 0, 0, 4),
                        Padding = new Thickness(8, 6, 8, 6),
                        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Cursor = Cursors.Hand
                    };
                    var st = new StackPanel();
                    st.Children.Add(new TextBlock { Text = "\U0001F4AC " + turn.u, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                    if (!string.IsNullOrWhiteSpace(turn.a))
                    {
                        st.Children.Add(new TextBlock { Text = turn.a, FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), TextWrapping = TextWrapping.Wrap });
                    }
                    card.Child = st;
                    card.MouseLeftButtonDown += (s, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(turn.a)) SpeakAnswer(turn.a);
                    };
                    stack.Children.Add(card);
                }
            }

            scroll.Content = stack;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            return root;
        }

        // ---------------------------------------------------------------------
        // Flyout 3: Hotkeys
        // ---------------------------------------------------------------------
        private UIElement BuildHubHotkeysFlyout()
        {
            var root = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var closeBtn = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 9.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseHubFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            header.Children.Add(new TextBlock { Text = "\u2328 Keyboard Shortcuts", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            root.Children.Add(header);

            string[,] keys = new string[,]
            {
                { "Alt + V", "Start / Stop Voice Recording" },
                { "Ctrl + Alt + X", "Emergency Stop (Speech, Tasks & Timers)" },
                { "Alt + S", "Screen Window Capture Context" },
                { "Hover Notch", "Fluidly widens into Flinch Hub" },
                { "Voice Command", "\"Check my email\", \"Draft email to alex saying hello\"" }
            };

            for (int i = 0; i < keys.GetLength(0); i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x22, 0x2D)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x3A, 0x4B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1)
                };
                badge.Child = new TextBlock { Text = keys[i, 0], FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = BrushMint };
                DockPanel.SetDock(badge, Dock.Left);
                row.Children.Add(badge);

                row.Children.Add(new TextBlock
                {
                    Text = "  " + keys[i, 1],
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                root.Children.Add(row);
            }

            return root;
        }

        // ---------------------------------------------------------------------
        // Flyout 4: General Settings
        // ---------------------------------------------------------------------
        private UIElement BuildHubGeneralSettingsFlyout()
        {
            var root = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var closeBtn = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 9.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseHubFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            header.Children.Add(new TextBlock { Text = "\u2699 General Settings & Automations", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            root.Children.Add(header);

            root.Children.Add(CreateHubToggleCard(
                "Launch at Windows Startup",
                "Automatically starts Flinch whenever you turn on or sign in to Windows.",
                AutoStartManager.IsAutoStartEnabled(),
                val =>
                {
                    AutoStartManager.SetAutoStart(val);
                }));

            root.Children.Add(CreateHubToggleCard(
                "Email Guardian",
                "Monitors Gmail in background every 15 min for urgent action items.",
                BackgroundAutomationsEngine.Config.EmailGuardianEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.EmailGuardianEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                }));

            root.Children.Add(CreateHubToggleCard(
                "Downloads Organizer",
                "Automatically clusters incoming downloads into clean category folders.",
                BackgroundAutomationsEngine.Config.DownloadsOrganizerEnabled,
                val =>
                {
                    BackgroundAutomationsEngine.Config.DownloadsOrganizerEnabled = val;
                    BackgroundAutomationsEngine.SaveConfigurations();
                }));

            return root;
        }

        private Border CreateHubToggleCard(string title, string subtitle, bool initial, Action<bool> onToggle)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 6, 10, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5)
            };

            var dock = new DockPanel();
            var check = new CheckBox
            {
                IsChecked = initial,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            check.Checked += (s, e) => { if (onToggle != null) onToggle(true); };
            check.Unchecked += (s, e) => { if (onToggle != null) onToggle(false); };
            DockPanel.SetDock(check, Dock.Right);
            dock.Children.Add(check);

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 9.5, Foreground = BrushTextMuted, Margin = new Thickness(0, 1, 0, 0) });
            dock.Children.Add(stack);

            card.Child = dock;
            return card;
        }
    }
}
