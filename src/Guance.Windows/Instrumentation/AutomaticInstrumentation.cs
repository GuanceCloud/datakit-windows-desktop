using System.Diagnostics;

namespace Guance.Windows;

internal sealed class AutomaticInstrumentation : IDisposable
{
    private readonly GuanceClient client;
    private readonly AutomaticInstrumentationOptions options;
    private HttpDiagnosticObserver? httpObserver;
    private IDisposable? allListenersSubscription;
    private UiThreadBlockMonitor? blockMonitor;
    private bool started;

    public AutomaticInstrumentation(GuanceClient client, AutomaticInstrumentationOptions options)
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
            allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(httpObserver);
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

        allListenersSubscription?.Dispose();
        allListenersSubscription = null;
        httpObserver?.Dispose();
        httpObserver = null;
        blockMonitor?.Dispose();
        WindowsDesktopInstrumentation.Detach(client);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            var message = string.IsNullOrWhiteSpace(exception.Message)
                ? exceptionType
                : $"{exceptionType}: {exception.Message}";
            client.AddError(
                exception.ToString(),
                message,
                args.IsTerminating ? "windows_crash" : exception.GetType().Name,
                "logger",
                properties: null,
                waitForQueue: args.IsTerminating);
        }
        else
        {
            var exceptionText = args.ExceptionObject?.ToString() ?? string.Empty;
            client.AddError(
                exceptionText,
                string.IsNullOrWhiteSpace(exceptionText)
                    ? "Unhandled non-Exception object"
                    : $"Unhandled non-Exception object: {exceptionText}",
                args.IsTerminating ? "windows_crash" : "UnhandledException",
                "logger",
                properties: null,
                waitForQueue: args.IsTerminating);
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
            "logger");
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
            client.ReportDiagnostic(RumDiagnosticLevel.Warning, "crash_flush", "Crash flush failed.", ex);
        }
    }
}
