using Guance.Rum.Windows;
using Microsoft.UI.Xaml;

namespace WinUI3Sample;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        InitializeComponent();
        RumSdk.Init(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "rum-winui-demo",
            ServiceName = "winui-sample",
            SessionReplay = new RumSessionReplayConfig { Enabled = true },
            Debug = true
        });
        RumSdk.EnableAutomaticInstrumentation();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow().UseGuanceRum("MainWindow");
        window.Closed += async (_, _) => await RumSdk.ShutdownAsync();
        window.Activate();
    }
}
