// ============================================================================
// FLINCH NOTCH HUB: Pure MacBook Notch Widening Control Center
// 100% Faithful Vector Implementation of "flinch-notch.svg" (1430 x 192)
// C# 5 Compatible for .NET Framework 4.0 / 4.8
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
        private Canvas _hubSvgCanvas;
        private Border _hubFlyoutHost;
        private ContentControl _hubFlyoutContent;
        private bool _isHubFlyoutOpen = false;
        private string _activeHubFlyoutType = null;


        // Customization Theme States
        private int _hubAccentIndex = 0;
        private static readonly Color[] HubAccentColors = new Color[]
        {
            Color.FromRgb(0x3D, 0xDC, 0x84), // Mint
            Color.FromRgb(0xA8, 0x55, 0xF7), // Purple / Violet
            Color.FromRgb(0x38, 0xBD, 0xF8), // Sky Blue
            Color.FromRgb(0xFB, 0x92, 0x3C), // Amber
            Color.FromRgb(0xF8, 0x71, 0x71)  // Coral
        };

        // Live Dynamic Rows
        private TextBlock _hubHistoryQ1;
        private TextBlock _hubHistoryT1;
        private TextBlock _hubHistoryQ2;
        private TextBlock _hubHistoryT2;
        private TextBlock _hubHistoryQ3;
        private TextBlock _hubHistoryT3;
        private Ellipse _hubGmailStatusDot;

        // Gmail inputs in Hub Flyout
        private TextBox _hubEmailBox;
        private PasswordBox _hubPassBox;
        private TextBlock _hubEmailFeedbackText;
        private TextBlock _hubGmailStatusHeader;
        private StackPanel _hubInboxCardsPanel;

        // Fonts
        private static readonly FontFamily AppleFont = new FontFamily("-apple-system, 'SF Pro Display', 'SF Pro Text', Inter, 'Segoe UI', 'Helvetica Neue', Arial, sans-serif");

        // =====================================================================
        // BUILD HUB EXPANDED PANEL (Directly inside notch content host)
        // Scaled to sleek compact size (860 x 116)
        // =====================================================================
        private Grid BuildHubExpandedPanel()
        {
            var root = new Grid
            {
                Width = 860,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
                Opacity = 0.0
            };

            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(116) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // Dropdown Flyout

            // Row 0: SVG Canvas (1430 x 192) scaled down smoothly with a Viewbox to 860 x 116
            _hubSvgCanvas = BuildHubSvgCanvas();
            var viewbox = new Viewbox
            {
                Width = 860,
                Height = 116,
                Stretch = Stretch.Uniform,
                Child = _hubSvgCanvas
            };
            Grid.SetRow(viewbox, 0);
            root.Children.Add(viewbox);

            // Row 1: Dropdown Flyout Host
            _hubFlyoutHost = new Border
            {
                Width = 780,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x18)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0, 0, 18, 18),
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(24, 12, 24, 16),
                Margin = new Thickness(24, -2, 24, 0),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 6, Opacity = 0.4 }
            };
            _hubFlyoutContent = new ContentControl();
            _hubFlyoutHost.Child = _hubFlyoutContent;
            Grid.SetRow(_hubFlyoutHost, 1);
            root.Children.Add(_hubFlyoutHost);

            return root;
        }

        // =====================================================================
        // 1430 x 192 SVG CANVAS (Pixel-identical to flinch-notch.svg)
        // =====================================================================
        private Canvas BuildHubSvgCanvas()
        {
            var canvas = new Canvas
            {
                Width = 1430,
                Height = 192,
                ClipToBounds = false
            };

            // 1. Background Path with camera notch cutout (M28 0 L551 0 ...)
            var panelPath = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M28 0 L551 0 Q565 0 565 14 L565 18 Q565 40 587 40 L843 40 Q865 40 865 18 L865 14 Q865 0 879 0 L1402 0 Q1430 0 1430 28 L1430 152 Q1430 192 1390 192 L40 192 Q0 192 0 152 L0 28 Q0 0 28 0 Z"),
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x1F, 0x1F, 0x21), 0.0),
                        new GradientStop(Color.FromRgb(0x1A, 0x1A, 0x1B), 1.0)
                    }
                },
                Stroke = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), // stroke="#ffffff" stroke-opacity="0.07"
                StrokeThickness = 1.5,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 270,
                    ShadowDepth = 8,
                    BlurRadius = 20,
                    Opacity = 0.35
                }
            };
            canvas.Children.Add(panelPath);

            // 2. Dividers (4 vertical lines)
            canvas.Children.Add(CreateDivider(361, 22, 361, 170));
            canvas.Children.Add(CreateDivider(595, 60, 595, 170));
            canvas.Children.Add(CreateDivider(845, 60, 845, 170));
            canvas.Children.Add(CreateDivider(1194, 22, 1194, 170));

            // =================================================================
            // COLUMN 0: CHAT HISTORY (x: 0 to 361)
            // =================================================================
            // Header
            var historyHeader = new Canvas { Cursor = Cursors.Hand };
            historyHeader.Children.Add(CreateSvgPath("M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2zm0 4v6l4 2", 50, 20, 27, 27, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)), 1.8));
            var historyTitle = new TextBlock
            {
                Text = "Chat History",
                FontSize = 22.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                FontFamily = AppleFont
            };
            Canvas.SetLeft(historyTitle, 90);
            Canvas.SetTop(historyTitle, 16);
            historyHeader.Children.Add(historyTitle);
            historyHeader.Children.Add(CreateSvgPath("m6 9 6 6 6-6", 224, 25, 22, 22, new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), 1.8));
            historyHeader.MouseLeftButtonDown += (s, e) => { e.Handled = true; ToggleHubFlyout("history"); };
            canvas.Children.Add(historyHeader);

            // Row 1: "What is flinch?"
            var row1 = CreateHistoryRowItem(
                "M7.9 20A9 9 0 1 0 4 16.1L2 22Z M8 12h.01M12 12h.01M16 12h.01",
                52, 69, "What is flinch?", "2m ago", 86, out _hubHistoryQ1, out _hubHistoryT1);
            canvas.Children.Add(row1);

            // Row 2: "Create a portfolio site"
            var row2 = CreateHistoryRowItem(
                "M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2z M14 2v6h6M8 13h8M8 17h5",
                52, 106, "Create a portfolio site", "5m ago", 123, out _hubHistoryQ2, out _hubHistoryT2);
            canvas.Children.Add(row2);

            // Row 3: "Explain this code"
            var row3 = CreateHistoryRowItem(
                "m16 18 6-6-6-6M8 6l-6 6 6 6M14 4l-4 16",
                52, 143, "Explain this code", "12m ago", 160, out _hubHistoryQ3, out _hubHistoryT3);
            canvas.Children.Add(row3);

            // =================================================================
            // COLUMN 1: INTEGRATIONS (x: 361 to 595)
            // =================================================================
            // Header
            var intHeader = new Canvas { Cursor = Cursors.Hand };
            intHeader.Children.Add(CreateSvgPath(
                "M19.439 7.85c-.049.322.059.648.289.878l1.568 1.568c.47.47.706 1.087.706 1.704s-.235 1.233-.706 1.704l-1.611 1.611a.98.98 0 0 1-.837.276c-.47-.07-.802-.48-.968-.925a2.501 2.501 0 1 0-3.214 3.214c.446.166.855.497.925.968a.979.979 0 0 1-.276.837l-1.61 1.61a2.404 2.404 0 0 1-1.705.707 2.402 2.402 0 0 1-1.704-.706l-1.568-1.568a1.026 1.026 0 0 0-.877-.29c-.493.074-.84.504-1.02.968a2.5 2.5 0 1 1-3.237-3.237c.464-.18.894-.527.967-1.02a1.026 1.026 0 0 0-.289-.877l-1.568-1.568A2.402 2.402 0 0 1 1.998 12c0-.617.236-1.234.706-1.704L4.23 8.77c.24-.24.581-.353.917-.303.515.077.877.528 1.073 1.01a2.5 2.5 0 1 0 3.259-3.259c-.482-.196-.933-.558-1.01-1.073-.05-.336.062-.676.303-.917l1.525-1.525A2.402 2.402 0 0 1 12 1.998c.617 0 1.234.236 1.704.706l1.568 1.568c.23.23.556.338.877.29.493-.074.84-.504 1.02-.968a2.5 2.5 0 1 1 3.236 3.236c-.464.18-.894.527-.967 1.02Z",
                383, 20, 27, 27, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)), 1.8));
            var intTitle = new TextBlock
            {
                Text = "Integrations",
                FontSize = 22.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                FontFamily = AppleFont
            };
            Canvas.SetLeft(intTitle, 419);
            Canvas.SetTop(intTitle, 16);
            intHeader.Children.Add(intTitle);
            intHeader.Children.Add(CreateSvgPath("m6 9 6 6 6-6", 541, 25, 22, 22, new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), 1.8));
            intHeader.MouseLeftButtonDown += (s, e) => { e.Handled = true; ToggleHubFlyout("gmail"); };
            canvas.Children.Add(intHeader);

            // Notion Tile (x: 394, y: 74, w: 40, h: 40, rx: 9)
            var notionBorder = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(9),
                Background = Brushes.White,
                Cursor = Cursors.Hand,
                ToolTip = "Notion Workspace"
            };
            var notionPath = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M4.459 4.208c.746.606 1.026.56 2.428.466l13.215-.793c.28 0 .047-.28-.046-.326L17.86 1.968c-.42-.326-.981-.7-2.055-.607L3.01 2.295c-.466.046-.56.28-.374.466zm.793 3.08v13.904c0 .747.373 1.027 1.214.98l14.523-.84c.841-.046.935-.56.935-1.167V6.354c0-.606-.233-.933-.748-.887l-15.177.887c-.56.047-.747.327-.747.933zm14.337.745c.093.42 0 .84-.42.888l-.7.14v10.264c-.608.327-1.168.514-1.635.514-.748 0-.935-.234-1.495-.933l-4.577-7.186v6.952L12.21 19s0 .84-1.168.84l-3.222.186c-.093-.186 0-.653.327-.746l.84-.233V9.854L7.822 9.76c-.094-.42.14-1.026.793-1.073l3.456-.233 4.764 7.279v-6.44l-1.215-.139c-.093-.514.28-.887.747-.933zM1.936 1.035l13.31-.98c1.634-.14 2.055-.047 3.082.7l4.249 2.986c.7.513.934.653.934 1.213v16.378c0 1.026-.373 1.634-1.68 1.726l-15.458.934c-.98.047-1.448-.093-1.962-.747l-3.129-4.06c-.56-.747-.793-1.306-.793-1.96V2.667c0-.839.374-1.54 1.447-1.632z"),
                Fill = Brushes.Black,
                Stretch = Stretch.Uniform,
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            notionBorder.Child = notionPath;
            notionBorder.MouseLeftButtonDown += (s, e) => { e.Handled = true; Process.Start("https://notion.so"); };
            Canvas.SetLeft(notionBorder, 394);
            Canvas.SetTop(notionBorder, 74);
            canvas.Children.Add(notionBorder);

            // Spotify Tile (x: 458, y: 72, w: 52, h: 44, rx: 12)
            var spotifyBorder = new Border
            {
                Width = 52,
                Height = 44,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x2A, 0x21)),
                Cursor = Cursors.Hand,
                ToolTip = "Spotify Music"
            };
            var spotifyCanvas = new Canvas { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var spotifyCircle = new Ellipse { Width = 34, Height = 34, Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60)) };
            spotifyCanvas.Children.Add(spotifyCircle);
            spotifyCanvas.Children.Add(CreateSvgPath("M6 12 Q17 7 28 14", 0, 0, 34, 34, new SolidColorBrush(Color.FromRgb(0x0B, 0x1A, 0x10)), 3.2));
            spotifyCanvas.Children.Add(CreateSvgPath("M8 18 Q17 14 26 19", 0, 0, 34, 34, new SolidColorBrush(Color.FromRgb(0x0B, 0x1A, 0x10)), 2.7));
            spotifyCanvas.Children.Add(CreateSvgPath("M10 23 Q17 21 24 24", 0, 0, 34, 34, new SolidColorBrush(Color.FromRgb(0x0B, 0x1A, 0x10)), 2.2));
            spotifyBorder.Child = spotifyCanvas;
            spotifyBorder.MouseLeftButtonDown += (s, e) => { e.Handled = true; Process.Start("https://open.spotify.com"); };
            Canvas.SetLeft(spotifyBorder, 458);
            Canvas.SetTop(spotifyBorder, 72);
            canvas.Children.Add(spotifyBorder);

            // Google Drive / Gmail Tile (x: 521.5, y: 74)
            var gdriveBorder = new Border
            {
                Width = 44,
                Height = 40,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x20, 0x2A)),
                Cursor = Cursors.Hand,
                ToolTip = "Google Drive & Gmail Integration"
            };
            var gdriveCanvas = new Canvas { Width = 30, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            gdriveCanvas.Children.Add(CreateSvgPathFilled("m2.2 22.3 1.3 2.2c.3.5.7.8 1.1 1.1l4.6-8h-9.2c0 .5.1 1 .4 1.5z", new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xDA))));
            gdriveCanvas.Children.Add(CreateSvgPathFilled("m14.5 8.3-4.6-8c-.5.3-.8.6-1.1 1.1l-8.5 14.7c-.3.5-.4 1-.4 1.5h9.2z", new SolidColorBrush(Color.FromRgb(0x00, 0xAC, 0x47))));
            gdriveCanvas.Children.Add(CreateSvgPathFilled("m24.5 25.6c.5-.3.8-.6 1.1-1.1l.5-.9 2.6-4.4c.3-.5.4-1 .4-1.5h-9.2l2 3.8z", new SolidColorBrush(Color.FromRgb(0xEA, 0x43, 0x35))));
            gdriveCanvas.Children.Add(CreateSvgPathFilled("m14.5 8.3 4.6-8c-.5-.3-1-.4-1.5-.4h-6.2c-.5 0-1 .1-1.5.4z", new SolidColorBrush(Color.FromRgb(0x00, 0x83, 0x2D))));
            gdriveCanvas.Children.Add(CreateSvgPathFilled("m20 17.7h-10.8l-4.6 8c.5.3 1 .4 1.5.4h17c.5 0 1-.1 1.5-.4z", new SolidColorBrush(Color.FromRgb(0x26, 0x84, 0xFC))));
            gdriveCanvas.Children.Add(CreateSvgPathFilled("m24.5 8.8-4.2-7.3c-.3-.5-.7-.8-1.1-1.1l-4.6 8 5.4 9.3h9.2c0-.5-.1-1-.4-1.5z", new SolidColorBrush(Color.FromRgb(0xFF, 0xBA, 0x00))));
            
            // Live Status Indicator Dot on Gmail
            _hubGmailStatusDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
            };
            Canvas.SetLeft(_hubGmailStatusDot, 22);
            Canvas.SetTop(_hubGmailStatusDot, 0);
            gdriveCanvas.Children.Add(_hubGmailStatusDot);

            gdriveBorder.Child = gdriveCanvas;
            gdriveBorder.MouseLeftButtonDown += (s, e) => { e.Handled = true; ToggleHubFlyout("gmail"); };
            Canvas.SetLeft(gdriveBorder, 521.5);
            Canvas.SetTop(gdriveBorder, 74);
            canvas.Children.Add(gdriveBorder);

            // Add Button (x: 388.75, y: 131.75, w: 180.5, h: 35.5, rx: 12)
            var addBtn = new Border
            {
                Width = 180.5,
                Height = 35.5,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x21)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1.5),
                Cursor = Cursors.Hand
            };
            var addStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            addStack.Children.Add(CreateSvgPath("M5 12h14M12 5v14", 0, 0, 18, 18, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)), 2.0));
            var addText = new TextBlock
            {
                Text = "Add",
                FontSize = 17,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)),
                FontFamily = AppleFont,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            addStack.Children.Add(addText);
            addBtn.Child = addStack;
            addBtn.MouseLeftButtonDown += (s, e) => { e.Handled = true; ToggleHubFlyout("gmail"); };
            Canvas.SetLeft(addBtn, 388.75);
            Canvas.SetTop(addBtn, 131.75);
            canvas.Children.Add(addBtn);

            // =================================================================
            // COLUMN 2: CENTER AREA (Empty MacBook Camera Bezel Notch)
            // =================================================================
            // Intentionally clean and empty between dividers x=595 and x=845,
            // faithfully preserving the top bezel camera notch cutout aesthetic
            // without any mic or listening status card cluttering the center.

            // =================================================================
            // COLUMN 3: CUSTOMIZATION (x: 845 to 1194)
            // =================================================================
            // Header
            var custHeader = new Canvas { Cursor = Cursors.Hand };
            custHeader.Children.Add(CreateSvgPath(
                "M21 3 11.5 12.5 M9.5 11.5c-2.2-.3-4 1-4.3 3.2-.2 1.6-1.1 2.8-2.7 3.3 3.8 2.3 8.6 1.4 9.4-2.4.3-1.3-.4-2.9-1.5-3.6",
                894, 20, 27, 27, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)), 1.8));
            var custTitle = new TextBlock
            {
                Text = "Customization",
                FontSize = 22.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                FontFamily = AppleFont
            };
            Canvas.SetLeft(custTitle, 933);
            Canvas.SetTop(custTitle, 16);
            custHeader.Children.Add(custTitle);
            custHeader.Children.Add(CreateSvgPath("m6 9 6 6 6-6", 1084, 25, 22, 22, new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), 1.8));
            custHeader.MouseLeftButtonDown += (s, e) => { e.Handled = true; CycleHubAccentColor(); };
            canvas.Children.Add(custHeader);

            // Sun Tile: rect x="883" y="79" w="56" h="56" rx="17" fill="#3a3a3c"
            var sunBorder = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
                Cursor = Cursors.Hand,
                ToolTip = "Light Appearance"
            };
            sunBorder.Child = CreateSvgPath(
                "M12 2a4 4 0 1 0 4 4 4 4 0 0 0-4-4zm0-2v2m0 16v2m-7.07-13.07 1.41 1.41m9.32 9.32 1.41 1.41M1 10h2m16 0h2M3.34 16.66l1.41-1.41m9.32-9.32 1.41-1.41",
                0, 0, 28, 28, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)), 1.8);
            sunBorder.MouseLeftButtonDown += (s, e) => { e.Handled = true; ShowAlert("theme", "Daylight visual theme activated.", "Theme"); };
            Canvas.SetLeft(sunBorder, 883);
            Canvas.SetTop(sunBorder, 79);
            canvas.Children.Add(sunBorder);

            // Moon Tile: rect x="955" y="79" w="56" h="56" rx="17" fill="#2a2a2c"
            var moonBorder = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2C)),
                Cursor = Cursors.Hand,
                ToolTip = "Dark Appearance"
            };
            moonBorder.Child = CreateSvgPath(
                "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z",
                0, 0, 28, 28, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)), 1.8);
            moonBorder.MouseLeftButtonDown += (s, e) => { e.Handled = true; ShowAlert("theme", "Midnight dark theme activated.", "Theme"); };
            Canvas.SetLeft(moonBorder, 955);
            Canvas.SetTop(moonBorder, 79);
            canvas.Children.Add(moonBorder);

            // Gradient Orb: circle cx="1055" cy="107" r="29" fill="#1a1a1b" stroke="#ffffff" stroke-opacity="0.35"
            var orbOuter = new Canvas
            {
                Width = 58,
                Height = 58,
                Cursor = Cursors.Hand,
                ToolTip = "Cycle Voice & Glow Palette"
            };
            var orbBorderCircle = new Ellipse
            {
                Width = 58,
                Height = 58,
                Fill = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1B)),
                Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                StrokeThickness = 1.5
            };
            orbOuter.Children.Add(orbBorderCircle);

            var orbInner = new Ellipse
            {
                Width = 38,
                Height = 38,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0xFF, 0x4F, 0x9A), 0.0),
                        new GradientStop(Color.FromRgb(0x9B, 0x5C, 0xFF), 0.5),
                        new GradientStop(Color.FromRgb(0x3B, 0x7B, 0xFF), 1.0)
                    }
                }
            };
            Canvas.SetLeft(orbInner, 10);
            Canvas.SetTop(orbInner, 10);
            orbOuter.Children.Add(orbInner);
            orbOuter.MouseLeftButtonDown += (s, e) => { e.Handled = true; CycleHubAccentColor(); };
            Canvas.SetLeft(orbOuter, 1026);
            Canvas.SetTop(orbOuter, 78);
            canvas.Children.Add(orbOuter);

            // Typography Tile: rect x="1100" y="79" w="56" h="56" rx="17" fill="#2a2a2c"
            var typeBorder = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2C)),
                Cursor = Cursors.Hand,
                ToolTip = "Adjust Interface Scale"
            };
            var typeText = new TextBlock
            {
                Text = "Aa",
                FontSize = 25,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)),
                FontFamily = AppleFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            typeBorder.Child = typeText;
            typeBorder.MouseLeftButtonDown += (s, e) => { e.Handled = true; ShowAlert("scale", "Font scale set to standard.", "Display"); };
            Canvas.SetLeft(typeBorder, 1100);
            Canvas.SetTop(typeBorder, 79);
            canvas.Children.Add(typeBorder);

            // =================================================================
            // COLUMN 4: SETTINGS (x: 1194 to 1430)
            // =================================================================
            // Header
            var settHeader = new Canvas { Cursor = Cursors.Hand };
            settHeader.Children.Add(CreateSvgPath(
                "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 9a3 3 0 1 1-3 3 3 3 0 0 1 3-3z",
                1231, 20, 27, 27, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF3)), 1.8));
            var settTitle = new TextBlock
            {
                Text = "Settings",
                FontSize = 22.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                FontFamily = AppleFont
            };
            Canvas.SetLeft(settTitle, 1269);
            Canvas.SetTop(settTitle, 16);
            settHeader.Children.Add(settTitle);
            settHeader.Children.Add(CreateSvgPath("m6 9 6 6 6-6", 1359, 25, 22, 22, new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), 1.8));
            settHeader.MouseLeftButtonDown += (s, e) => { e.Handled = true; ToggleHubFlyout("settings"); };
            canvas.Children.Add(settHeader);

            // Row 1: General (x: 1232, y: 69)
            canvas.Children.Add(CreateSettingsRowItem(
                "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 9a3 3 0 1 1-3 3 3 3 0 0 1 3-3z",
                1232, 69, "General", 86, () => ToggleHubFlyout("settings")));

            // Row 2: Hotkeys (x: 1232, y: 106)
            canvas.Children.Add(CreateSettingsRowItem(
                "M2 6a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6zm4 2h.01M10 8h.01M14 8h.01M18 8h.01M8 12h.01M12 12h.01M16 12h.01M7 16h10",
                1232, 106, "Hotkeys", 123, () => ToggleHubFlyout("hotkeys")));

            // Row 3: About Flinch (x: 1232, y: 143)
            canvas.Children.Add(CreateSettingsRowItem(
                "M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2zm0 14v-4m0-4h.01",
                1232, 143, "About Flinch", 160, () => ShowAlert("about", "Flinch v" + BuildConfig.BuildVersion + " · Autonomous Voice Companion", "About")));

            return canvas;
        }

        // =====================================================================
        // SVG HELPERS
        // =====================================================================
        private static Line CreateDivider(double x1, double y1, double x2, double y2)
        {
            return new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush(Color.FromArgb(23, 255, 255, 255)), // stroke-opacity="0.09"
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }

        private static Viewbox CreateSvgPath(string pathData, double x, double y, double w, double h, Brush stroke, double strokeThickness = 1.8, Brush fill = null)
        {
            var vb = new Viewbox
            {
                Width = w,
                Height = h
            };
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathData),
                Stroke = stroke,
                StrokeThickness = strokeThickness,
                Fill = fill ?? Brushes.Transparent,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            vb.Child = path;
            Canvas.SetLeft(vb, x);
            Canvas.SetTop(vb, y);
            return vb;
        }

        private static Viewbox CreateSvgPathFilled(string pathData, Brush fill)
        {
            var vb = new Viewbox { Width = 30, Height = 26 };
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathData),
                Fill = fill,
                Stretch = Stretch.Uniform
            };
            vb.Child = path;
            return vb;
        }

        private Canvas CreateHistoryRowItem(string iconData, double iconX, double iconY, string query, string time, double textY, out TextBlock queryOut, out TextBlock timeOut)
        {
            var row = new Canvas { Cursor = Cursors.Hand };

            // Icon
            row.Children.Add(CreateSvgPath(iconData, iconX, iconY, 22, 22, new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE7)), 1.8));

            // Query Text
            var qText = new TextBlock
            {
                Text = query,
                FontSize = 17,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEA)),
                FontFamily = AppleFont
            };
            Canvas.SetLeft(qText, 90);
            Canvas.SetTop(qText, textY - 21);
            row.Children.Add(qText);
            queryOut = qText;

            // Time text
            var tText = new TextBlock
            {
                Text = time,
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                FontFamily = AppleFont,
                TextAlignment = TextAlignment.Right,
                Width = 70
            };
            Canvas.SetLeft(tText, 265);
            Canvas.SetTop(tText, textY - 20);
            row.Children.Add(tText);
            timeOut = tText;

            row.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                string q = qText.Text;
                ShowAlert("history", q, "Recent Query");
            };

            return row;
        }

        private Canvas CreateSettingsRowItem(string iconData, double iconX, double iconY, string label, double textY, Action onClick)
        {
            var row = new Canvas { Cursor = Cursors.Hand };

            // Icon
            row.Children.Add(CreateSvgPath(iconData, iconX, iconY, 22, 22, new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE7)), 1.8));

            // Label Text
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 17,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEA)),
                FontFamily = AppleFont
            };
            Canvas.SetLeft(lbl, 1269);
            Canvas.SetTop(lbl, textY - 21);
            row.Children.Add(lbl);

            row.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (onClick != null) onClick();
            };

            return row;
        }

        // =====================================================================
        // INTERACTIONS & FLYOUTS
        // =====================================================================
        private void CycleHubAccentColor()
        {
            _hubAccentIndex = (_hubAccentIndex + 1) % HubAccentColors.Length;
            Color newCol = HubAccentColors[_hubAccentIndex];
            ShowAlert("theme", "Accent color updated.", "Accent");
        }

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
            _targetH = 260; // Deepen notch downwards
        }

        private void CloseHubFlyout()
        {
            _isHubFlyoutOpen = false;
            _activeHubFlyoutType = null;
            _hubFlyoutHost.Visibility = Visibility.Collapsed;
            _targetH = 116; // Spring back to compact 116px SVG panel
        }

        private bool HasFocusedInputInHub()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return (focused is TextBox || focused is PasswordBox);
        }

        private void RefreshHubData()
        {
            // Populate recent turns if available
            try
            {
                var turns = FlinchSkills.GetRecentTurns(3);
                if (turns != null && turns.Count > 0)
                {
                    if (turns.Count >= 1 && _hubHistoryQ1 != null) { _hubHistoryQ1.Text = TruncateHubText(turns[0].u, 24); _hubHistoryT1.Text = FormatHubIsoTime(turns[0].t); }
                    if (turns.Count >= 2 && _hubHistoryQ2 != null) { _hubHistoryQ2.Text = TruncateHubText(turns[1].u, 24); _hubHistoryT2.Text = FormatHubIsoTime(turns[1].t); }
                    if (turns.Count >= 3 && _hubHistoryQ3 != null) { _hubHistoryQ3.Text = TruncateHubText(turns[2].u, 24); _hubHistoryT3.Text = FormatHubIsoTime(turns[2].t); }
                }
            }
            catch { }

            if (_hubGmailStatusDot != null)
            {
                _hubGmailStatusDot.Fill = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            }

        }

        private static string FormatHubIsoTime(string iso)
        {
            DateTime dt;
            if (!string.IsNullOrWhiteSpace(iso) && DateTime.TryParse(iso, out dt))
            {
                var span = DateTime.UtcNow - dt;
                if (span.TotalMinutes < 1) return "Just now";
                if (span.TotalMinutes < 60) return ((int)span.TotalMinutes) + "m ago";
                if (span.TotalHours < 24) return ((int)span.TotalHours) + "h ago";
                return ((int)span.TotalDays) + "d ago";
            }
            return "2m ago";
        }

        private static string TruncateHubText(string txt, int max)
        {
            if (string.IsNullOrWhiteSpace(txt)) return "";
            txt = txt.Trim();
            return (txt.Length > max) ? (txt.Substring(0, max) + "…") : txt;
        }

        private static string FormatHubTime(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "Just now";
            if (span.TotalMinutes < 60) return ((int)span.TotalMinutes) + "m ago";
            if (span.TotalHours < 24) return ((int)span.TotalHours) + "h ago";
            return ((int)span.TotalDays) + "d ago";
        }

        // ---------------------------------------------------------------------
        // FLYOUT 1: GMAIL
        // ---------------------------------------------------------------------
        private UIElement BuildHubGmailFlyout()
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
            closeBtn.Child = new TextBlock { Text = "\u2715", FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.MouseLeftButtonDown += (s, e) => CloseHubFlyout();
            DockPanel.SetDock(closeBtn, Dock.Right);
            header.Children.Add(closeBtn);

            _hubGmailStatusHeader = new TextBlock
            {
                Text = BackgroundAutomationsEngine.EmailConfig.IsConnected
                    ? "\u2713 Gmail Connected (" + BackgroundAutomationsEngine.EmailConfig.EmailAddress + ")"
                    : "\U0001F4E7 Connect Gmail for Voice Inbox & Autonomous Actions",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BackgroundAutomationsEngine.EmailConfig.IsConnected ? BrushMint : Brushes.White
            };
            header.Children.Add(_hubGmailStatusHeader);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var formStack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            _hubEmailBox = new TextBox
            {
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x1F, 0x27)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x38, 0x44)),
                Padding = new Thickness(8, 4, 8, 4),
                Text = BackgroundAutomationsEngine.EmailConfig.EmailAddress ?? ""
            };
            formStack.Children.Add(new TextBlock { Text = "Gmail Address", FontSize = 10.5, Foreground = BrushTextMuted, Margin = new Thickness(0, 0, 0, 3) });
            formStack.Children.Add(_hubEmailBox);

            _hubPassBox = new PasswordBox
            {
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x1F, 0x27)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x38, 0x44)),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 6, 0, 0)
            };
            formStack.Children.Add(new TextBlock { Text = "Google App Password (16 chars)", FontSize = 10.5, Foreground = BrushTextMuted, Margin = new Thickness(0, 6, 0, 3) });
            formStack.Children.Add(_hubPassBox);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            btnRow.Children.Add(CreateHubActionButton("Save & Connect", BrushMint, Colors.Black, (s, e) => ConnectHubGmail()));
            btnRow.Children.Add(CreateHubActionButton("Send Test", new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Colors.White, (s, e) => SendHubTestEmail()));
            btnRow.Children.Add(CreateHubActionButton("Read Unread", new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Colors.White, (s, e) => FetchHubUnreadMessages()));
            formStack.Children.Add(btnRow);

            _hubEmailFeedbackText = new TextBlock { FontSize = 10.5, Foreground = BrushMint, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            formStack.Children.Add(_hubEmailFeedbackText);
            Grid.SetColumn(formStack, 0);
            contentGrid.Children.Add(formStack);

            _hubInboxCardsPanel = new StackPanel();
            var scroll = new ScrollViewer { Content = _hubInboxCardsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 125 };
            Grid.SetColumn(scroll, 1);
            contentGrid.Children.Add(scroll);

            Grid.SetRow(contentGrid, 1);
            root.Children.Add(contentGrid);

            return root;
        }

        private Border CreateHubActionButton(string label, Brush bg, Color fg, RoutedEventHandler onClick)
        {
            var btn = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            btn.Child = new TextBlock { Text = label, Foreground = new SolidColorBrush(fg), FontSize = 11, FontWeight = FontWeights.SemiBold };
            btn.MouseLeftButtonDown += (s, e) => { e.Handled = true; if (onClick != null) onClick(s, e); };
            return btn;
        }

        private void ConnectHubGmail()
        {
            string email = (_hubEmailBox.Text ?? "").Trim();
            string pass = _hubPassBox.Password ?? "";

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pass))
            {
                _hubEmailFeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                _hubEmailFeedbackText.Text = "Please enter both Gmail address and 16-character App Password.";
                return;
            }

            _hubEmailFeedbackText.Foreground = BrushMint;
            _hubEmailFeedbackText.Text = "Testing connection with Google IMAP / SMTP...";

            Task.Run(() =>
            {
                string err;
                int count;
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

                        _hubEmailFeedbackText.Text = string.Format("\u2713 Connected! Found {0} unread email(s).", count);
                        _hubGmailStatusDot.Fill = BrushMint;
                        _hubGmailStatusHeader.Text = "\u2713 Gmail Connected (" + email + ")";
                        _hubGmailStatusHeader.Foreground = BrushMint;
                    }
                    else
                    {
                        _hubEmailFeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                        _hubEmailFeedbackText.Text = "Connection failed: " + err;
                    }
                }));
            });
        }

        private void SendHubTestEmail()
        {
            string email = BackgroundAutomationsEngine.EmailConfig.EmailAddress;
            string pass = BackgroundAutomationsEngine.GetEmailPassword();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pass))
            {
                _hubEmailFeedbackText.Text = "Connect credentials first.";
                return;
            }

            Task.Run(() =>
            {
                string err;
                bool ok = DirectSmtpClient.SendGmailEmail(email, "Flinch Test Email", "Hello! This test confirms Flinch can send emails autonomously on your command.", out err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _hubEmailFeedbackText.Text = ok ? "\u2713 Test email delivered to your inbox!" : ("Error: " + err);
                }));
            });
        }

        private void FetchHubUnreadMessages()
        {
            string email = BackgroundAutomationsEngine.EmailConfig.EmailAddress;
            string pass = BackgroundAutomationsEngine.GetEmailPassword();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pass))
            {
                _hubEmailFeedbackText.Text = "Connect credentials first.";
                return;
            }

            Task.Run(() =>
            {
                string err;
                List<EmailMessageInfo> msgs;
                bool ok = DirectImapClient.FetchUnreadMessages("imap.gmail.com", 993, true, email, pass, 5, out msgs, out err);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _hubInboxCardsPanel.Children.Clear();
                    if (!ok || msgs == null || msgs.Count == 0)
                    {
                        _hubInboxCardsPanel.Children.Add(new TextBlock { Text = "Inbox clean! No unread messages.", Foreground = BrushTextMuted, FontSize = 11 });
                        return;
                    }
                    foreach (var msg in msgs)
                    {
                        var card = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x24)),
                            CornerRadius = new CornerRadius(5),
                            Padding = new Thickness(8, 5, 8, 5),
                            Margin = new Thickness(0, 0, 0, 4)
                        };
                        var sp = new StackPanel();
                        sp.Children.Add(new TextBlock { Text = msg.Subject, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontSize = 11 });
                        sp.Children.Add(new TextBlock { Text = "From: " + msg.From + " · " + msg.Snippet, Foreground = BrushTextMuted, FontSize = 9.5 });
                        card.Child = sp;
                        _hubInboxCardsPanel.Children.Add(card);
                    }
                }));
            });
        }

        // ---------------------------------------------------------------------
        // FLYOUT 2: HISTORY SEARCH
        // ---------------------------------------------------------------------
        private UIElement BuildHubHistoryFlyout()
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

            header.Children.Add(new TextBlock { Text = "\U0001F552 Full Chat & Interaction History", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            root.Children.Add(header);

            var turns = FlinchSkills.GetRecentTurns(10);
            var historyStack = new StackPanel();
            if (turns != null && turns.Count > 0)
            {
                foreach (var t in turns)
                {
                    var card = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 0, 0, 4),
                        Cursor = Cursors.Hand
                    };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = t.u, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontSize = 11.5 });
                    sp.Children.Add(new TextBlock { Text = t.a, Foreground = BrushTextMuted, FontSize = 10, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis });
                    card.Child = sp;
                    historyStack.Children.Add(card);
                }
            }
            else
            {
                historyStack.Children.Add(new TextBlock { Text = "No prior conversations yet. Ask Flinch anything!", Foreground = BrushTextMuted, FontSize = 11 });
            }

            var scroll = new ScrollViewer { Content = historyStack, Height = 125, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scroll);
            return root;
        }

        // ---------------------------------------------------------------------
        // FLYOUT 3: HOTKEYS
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

            header.Children.Add(new TextBlock { Text = "\u2328 Keyboard Shortcuts & Gestures", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            root.Children.Add(header);

            root.Children.Add(CreateHotkeyCard("Alt + V", "Hold to talk or tap to start recording voice query."));
            root.Children.Add(CreateHotkeyCard("Top Notch Hover", "Expands the 5-section control hub seamlessly."));
            root.Children.Add(CreateHotkeyCard("Escape", "Instantly cancels speech playback, alerts, and timers."));

            return root;
        }

        private Border CreateHotkeyCard(string key, string desc)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x22)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var dock = new DockPanel();
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2)
            };
            badge.Child = new TextBlock { Text = key, Foreground = BrushMint, FontSize = 11, FontWeight = FontWeights.Bold };
            DockPanel.SetDock(badge, Dock.Right);
            dock.Children.Add(badge);
            dock.Children.Add(new TextBlock { Text = desc, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            card.Child = dock;
            return card;
        }

        // ---------------------------------------------------------------------
        // FLYOUT 4: GENERAL SETTINGS
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
                val => AutoStartManager.SetAutoStart(val)));

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
                Margin = new Thickness(0, 0, 0, 5),
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
