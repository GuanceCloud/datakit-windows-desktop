using System.Reflection;
using Guance.Rum.Windows.SessionReplay;

namespace Guance.Rum.Windows;

internal sealed class WebViewInstrumentationManager : IDisposable
{
    private readonly RumClient client;
    private readonly object gate = new();
    private readonly Dictionary<object, WebViewAttachment> attachments = new(ReferenceEqualityComparer.Instance);
    private bool disposed;

    public WebViewInstrumentationManager(RumClient client)
    {
        this.client = client;
    }

    public void Attach(object webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        WebViewAttachment attachment;
        lock (gate)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(WebViewInstrumentationManager));
            }
            if (attachments.ContainsKey(webView))
            {
                return;
            }

            attachment = new WebViewAttachment(client, webView, Detach);
            attachments.Add(webView, attachment);
        }

        try
        {
            attachment.Start();
        }
        catch
        {
            lock (gate)
            {
                attachments.Remove(webView);
            }
            attachment.Dispose();
            throw;
        }
    }

    public void Detach(object webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        WebViewAttachment? attachment;
        lock (gate)
        {
            if (!attachments.Remove(webView, out attachment))
            {
                return;
            }
        }

        attachment.Dispose();
    }

    public void Dispose()
    {
        WebViewAttachment[] snapshot;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            snapshot = attachments.Values.ToArray();
            attachments.Clear();
        }

        foreach (var attachment in snapshot)
        {
            attachment.Dispose();
        }
    }

    private sealed class WebViewAttachment : IDisposable
    {
        private readonly RumClient client;
        private readonly object webView;
        private readonly Action<object> detach;
        private readonly string token = Guid.NewGuid().ToString("N");
        private readonly string slotId;
        private readonly List<ReflectionEventSubscription> subscriptions = new();
        private object? coreWebView;
        private string? scriptId;
        private string? pendingNavigationUrl;
        private string? hostViewId;
        private string? hostViewName;
        private string? browserViewId;
        private bool hostViewCaptured;
        private int initialized;
        private int disposed;

        public WebViewAttachment(RumClient client, object webView, Action<object> detach)
        {
            this.client = client;
            this.webView = webView;
            this.detach = detach;
            slotId = WebViewSlotRegistry.GetOrCreate(webView);
        }

        public void Start()
        {
            var type = webView.GetType();
            if (type.GetRuntimeProperty("CoreWebView2") is null)
            {
                throw new ArgumentException("The object does not expose a CoreWebView2 property.", nameof(webView));
            }

            Subscribe(webView, "CoreWebView2InitializationCompleted", OnInitializationCompleted);
            Subscribe(webView, "CoreWebView2Initialized", OnInitializationCompleted);
            Subscribe(webView, "Disposed", OnControlDetached);
            Subscribe(webView, "Unloaded", OnControlDetached);

            var existingCore = ReadProperty(webView, "CoreWebView2");
            if (existingCore is not null)
            {
                InitializeCore(existingCore);
                return;
            }

            BeginEnsureCore();
        }

        private void OnControlDetached(object? sender, object? args)
        {
            detach(webView);
        }

        private void BeginEnsureCore()
        {
            try
            {
                var method = webView.GetType().GetRuntimeMethods()
                    .Where(candidate => candidate.Name == "EnsureCoreWebView2Async")
                    .OrderBy(candidate => candidate.GetParameters().Length)
                    .FirstOrDefault();
                if (method is null)
                {
                    client.ReportWebViewDiagnostic(
                        RumDiagnosticLevel.Warning,
                        "WebView2 is not initialized and does not expose EnsureCoreWebView2Async.");
                    return;
                }

                var arguments = method.GetParameters()
                    .Select(parameter => parameter.HasDefaultValue ? Type.Missing : null)
                    .ToArray();
                var operation = method.Invoke(webView, arguments);
                if (operation is Task initialization)
                {
                    _ = ObserveInitializationAsync(initialization);
                }
            }
            catch (TargetInvocationException ex)
            {
                client.ReportWebViewDiagnostic(
                    RumDiagnosticLevel.Warning,
                    "WebView2 initialization failed.",
                    ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                client.ReportWebViewDiagnostic(RumDiagnosticLevel.Warning, "WebView2 initialization failed.", ex);
            }
        }

        private async Task ObserveInitializationAsync(Task initialization)
        {
            try
            {
                await initialization;
                if (Volatile.Read(ref disposed) == 0)
                {
                    var core = ReadProperty(webView, "CoreWebView2");
                    if (core is not null)
                    {
                        InitializeCore(core);
                    }
                }
            }
            catch (Exception ex)
            {
                client.ReportWebViewDiagnostic(RumDiagnosticLevel.Warning, "WebView2 initialization failed.", ex);
            }
        }

        private void OnInitializationCompleted(object? sender, object? args)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            if (ReadProperty(args, "IsSuccess") is bool isSuccess && !isSuccess)
            {
                client.ReportWebViewDiagnostic(
                    RumDiagnosticLevel.Warning,
                    "WebView2 initialization did not succeed.",
                    ReadProperty(args, "InitializationException") as Exception);
                return;
            }

            var core = ReadProperty(webView, "CoreWebView2");
            if (core is not null)
            {
                InitializeCore(core);
            }
        }

        private void InitializeCore(object core)
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0 || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            coreWebView = core;
            Subscribe(core, "WebMessageReceived", OnWebMessageReceived);
            Subscribe(core, "ProcessFailed", OnProcessFailed);
            Subscribe(core, "NavigationStarting", OnNavigationStarting);
            Subscribe(core, "NavigationCompleted", OnNavigationCompleted);
            _ = InstallBridgeAsync(core);
        }

        private async Task InstallBridgeAsync(object core)
        {
            try
            {
                var script = WebViewBridgeScript.Create(
                    token,
                    client.Config.SessionReplay.Enabled,
                    GetSessionReplayPrivacyLevel(client.Config.SessionReplay));
                var addScript = core.GetType().GetRuntimeMethod(
                    "AddScriptToExecuteOnDocumentCreatedAsync",
                    new[] { typeof(string) });
                if (addScript is null)
                {
                    client.ReportWebViewDiagnostic(
                        RumDiagnosticLevel.Warning,
                        "CoreWebView2 does not expose AddScriptToExecuteOnDocumentCreatedAsync.");
                    return;
                }

                var registration = addScript.Invoke(core, new object[] { script });
                scriptId = await ReadTaskResultAsync(registration);
                if (Volatile.Read(ref disposed) != 0)
                {
                    RemoveInjectedScript(core);
                    return;
                }

                var executeScript = core.GetType().GetRuntimeMethod("ExecuteScriptAsync", new[] { typeof(string) });
                if (executeScript?.Invoke(core, new object[] { script }) is Task execution)
                {
                    await execution;
                }
            }
            catch (TargetInvocationException ex)
            {
                client.ReportWebViewDiagnostic(
                    RumDiagnosticLevel.Warning,
                    "WebView2 bridge injection failed.",
                    ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                client.ReportWebViewDiagnostic(RumDiagnosticLevel.Warning, "WebView2 bridge injection failed.", ex);
            }
        }

        private static async Task<string?> ReadTaskResultAsync(object? operation)
        {
            if (operation is not Task task)
            {
                return null;
            }

            await task;
            return task.GetType().GetRuntimeProperty("Result")?.GetValue(task) as string;
        }

        private void OnWebMessageReceived(object? sender, object? args)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            try
            {
                var json = ReadProperty(args, "WebMessageAsJson") as string;
                var source = ReadProperty(args, "Source") as string;
                if (!WebViewBridgeMessage.TryParse(json ?? string.Empty, token, source, out var message) ||
                    message is null)
                {
                    client.ReportWebViewDiagnostic(RumDiagnosticLevel.Debug, "Rejected an invalid WebView2 bridge message.");
                    return;
                }

                Track(message);
            }
            catch (Exception ex)
            {
                client.ReportWebViewDiagnostic(RumDiagnosticLevel.Warning, "WebView2 bridge message handling failed.", ex);
            }
        }

        private void Track(WebViewBridgeMessage message)
        {
            var context = CreateContext(message.Source);

            switch (message.Type)
            {
                case "view":
                    context["view_type"] = "webview";
                    var fallbackName = HttpHeaderRedactor.RedactUrl(message.Url!, client.Config.Privacy);
                    client.StartView(
                        string.IsNullOrWhiteSpace(message.Title) ? fallbackName : message.Title!,
                        context);
                    break;
                case "action":
                    context["action_source"] = "webview";
                    client.AddAction(
                        message.Name!,
                        message.ActionType!,
                        TimeSpan.FromMilliseconds(message.DurationMilliseconds),
                        context);
                    break;
                case "error":
                    client.AddError(
                        message.Stack ?? string.Empty,
                        message.Message!,
                        message.ErrorType!,
                        "webview",
                        context);
                    break;
                case "resource":
                    context["resource_provider"] = "webview";
                    var resourceId = client.StartResource(message.Url!, message.Method!, context);
                    client.StopResource(
                        resourceId,
                        message.Status,
                        RumResourceTiming.FromTotalElapsed(
                            TimeSpan.FromMilliseconds(message.DurationMilliseconds),
                            "webview_performance"),
                        message.ResponseSize,
                        message.RequestSize,
                        message.ResourceType);
                    break;
                case "session_replay":
                    client.CaptureSessionReplayWebViewRecord(
                        slotId,
                        message.Record!.Value,
                        string.IsNullOrWhiteSpace(browserViewId)
                            ? message.BrowserViewId
                            : browserViewId);
                    break;
                case "rum":
                    CaptureHostView();
                    if (!string.IsNullOrWhiteSpace(message.BrowserViewId))
                    {
                        browserViewId = message.BrowserViewId;
                    }
                    client.TrackWebViewRumRecord(
                        message.Record!.Value,
                        hostViewId,
                        hostViewName,
                        message.Source);
                    break;
            }
        }

        private static string GetSessionReplayPrivacyLevel(RumSessionReplayConfig replay)
        {
            if (replay.TouchPrivacy == SessionReplayTouchPrivacy.Show)
            {
                if (replay.TextAndInputPrivacy is SessionReplayTextAndInputPrivacy.Allow or
                    SessionReplayTextAndInputPrivacy.MaskSensitiveInputs)
                {
                    return "allow";
                }

                if (replay.TextAndInputPrivacy == SessionReplayTextAndInputPrivacy.MaskAllInputs)
                {
                    return "mask-user-input";
                }
            }

            return "mask";
        }

        private Dictionary<string, object?> CreateContext(string? url)
        {
            CaptureHostView();
            var context = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(url))
            {
                context["webview_url"] = HttpHeaderRedactor.RedactUrl(url, client.Config.Privacy);
            }
            if (!string.IsNullOrWhiteSpace(hostViewId))
            {
                context["webview_host_view_id"] = hostViewId;
            }
            if (!string.IsNullOrWhiteSpace(hostViewName))
            {
                context["webview_host_view_name"] = hostViewName;
            }
            return context;
        }

        private void CaptureHostView()
        {
            if (hostViewCaptured)
            {
                return;
            }

            (hostViewId, hostViewName) = client.GetActiveViewCorrelation();
            hostViewCaptured = true;
        }

        private void OnNavigationStarting(object? sender, object? args)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            pendingNavigationUrl = ReadProperty(args, "Uri") as string;
            browserViewId = null;
        }

        private void OnNavigationCompleted(object? sender, object? args)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                ReadProperty(args, "IsSuccess") is not bool isSuccess ||
                isSuccess)
            {
                return;
            }

            var status = ReadProperty(args, "WebErrorStatus")?.ToString() ?? "Unknown";
            var context = CreateContext(pendingNavigationUrl);
            context["webview_navigation_status"] = status;
            client.AddError(
                string.Empty,
                $"WebView2 navigation failed: {status}",
                "WebView2NavigationError",
                "webview",
                context);
        }

        private void OnProcessFailed(object? sender, object? args)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var kind = ReadProperty(args, "ProcessFailedKind")?.ToString() ?? "Unknown";
            var reason = ReadProperty(args, "Reason")?.ToString();
            var context = CreateContext(null);
            context["webview_process_failed_kind"] = kind;
            client.AddError(
                string.Empty,
                string.IsNullOrWhiteSpace(reason) ? $"WebView2 process failed: {kind}" : reason,
                "WebView2ProcessFailed",
                "webview",
                context);
        }

        private void Subscribe(object target, string eventName, Action<object?, object?> callback)
        {
            var subscription = ReflectionEventSubscription.TryCreate(target, eventName, callback);
            if (subscription is not null)
            {
                subscriptions.Add(subscription);
            }
        }

        private void RemoveInjectedScript(object? registeredCore = null)
        {
            var core = registeredCore ?? coreWebView;
            var id = scriptId;
            if (core is null || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            try
            {
                core.GetType()
                    .GetRuntimeMethod("RemoveScriptToExecuteOnDocumentCreated", new[] { typeof(string) })
                    ?.Invoke(core, new object[] { id });
            }
            catch
            {
                // CoreWebView2 may already be disposed.
            }
            finally
            {
                scriptId = null;
            }
        }

        private void CleanupCurrentDocument()
        {
            var core = coreWebView;
            if (core is null)
            {
                return;
            }

            try
            {
                const string cleanupScript =
                    "window.__guanceRumWebViewBridge && window.__guanceRumWebViewBridge.cleanup();";
                var operation = core.GetType()
                    .GetRuntimeMethod("ExecuteScriptAsync", new[] { typeof(string) })
                    ?.Invoke(core, new object[] { cleanupScript });
                if (operation is Task cleanup)
                {
                    _ = ObserveCleanupAsync(cleanup);
                }
            }
            catch
            {
                // CoreWebView2 may already be disposed.
            }
        }

        private static async Task ObserveCleanupAsync(Task cleanup)
        {
            try
            {
                await cleanup;
            }
            catch
            {
                // The control may finish disposing before the cleanup script runs.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            CleanupCurrentDocument();
            RemoveInjectedScript();
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
            subscriptions.Clear();
            coreWebView = null;
        }

        private static object? ReadProperty(object? target, string propertyName)
        {
            return target?.GetType().GetRuntimeProperty(propertyName)?.GetValue(target);
        }
    }
}
