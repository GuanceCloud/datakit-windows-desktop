using System.Windows;
using Guance.Windows;
using Guance.Windows.Samples;

namespace WpfSample;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        GuanceSdk.Init(SampleGuanceConfig.Load("rum-wpf-demo", "wpf-sample", e.Args));
        GuanceSdk.EnableAutomaticInstrumentation();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GuanceSdk.ShutdownAsync().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
