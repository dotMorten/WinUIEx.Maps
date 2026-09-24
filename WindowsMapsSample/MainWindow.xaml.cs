using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace WindowsMapsSample;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppTitleBar.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        UpdateTitleBarTheme();

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.IconShowOptions = Microsoft.UI.Windowing.IconShowOptions.HideIconAndSystemMenu;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(840 * scale)));
    }

    internal void InitializeContent() => ContentRoot.Content = new MainPage();

    private void UpdateTitleBarTheme() => AppWindow.TitleBar.PreferredTheme =
        AppTitleBar.ActualTheme == ElementTheme.Dark
            ? Microsoft.UI.Windowing.TitleBarTheme.Dark
            : Microsoft.UI.Windowing.TitleBarTheme.Light;

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
