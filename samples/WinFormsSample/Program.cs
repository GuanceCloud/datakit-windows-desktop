using Guance.Windows;
using Guance.Windows.Samples;
using System.Windows.Forms;

namespace WinFormsSample;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        GuanceSdk.Init(SampleGuanceConfig.Load("rum-winforms-demo", "winforms-sample", args));
        GuanceSdk.EnableAutomaticInstrumentation();
        GuanceSdk.AddLog("WinForms sample started.", LogStatus.Info);
        GuanceSdk.AddLogs(new[]
        {
            new LogEntry("WinForms sample batch log.", LogStatus.Info)
        });
        Application.Run(new MainForm());
        GuanceSdk.ShutdownAsync().GetAwaiter().GetResult();
    }
}
