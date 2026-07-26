using System.Net.Http;

namespace Guance.Rum.Windows;

public static class RumSdk
{
    private static readonly object Gate = new();
    private static RumClient? current;

    public static RumClient Init(RumConfig config)
    {
        lock (Gate)
        {
            var previous = current;
            current = new RumClient(config);
            if (previous is not null)
            {
                _ = previous.DisposeAsync();
            }

            return current;
        }
    }

    public static void EnableAutomaticInstrumentation(AutomaticInstrumentationOptions? options = null) => Client.EnableAutomaticInstrumentation(options);
    public static void AttachWinUIWindow(object window, string? viewName = null) => Client.AttachWinUIWindow(window, viewName);
    public static void AttachWebView(object webView) => Client.AttachWebView(webView);
    public static void DetachWebView(object webView) => Client.DetachWebView(webView);
    public static void StartSessionReplayRecording() => Client.StartSessionReplayRecording();
    public static void StopSessionReplayRecording() => Client.StopSessionReplayRecording();
    public static void SetSessionReplayTextAndInputPrivacy(object element, SessionReplayTextAndInputPrivacy? privacy) => Client.SetSessionReplayTextAndInputPrivacy(element, privacy);
    public static void SetSessionReplayTouchPrivacy(object element, SessionReplayTouchPrivacy? privacy) => Client.SetSessionReplayTouchPrivacy(element, privacy);
    public static void SetSessionReplayImagePrivacy(object element, SessionReplayImagePrivacy? privacy) => Client.SetSessionReplayImagePrivacy(element, privacy);
    public static void SetSessionReplayHidden(object element, bool hidden = true) => Client.SetSessionReplayHidden(element, hidden);
    public static void SetUser(string id, string? name = null, string? email = null, IReadOnlyDictionary<string, object?>? extra = null) => Client.SetUser(id, name, email, extra);
    public static void ClearUser() => Client.ClearUser();
    public static void AddGlobalContext(string key, object? value) => Client.AddGlobalContext(key, value);
    public static void AddRumGlobalContext(string key, object? value) => Client.AddRumGlobalContext(key, value);
    public static void StartView(string name, IReadOnlyDictionary<string, object?>? properties = null) => Client.StartView(name, properties);
    public static void StopView(IReadOnlyDictionary<string, object?>? properties = null) => Client.StopView(properties);
    public static RumActionScope StartAction(string name, string type, IReadOnlyDictionary<string, object?>? properties = null) => Client.StartAction(name, type, properties);
    public static void AddAction(string name, string type, TimeSpan duration, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddAction(name, type, duration, properties);
    public static string StartResource(string url, string method, IReadOnlyDictionary<string, object?>? properties = null) => Client.StartResource(url, method, properties);
    public static void StopResource(string resourceId, int statusCode, RumResourceTiming timing, long responseSize = -1, long requestSize = -1, string? resourceType = null, string? requestHeader = null, string? responseHeader = null, string? errorStack = null, string? errorMessage = null, IReadOnlyDictionary<string, object?>? properties = null)
        => Client.StopResource(resourceId, statusCode, timing, responseSize, requestSize, resourceType, requestHeader, responseHeader, errorStack, errorMessage, properties);
    public static void StopResource(string resourceId, int statusCode, long responseSize = -1, long requestSize = -1, string? resourceType = null, string? requestHeader = null, string? responseHeader = null, string? errorStack = null, string? errorMessage = null, IReadOnlyDictionary<string, object?>? properties = null)
        => Client.StopResource(resourceId, statusCode, responseSize, requestSize, resourceType, requestHeader, responseHeader, errorStack, errorMessage, properties);
    public static void AddError(Exception exception, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddError(exception, properties);
    public static void AddError(string stack, string message, string errorType, string source = "logger", IReadOnlyDictionary<string, object?>? properties = null) => Client.AddError(stack, message, errorType, source, properties);
    public static void AddLongTask(TimeSpan duration, string? stack = null, IReadOnlyDictionary<string, object?>? properties = null) => Client.AddLongTask(duration, stack, properties);
    public static RumDiagnosticsSnapshot GetDiagnosticsSnapshot() => Client.GetDiagnosticsSnapshot();
    public static void AddDiagnosticListener(EventHandler<RumDiagnosticEvent> listener) => Client.DiagnosticEvent += listener;
    public static void RemoveDiagnosticListener(EventHandler<RumDiagnosticEvent> listener) => Client.DiagnosticEvent -= listener;
    public static Task FlushAsync(CancellationToken cancellationToken = default) => Client.FlushAsync(cancellationToken);
    public static Task ShutdownAsync(CancellationToken cancellationToken = default) => Client.ShutdownAsync(cancellationToken);

    public static HttpMessageHandler CreateHttpMessageHandler(HttpMessageHandler? innerHandler = null)
    {
        return new RumHttpMessageHandler(Client, innerHandler ?? new HttpClientHandler());
    }

    public static TWindow UseWinUIWindow<TWindow>(TWindow window, string? viewName = null) where TWindow : class
    {
        Client.AttachWinUIWindow(window, viewName);
        return window;
    }

    internal static RumClient Client
    {
        get
        {
            lock (Gate)
            {
                return current ?? throw new InvalidOperationException("RumSdk.Init must be called before using the SDK.");
            }
        }
    }
}
