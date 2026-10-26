using System;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Flinch_App;

public sealed partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }
    public IntPtr Hwnd => WindowNative.GetWindowHandle(this);

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }
}
