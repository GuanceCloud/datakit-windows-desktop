using Guance.Windows;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Guance.Windows.CrashSmoke <datakit-url> <cache-directory>");
    return 2;
}

GuanceSdk.Init(new GuanceConfig
{
    DatakitUrl = args[0],
    RumAppId = "crash-smoke-app",
    ServiceName = "crash-smoke",
    Env = "local",
    CacheDirectory = args[1],
    Cache = new CacheOptions { MaxBatchItems = 1 },
    FlushInterval = TimeSpan.FromMinutes(5),
    HttpTimeout = TimeSpan.FromSeconds(2),
    DiagnosticListener = item => Console.Error.WriteLine($"[crash-smoke] {item.Source} status={item.StatusCode}: {item.Message}"),
    SessionReplay = new RumSessionReplayConfig { Enabled = false }
});

GuanceSdk.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
{
    EnableWpf = false,
    EnableWinForms = false,
    EnableWinUI = false,
    EnableHttpClient = false,
    EnableUnhandledException = true,
    EnableUiThreadBlock = false
});
// Keep the smoke deterministic on hosts where Windows Error Reporting retains the faulting process.
// The SDK handler is registered first, so this runs only after its bounded crash upload returns.
AppDomain.CurrentDomain.UnhandledException += (_, _) => Environment.Exit(1);
GuanceSdk.StartView("CrashSmoke");

throw new InvalidOperationException("guance crash smoke");
