using System.Windows;
using Guance.Rum.Windows;

namespace WpfSample;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RumSdk.Init(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "rum-wpf-demo",
            ServiceName = "wpf-sample",
            SessionReplay = new RumSessionReplayConfig { Enabled = true },
            Debug = true
        });
        RumSdk.EnableAutomaticInstrumentation();
        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await RumSdk.ShutdownAsync();
        base.OnExit(e);
    }
}
