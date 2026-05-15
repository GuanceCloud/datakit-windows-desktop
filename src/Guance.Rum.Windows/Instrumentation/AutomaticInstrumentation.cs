using System.Diagnostics;

namespace Guance.Rum.Windows;

internal sealed class AutomaticInstrumentation : IDisposable
{
    private readonly RumClient client;
    private readonly AutomaticInstrumentationOptions options;
    private HttpDiagnosticObserver? httpObserver;
    private UiThreadBlockMonitor? blockMonitor;
    private bool started;

    public AutomaticInstrumentation(RumClient client, AutomaticInstrumentationOptions options)
    {
        this.client = client;
        this.options = options;
    }

    public void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        if (options.EnableUnhandledException)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        if (options.EnableHttpClient)
        {
            httpObserver = new HttpDiagnosticObserver(client);
            DiagnosticListener.AllListeners.Subscribe(httpObserver);
        }

        if (options.EnableUiThreadBlock && SynchronizationContext.Current is not null)
        {
            blockMonitor = new UiThreadBlockMonitor(client, SynchronizationContext.Current, options.UiThreadProbeInterval, options.UiThreadBlockThreshold, options.UiThreadLongTaskCooldown);
            blockMonitor.Start();
        }

        WindowsDesktopInstrumentation.TryAttach(client, options);
    }

    public void Dispose()
    {
        if (options.EnableUnhandledException)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }

        httpObserver?.Dispose();
        blockMonitor?.Dispose();
        WindowsDesktopInstrumentation.Detach(client);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var properties = new Dictionary<string, object?>
        {
            [RumConstants.IsCrash] = args.IsTerminating,
            [RumConstants.CrashSource] = "AppDomain.UnhandledException"
        };

        if (args.ExceptionObject is Exception exception)
        {
            client.AddError(exception.ToString(), exception.Message, exception.GetType().Name, "crash", properties);
        }
        else
        {
            client.AddError(args.ExceptionObject?.ToString() ?? string.Empty, "Unhandled non-Exception object", "UnhandledException", "crash", properties);
        }

        if (args.IsTerminating)
        {
            FlushForCrash();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        client.AddError(
            args.Exception.ToString(),
            args.Exception.Message,
            args.Exception.GetType().Name,
            "logger",
            new Dictionary<string, object?>
            {
                [RumConstants.IsCrash] = false,
                [RumConstants.CrashSource] = "TaskScheduler.UnobservedTaskException"
            });
    }

    private void FlushForCrash()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            client.FlushAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Guance.RUM] crash flush failed: {ex}");
        }
    }
}
