using Guance.Rum.Windows;
using System.Windows.Forms;

namespace WinFormsSample;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        RumSdk.Init(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "rum-winforms-demo",
            ServiceName = "winforms-sample",
            SessionReplay = new RumSessionReplayConfig { Enabled = true },
            Debug = true
        });
        RumSdk.EnableAutomaticInstrumentation();
        Application.Run(new MainForm());
        RumSdk.ShutdownAsync().GetAwaiter().GetResult();
    }
}
