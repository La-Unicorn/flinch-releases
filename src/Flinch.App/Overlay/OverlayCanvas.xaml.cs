using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Flinch.Core.Contracts;

namespace Flinch_App.Overlay
{
    public sealed partial class OverlayCanvas : UserControl
    {
        public event EventHandler? DismissRequested;
        public event EventHandler<bool>? ClickThroughToggled;

        private bool _isClickThrough;

        public OverlayCanvas()
        {
            this.InitializeComponent();
        }

        public void RenderScene(OverlayScene scene, double canvasWidth, double canvasHeight)
        {
            ShapesCanvas.Children.Clear();

            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            foreach (var shape in scene.Shapes)
            {
                RenderSingleShape(shape, canvasWidth, canvasHeight, animate: false);
            }
        }

        public void AddShapeAnimated(OverlayShape shape, double canvasWidth, double canvasHeight)
        {
            if (canvasWidth <= 0 || canvasHeight <= 0) return;
            RenderSingleShape(shape, canvasWidth, canvasHeight, animate: true);
        }

        public void ClearShapes()
        {
            ShapesCanvas.Children.Clear();
        }

        private void RenderSingleShape(OverlayShape shape, double w, double h, bool animate)
        {
            var brush = ParseColorBrush(shape.ColorHex);

            // Compute physical pixels from normalized coordinates
            double px = shape.Rect.X * w;
            double py = shape.Rect.Y * h;
            double pw = Math.Max(20, shape.Rect.Width * w);
            double ph = Math.Max(20, shape.Rect.Height * h);

            UIElement? primaryElement = null;

            switch (shape.ShapeType)
            {
                case OverlayShapeType.Outline:
                    primaryElement = RenderOutline(shape, px, py, pw, ph, brush);
                    break;

                case OverlayShapeType.Highlight:
                    primaryElement = RenderHighlight(shape, px, py, pw, ph, brush);
                    break;

                case OverlayShapeType.Arrow:
                    primaryElement = RenderArrow(shape, w, h, brush);
                    break;

                case OverlayShapeType.Callout:
                    primaryElement = RenderCallout(shape, px, py, pw, ph, brush);
                    break;

                case OverlayShapeType.Equation:
                    primaryElement = RenderEquationCard(shape, px, py, pw, ph, brush);
                    break;

                case OverlayShapeType.Circle:
                    primaryElement = RenderCircle(shape, px, py, pw, ph, brush);
                    break;
            }

            if (animate && primaryElement != null)
            {
                ApplyEntranceAnimation(primaryElement, py + 15, py);
            }
        }

        private static void ApplyEntranceAnimation(UIElement element, double startY, double targetY)
        {
            bool isReducedMotion = false;
            try
            {
                var uiSettings = new Windows.UI.ViewManagement.UISettings();
                isReducedMotion = !uiSettings.AnimationsEnabled;
            }
            catch { }

            if (isReducedMotion)
            {
                element.Opacity = 1.0;
                return;
            }

            element.Opacity = 0.0;
            var transform = new TranslateTransform { Y = startY - targetY };
            element.RenderTransform = transform;

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

            var fadeAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeAnim, element);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeAnim, "Opacity");
            sb.Children.Add(fadeAnim);

            var slideAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = startY - targetY,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slideAnim, transform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slideAnim, "Y");
            sb.Children.Add(slideAnim);

            sb.Begin();
        }

        private UIElement RenderOutline(OverlayShape shape, double x, double y, double width, double height, SolidColorBrush brush)
        {
            var border = new Border
            {
                Width = width,
                Height = height,
                BorderBrush = brush,
                BorderThickness = new Thickness(2.5),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(20, brush.Color.R, brush.Color.G, brush.Color.B))
            };

            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            ShapesCanvas.Children.Add(border);

            if (!string.IsNullOrWhiteSpace(shape.Label))
            {
                var labelPill = CreateLabelPill(shape.Label, shape.StepIndex, brush);
                Canvas.SetLeft(labelPill, x);
                Canvas.SetTop(labelPill, Math.Max(0, y - 28));
                ShapesCanvas.Children.Add(labelPill);
            }
            return border;
        }

        private UIElement RenderHighlight(OverlayShape shape, double x, double y, double width, double height, SolidColorBrush brush)
        {
            var highlight = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(60, brush.Color.R, brush.Color.G, brush.Color.B)),
                BorderBrush = brush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6)
            };

            Canvas.SetLeft(highlight, x);
            Canvas.SetTop(highlight, y);
            ShapesCanvas.Children.Add(highlight);

            if (!string.IsNullOrWhiteSpace(shape.Label))
            {
                var labelPill = CreateLabelPill(shape.Label, shape.StepIndex, brush);
                Canvas.SetLeft(labelPill, x);
                Canvas.SetTop(labelPill, Math.Max(0, y - 26));
                ShapesCanvas.Children.Add(labelPill);
            }
            return highlight;
        }

        private UIElement RenderArrow(OverlayShape shape, double w, double h, SolidColorBrush brush)
        {
            double fromX = (shape.From?.X ?? shape.Rect.X) * w;
            double fromY = (shape.From?.Y ?? shape.Rect.Y) * h;
            double toX = (shape.To?.X ?? (shape.Rect.X + shape.Rect.Width)) * w;
            double toY = (shape.To?.Y ?? (shape.Rect.Y + shape.Rect.Height)) * h;

            var container = new Canvas();

            var line = new Line
            {
                X1 = fromX,
                Y1 = fromY,
                X2 = toX,
                Y2 = toY,
                Stroke = brush,
                StrokeThickness = 3
            };
            container.Children.Add(line);

            // Draw arrowhead tip
            double angle = Math.Atan2(toY - fromY, toX - fromX);
            double arrowLength = 14;
            double arrowAngle = Math.PI / 6; // 30 degrees

            var pathFigure = new PathFigure { StartPoint = new Windows.Foundation.Point(toX, toY) };
            pathFigure.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(
                    toX - arrowLength * Math.Cos(angle - arrowAngle),
                    toY - arrowLength * Math.Sin(angle - arrowAngle))
            });
            pathFigure.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(
                    toX - arrowLength * Math.Cos(angle + arrowAngle),
                    toY - arrowLength * Math.Sin(angle + arrowAngle))
            });
            pathFigure.IsClosed = true;

            var pathGeometry = new PathGeometry();
            pathGeometry.Figures.Add(pathFigure);

            var arrowHead = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = pathGeometry,
                Fill = brush
            };
            container.Children.Add(arrowHead);

            if (!string.IsNullOrWhiteSpace(shape.Label))
            {
                var labelPill = CreateLabelPill(shape.Label, shape.StepIndex, brush);
                Canvas.SetLeft(labelPill, (fromX + toX) / 2);
                Canvas.SetTop(labelPill, ((fromY + toY) / 2) - 24);
                container.Children.Add(labelPill);
            }

            ShapesCanvas.Children.Add(container);
            return container;
        }

        private UIElement RenderCallout(OverlayShape shape, double x, double y, double width, double height, SolidColorBrush brush)
        {
            var card = new Border
            {
                Padding = new Thickness(12, 8, 12, 8),
                Background = new SolidColorBrush(Color.FromArgb(235, 25, 25, 30)),
                BorderBrush = brush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                MinWidth = 160
            };

            var stack = new StackPanel { Spacing = 4 };
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            headerStack.Children.Add(new Border
            {
                Background = brush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = $"Step {shape.StepIndex}",
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.Black)
                }
            });

            headerStack.Children.Add(new TextBlock
            {
                Text = shape.Label ?? "Information",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White)
            });

            stack.Children.Add(headerStack);
            card.Child = stack;

            Canvas.SetLeft(card, x + width + 12);
            Canvas.SetTop(card, y);
            ShapesCanvas.Children.Add(card);
            return card;
        }

        private UIElement RenderEquationCard(OverlayShape shape, double x, double y, double width, double height, SolidColorBrush brush)
        {
            var card = new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                Background = new SolidColorBrush(Color.FromArgb(240, 20, 20, 26)),
                BorderBrush = brush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10)
            };

            var stack = new StackPanel { Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(shape.Label))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = shape.Label,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = brush
                });
            }

            string rawMath = shape.Latex ?? shape.Label ?? "x";
            string mathText = FormatSafeMathDisplayText(rawMath);
            stack.Children.Add(new TextBlock
            {
                Text = mathText,
                FontFamily = new FontFamily("Cambria Math, Segoe UI Symbol, Consolas"),
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White)
            });

            card.Child = stack;

            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);
            ShapesCanvas.Children.Add(card);
            return card;
        }

        public static string FormatSafeMathDisplayText(string rawLatex)
        {
            if (string.IsNullOrWhiteSpace(rawLatex)) return string.Empty;
            var s = rawLatex.Trim();
            s = s.Replace(@"\cdot", "·")
                 .Replace(@"\pm", "±")
                 .Replace(@"\Delta", "Δ")
                 .Replace(@"\times", "×")
                 .Replace(@"\div", "÷")
                 .Replace(@"\approx", "≈")
                 .Replace(@"\neq", "≠")
                 .Replace(@"\leq", "≤")
                 .Replace(@"\geq", "≥");

            s = s.Replace("^{2}", "²").Replace("^2", "²")
                 .Replace("^{3}", "³").Replace("^3", "³")
                 .Replace("^{0}", "⁰").Replace("^{1}", "¹")
                 .Replace("_{1}", "₁").Replace("_1", "₁")
                 .Replace("_{2}", "₂").Replace("_2", "₂")
                 .Replace("_{0}", "₀").Replace("_0", "₀");

            while (s.Contains(@"\frac{"))
            {
                int fracIdx = s.IndexOf(@"\frac{");
                int numEnd = s.IndexOf('}', fracIdx + 6);
                if (numEnd > 0 && numEnd + 1 < s.Length && s[numEnd + 1] == '{')
                {
                    int denEnd = s.IndexOf('}', numEnd + 2);
                    if (denEnd > 0)
                    {
                        string num = s.Substring(fracIdx + 6, numEnd - (fracIdx + 6));
                        string den = s.Substring(numEnd + 2, denEnd - (numEnd + 2));
                        s = s.Remove(fracIdx, denEnd + 1 - fracIdx).Insert(fracIdx, $"({num} / {den})");
                        continue;
                    }
                }
                break;
            }

            while (s.Contains(@"\sqrt{"))
            {
                int sqIdx = s.IndexOf(@"\sqrt{");
                int sqEnd = s.IndexOf('}', sqIdx + 6);
                if (sqEnd > 0)
                {
                    string inner = s.Substring(sqIdx + 6, sqEnd - (sqIdx + 6));
                    s = s.Remove(sqIdx, sqEnd + 1 - sqIdx).Insert(sqIdx, $"√({inner})");
                    continue;
                }
                break;
            }

            return s;
        }

        private UIElement RenderCircle(OverlayShape shape, double x, double y, double width, double height, SolidColorBrush brush)
        {
            double diameter = Math.Max(width, height);
            var ellipse = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Stroke = brush,
                StrokeThickness = 2.5,
                Fill = new SolidColorBrush(Color.FromArgb(25, brush.Color.R, brush.Color.G, brush.Color.B))
            };

            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            ShapesCanvas.Children.Add(ellipse);
            return ellipse;
        }

        private static Border CreateLabelPill(string label, int stepIndex, SolidColorBrush brush)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 20, 20, 25)),
                BorderBrush = brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{stepIndex}.",
                            FontSize = 10,
                            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                            Foreground = brush
                        },
                        new TextBlock
                        {
                            Text = label,
                            FontSize = 11,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Colors.White)
                        }
                    }
                }
            };
        }

        private static SolidColorBrush ParseColorBrush(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return new SolidColorBrush(Color.FromArgb(255, 61, 220, 132));

            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, null, out byte b))
            {
                return new SolidColorBrush(Color.FromArgb(255, r, g, b));
            }

            return new SolidColorBrush(Color.FromArgb(255, 61, 220, 132));
        }

        private void OnDismissClicked(object sender, RoutedEventArgs e)
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnClickThroughToggleClicked(object sender, RoutedEventArgs e)
        {
            _isClickThrough = !_isClickThrough;
            ClickThroughText.Text = _isClickThrough ? "Click-Through: ON" : "Click-Through: OFF";
            ClickThroughIcon.Glyph = _isClickThrough ? "\uE73E" : "\uE7C5";
            ClickThroughToggled?.Invoke(this, _isClickThrough);
        }
    }
}
