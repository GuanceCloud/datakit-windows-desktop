using System.Net.Http;

namespace Guance.Windows;

/// <summary>Provides process-wide access to the current Guance Windows client.</summary>
public static class GuanceSdk
{
    private static readonly object Gate = new();
    private static GuanceClient? current;

    /// <summary>Initializes a new current client and asynchronously disposes the previous client.</summary>
    public static GuanceClient Init(GuanceConfig config)
    {
        lock (Gate)
        {
            var previous = current;
            current = new GuanceClient(config);
            if (previous is not null)
            {
                _ = previous.DisposeAsync();
            }

            return current;
        }
    }

    /// <summary>Enables the selected automatic UI, HTTP, exception, and launch instrumentation modules.</summary>
    public static void EnableAutomaticInstrumentation(AutomaticInstrumentationOptions? options = null) => Client.EnableAutomaticInstrumentation(options);
    /// <summary>Starts forwarding <see cref="System.Diagnostics.Trace" /> output to Guance Logging.</summary>
    public static void EnableAutomaticLogCapture() => Client.EnableAutomaticLogCapture();
    /// <summary>Stops forwarding <see cref="System.Diagnostics.Trace" /> output.</summary>
    public static void DisableAutomaticLogCapture() => Client.DisableAutomaticLogCapture();
    /// <summary>Attaches a WinUI 3 window for View and Action instrumentation.</summary>
    public static void AttachWinUIWindow(object window, string? viewName = null) => Client.AttachWinUIWindow(window, viewName);
    /// <summary>Attaches a supported WebView2 control to the native RUM bridge.</summary>
    public static void AttachWebView(object webView) => Client.AttachWebView(webView);
    /// <summary>Detaches a previously attached WebView2 control.</summary>
    public static void DetachWebView(object webView) => Client.DetachWebView(webView);
    /// <summary>Starts Session Replay recording when Replay is enabled and the session is sampled.</summary>
    public static void StartSessionReplayRecording() => Client.StartSessionReplayRecording();
    /// <summary>Stops Session Replay recording and flushes the active segment.</summary>
    public static void StopSessionReplayRecording() => Client.StopSessionReplayRecording();
    /// <summary>Overrides text and input privacy for a UI element.</summary>
    public static void SetSessionReplayTextAndInputPrivacy(object element, SessionReplayTextAndInputPrivacy? privacy) => Client.SetSessionReplayTextAndInputPrivacy(element, privacy);
    /// <summary>Overrides pointer and touch privacy for a UI element.</summary>
    public static void SetSessionReplayTouchPrivacy(object element, SessionReplayTouchPrivacy? privacy) => Client.SetSessionReplayTouchPrivacy(element, privacy);
    /// <summary>Overrides image privacy for a UI element.</summary>
    public static void SetSessionReplayImagePrivacy(object element, SessionReplayImagePrivacy? privacy) => Client.SetSessionReplayImagePrivacy(element, privacy);
    /// <summary>Includes or excludes an element subtree from Session Replay.</summary>
    public static void SetSessionReplayHidden(object element, bool hidden = true) => Client.SetSessionReplayHidden(element, hidden);
    /// <summary>Sets user identity and attributes for subsequently created telemetry.</summary>
    public static void SetUser(string id, string? name = null, string? email = null, IReadOnlyDictionary<string, object?>? extra = null) => Client.SetUser(id, name, email, extra);
    /// <summary>Clears the current user identity for subsequent telemetry.</summary>
    public static void ClearUser() => Client.ClearUser();
    /// <summary>Adds or replaces a context value shared by RUM and logs.</summary>
    public static void AddGlobalContext(string key, object? value) => Client.AddGlobalContext(key, value);
    /// <summary>Adds or replaces a context value attached only to RUM events.</summary>
    public static void AddRumGlobalContext(string key, object? value) => Client.AddRumGlobalContext(key, value);
    /// <summary>Starts a RUM View and closes the previous active View.</summary>
    public static void StartView(string name, IReadOnlyDictionary<string, object?>? properties = null) => Client.StartView(name, properties);
    /// <summary>Stops the active RUM View.</summary>
    public static void StopView(IReadOnlyDictionary<string, object?>? properties = null) => Client.StopView(properties);
    /// <summary>Starts a scoped RUM Action that stops when the returned scope is disposed.</summary>
    public static RumActionScope StartAction(string name, string type, IReadOnlyDictionary<string, object?>? properties = null) => Client.StartAction(name, type, properties);
    /// <summary>Adds a completed RUM Action with a known duration.</summary>
    public static void AddAction(string name, string type, TimeSpan duration, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddAction(name, type, duration, properties);
    /// <summary>Starts a manually tracked RUM Resource and returns its identifier.</summary>
    public static string StartResource(string url, string method, IReadOnlyDictionary<string, object?>? properties = null) => Client.StartResource(url, method, properties);
    /// <summary>Stops a RUM Resource using explicit phase timing information.</summary>
    public static void StopResource(string resourceId, int statusCode, RumResourceTiming timing, long responseSize = -1, long requestSize = -1, string? resourceType = null, string? requestHeader = null, string? responseHeader = null, string? errorStack = null, string? errorMessage = null, IReadOnlyDictionary<string, object?>? properties = null)
        => Client.StopResource(resourceId, statusCode, timing, responseSize, requestSize, resourceType, requestHeader, responseHeader, errorStack, errorMessage, properties);
    /// <summary>Stops a RUM Resource using elapsed time measured since it was started.</summary>
    public static void StopResource(string resourceId, int statusCode, long responseSize = -1, long requestSize = -1, string? resourceType = null, string? requestHeader = null, string? responseHeader = null, string? errorStack = null, string? errorMessage = null, IReadOnlyDictionary<string, object?>? properties = null)
        => Client.StopResource(resourceId, statusCode, responseSize, requestSize, resourceType, requestHeader, responseHeader, errorStack, errorMessage, properties);
    /// <summary>Adds a RUM Error from an exception.</summary>
    public static void AddError(Exception exception, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddError(exception, properties);
    /// <summary>Adds a RUM Error from explicit stack, message, type, and source values.</summary>
    public static void AddError(string stack, string message, string errorType, string source = "logger", IReadOnlyDictionary<string, object?>? properties = null) => Client.AddError(stack, message, errorType, source, properties);
    /// <summary>Adds a RUM Long Task.</summary>
    public static void AddLongTask(TimeSpan duration, string? stack = null, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddLongTask(duration, stack, properties);
    /// <summary>Adds a custom log with a predefined status.</summary>
    public static void AddLog(string content, LogStatus status, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddLog(content, status, properties);
    /// <summary>Adds a custom log with an application-defined status.</summary>
    public static void AddLog(string content, string status, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddLog(content, status, properties);
    /// <summary>Adds a batch of custom logs.</summary>
    public static void AddLogs(IEnumerable<LogEntry> logs) => Client.AddLogs(logs);
    /// <summary>Returns current RUM, Replay, queue, and upload diagnostics.</summary>
    public static RumDiagnosticsSnapshot GetDiagnosticsSnapshot() => Client.GetDiagnosticsSnapshot();
    /// <summary>Returns current shared disk-cache usage.</summary>
    public static CacheDiagnosticsSnapshot GetCacheDiagnosticsSnapshot() => Client.GetCacheDiagnosticsSnapshot();
    /// <summary>Returns current log queue and upload diagnostics.</summary>
    public static LogDiagnosticsSnapshot GetLogDiagnosticsSnapshot() => Client.GetLogDiagnosticsSnapshot();
    /// <summary>Subscribes an SDK diagnostic event handler.</summary>
    public static void AddDiagnosticListener(EventHandler<RumDiagnosticEvent> listener) => Client.DiagnosticEvent += listener;
    /// <summary>Unsubscribes an SDK diagnostic event handler.</summary>
    public static void RemoveDiagnosticListener(EventHandler<RumDiagnosticEvent> listener) => Client.DiagnosticEvent -= listener;
    /// <summary>Attempts to upload all currently queued telemetry.</summary>
    public static Task FlushAsync(CancellationToken cancellationToken = default) => Client.FlushAsync(cancellationToken);
    /// <summary>Stops instrumentation, flushes queues, and disposes the current client.</summary>
    public static Task ShutdownAsync(CancellationToken cancellationToken = default) => Client.ShutdownAsync(cancellationToken);

    /// <summary>Creates an instrumented HTTP handler for explicit <see cref="HttpClient" /> pipelines.</summary>
    public static HttpMessageHandler CreateHttpMessageHandler(HttpMessageHandler? innerHandler = null)
    {
        return new RumHttpMessageHandler(Client, innerHandler ?? new HttpClientHandler());
    }

    /// <summary>Attaches and returns a WinUI 3 window for fluent initialization.</summary>
    public static TWindow UseWinUIWindow<TWindow>(TWindow window, string? viewName = null) where TWindow : class
    {
        Client.AttachWinUIWindow(window, viewName);
        return window;
    }

    internal static GuanceClient Client
    {
        get
        {
            lock (Gate)
            {
                return current ?? throw new InvalidOperationException("GuanceSdk.Init must be called before using the SDK.");
            }
        }
    }
}
