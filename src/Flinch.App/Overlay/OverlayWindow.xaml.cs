using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using Flinch.Core.Contracts;

namespace Flinch_App.Overlay
{
    public sealed partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TRANSPARENT = 0x00000020L;
        private const long WS_EX_LAYERED = 0x00080000L;

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private readonly DispatcherTimer _autoDismissTimer;

        public OverlayWindow()
        {
            this.InitializeComponent();

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            ConfigureWindowFrame();
            ApplyNonActivatingStyles();
            EnableDwmTransparency();

            SceneCanvas.DismissRequested += (_, _) => Close();
            SceneCanvas.ClickThroughToggled += (_, isClickThrough) => SetClickThroughMode(isClickThrough);

            _autoDismissTimer = new DispatcherTimer();
            _autoDismissTimer.Tick += (_, _) =>
            {
                _autoDismissTimer.Stop();
                Close();
            };
        }

        private void ConfigureWindowFrame()
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
            }

            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        }

        private void ApplyNonActivatingStyles()
        {
            if (_hwnd == IntPtr.Zero) return;

            long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            exStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }

        private void EnableDwmTransparency()
        {
            if (_hwnd == IntPtr.Zero) return;

            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        }

        public void SetClickThroughMode(bool enable)
        {
            if (_hwnd == IntPtr.Zero) return;

            long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            if (enable)
            {
                exStyle |= WS_EX_TRANSPARENT;
            }
            else
            {
                exStyle &= ~WS_EX_TRANSPARENT;
            }
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }

        public void ShowScene(OverlayScene scene, RectInt32? targetBounds = null)
        {
            OpenEmptySession(targetBounds);

            double width = _appWindow.Size.Width;
            double height = _appWindow.Size.Height;
            SceneCanvas.RenderScene(scene, width, height);

            // Auto-dismiss countdown
            int duration = Math.Clamp(scene.DurationMs, 2000, 30000);
            _autoDismissTimer.Interval = TimeSpan.FromMilliseconds(duration);
            _autoDismissTimer.Start();
        }

        public void OpenEmptySession(RectInt32? targetBounds = null)
        {
            if (targetBounds.HasValue && targetBounds.Value.Width > 100 && targetBounds.Value.Height > 100)
            {
                _appWindow.MoveAndResize(targetBounds.Value);
            }
            else
            {
                var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                _appWindow.MoveAndResize(displayArea.WorkArea);
            }

            SceneCanvas.ClearShapes();
            this.Activate();
        }

        public void AddShape(OverlayShape shape)
        {
            double width = _appWindow.Size.Width;
            double height = _appWindow.Size.Height;
            SceneCanvas.AddShapeAnimated(shape, width, height);
        }

        public void ClearShapes()
        {
            SceneCanvas.ClearShapes();
        }

        private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                _autoDismissTimer.Stop();
                Close();
                e.Handled = true;
            }
        }
    }
}
