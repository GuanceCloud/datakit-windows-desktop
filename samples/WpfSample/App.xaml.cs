using System.Windows;
using Guance.Rum.Windows;
using Guance.Rum.Windows.Samples;

namespace WpfSample;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RumSdk.Init(SampleRumConfig.Load("rum-wpf-demo", "wpf-sample", e.Args));
        RumSdk.EnableAutomaticInstrumentation();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RumSdk.ShutdownAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
