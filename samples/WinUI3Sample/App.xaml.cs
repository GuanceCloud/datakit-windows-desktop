using Guance.Windows;
using Guance.Windows.Samples;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace WinUI3Sample;

public partial class App : Application
{
    private Window? window;
    private bool rumShutdownInProgress;

    public App()
    {
        InitializeComponent();
        GuanceSdk.Init(SampleGuanceConfig.Load("rum-winui-demo", "winui-sample"));
        GuanceSdk.EnableAutomaticInstrumentation();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow().UseGuanceRum("MainWindow");
        window.AppWindow.Closing += OnWindowClosing;
        window.Activate();
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (rumShutdownInProgress)
        {
            return;
        }

        rumShutdownInProgress = true;
        try
        {
            await GuanceSdk.ShutdownAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Guance.RUM.Sample] shutdown failed: {ex}");
        }
        finally
        {
            sender.Closing -= OnWindowClosing;
            window?.Close();
        }
    }
}
