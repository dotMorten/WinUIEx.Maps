using Microsoft.UI.Xaml;

namespace WindowsMapsSample;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public App()
    {
        // AppContext.SetSwitch("WinUIEx.Maps.DisableMultisampleAntialiasing", true);
        InitializeComponent();
        UnhandledException += (_, args) => Services.SampleEventSource.Log.UnhandledFailure(args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        Window = window;
        window.InitializeContent();
        window.Activate();
    }
}
