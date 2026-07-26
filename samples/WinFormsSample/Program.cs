using Guance.Rum.Windows;
using Guance.Rum.Windows.Samples;
using System.Windows.Forms;

namespace WinFormsSample;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        RumSdk.Init(SampleRumConfig.Load("rum-winforms-demo", "winforms-sample", args));
        RumSdk.EnableAutomaticInstrumentation();
        Application.Run(new MainForm());
        RumSdk.ShutdownAsync().GetAwaiter().GetResult();
    }
}
