#if WINDOWS
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.SessionReplay;
using Guance.Rum.Windows.Transport;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace Guance.Rum.Windows.Tests;

[Collection(WpfUiTestCollection.Name)]
public sealed class WebView2RuntimeSmokeTests
{
    private const string TestUrlEnvironmentVariable = "GUANCE_RUM_WEBVIEW_TEST_URL";
    private const string DatawayUrlEnvironmentVariable = "GUANCE_RUM_DATAWAY_URL";
    private const string ClientTokenEnvironmentVariable = "GUANCE_RUM_CLIENT_TOKEN";
    private const string AppIdEnvironmentVariable = "GUANCE_RUM_APP_ID";

    [WebViewRuntimeFact]
    public async Task WebView2Runtime_CollectsConfiguredPageInteractionErrorAndRequest()
    {
        var configuredUrl = Environment.GetEnvironmentVariable(TestUrlEnvironmentVariable);
        Assert.True(
            Uri.TryCreate(configuredUrl, UriKind.Absolute, out var testUri) &&
            testUri.Scheme is "http" or "https",
            $"{TestUrlEnvironmentVariable} must be an absolute HTTP or HTTPS URL.");

        await RunStaAsync(() =>
        {
            _ = System.Windows.Application.Current ??
                new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var rumQueue = new MemoryRumQueue();
            var client = CreateClient(rumQueue);
            var webView = new WebView2();
            var window = new Window
            {
                Title = "WebView2 runtime smoke",
                Width = 900,
                Height = 700,
                Content = webView,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                var navigationCompleted = false;
                webView.NavigationCompleted += (_, _) => navigationCompleted = true;
                client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
                {
                    EnableWpf = true,
                    EnableWinForms = false,
                    EnableWinUI = false,
                    EnableWebView = true,
                    EnableHttpClient = false,
                    EnableUnhandledException = false,
                    EnableUiThreadBlock = false
                });
                window.Show();
                var initialization = webView.EnsureCoreWebView2Async();
                PumpUntil(
                    () => initialization.IsCompleted,
                    TimeSpan.FromSeconds(20),
                    "WebView2 runtime initialization did not complete.");
                initialization.GetAwaiter().GetResult();
                webView.Source = testUri;

                PumpUntil(
                    () => navigationCompleted && webView.CoreWebView2 is not null,
                    TimeSpan.FromSeconds(20),
                    "WebView2 did not complete navigation.");
                PumpUntil(
                    () => client.GetDiagnosticsSnapshot().HasActiveView,
                    TimeSpan.FromSeconds(10),
                    "The injected bridge did not report its page view.");

                var actionCountBeforeControlCheck = CountMeasurements(rumQueue, "action");
                var controlCheck = webView.ExecuteScriptAsync(
                    "const checkbox=document.createElement('input');" +
                    "checkbox.type='checkbox';" +
                    "checkbox.setAttribute('aria-label','Single action checkbox');" +
                    "document.body.appendChild(checkbox);" +
                    "checkbox.click();" +
                    "const form=document.createElement('form');" +
                    "form.addEventListener('submit',event=>event.preventDefault());" +
                    "const submit=document.createElement('button');" +
                    "submit.type='submit';" +
                    "submit.textContent='Single submit';" +
                    "form.appendChild(submit);" +
                    "document.body.appendChild(form);" +
                    "submit.click();");
                PumpUntil(
                    () => controlCheck.IsCompleted,
                    TimeSpan.FromSeconds(10),
                    "WebView2 did not execute the single-action control check.");
                controlCheck.GetAwaiter().GetResult();
                PumpUntil(
                    () => CountMeasurements(rumQueue, "action") >= actionCountBeforeControlCheck + 2,
                    TimeSpan.FromSeconds(10),
                    "WebView2 bridge did not collect the control actions.");
                PumpFor(TimeSpan.FromMilliseconds(250));
                Assert.Equal(
                    actionCountBeforeControlCheck + 2,
                    CountMeasurements(rumQueue, "action"));

                var interaction = webView.ExecuteScriptAsync(
                    "document.getElementById('http-request-btn').click();" +
                    "document.getElementById('error-btn').click();");
                PumpUntil(
                    () => interaction.IsCompleted,
                    TimeSpan.FromSeconds(10),
                    "WebView2 did not execute the smoke interactions.");
                interaction.GetAwaiter().GetResult();

                PumpUntil(
                    () => ContainsMeasurements(rumQueue, "action", "error", "resource"),
                    TimeSpan.FromSeconds(15),
                    "WebView2 bridge did not collect action, error, and resource events.");
                client.StopView();

                var lines = rumQueue.PeekAsync(100, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Contains(lines, item =>
                    item.Line.StartsWith("view,", StringComparison.Ordinal) &&
                    item.Line.Contains("view_name=SDK\\ Webview\\ Demo", StringComparison.Ordinal));
                Assert.Contains(lines, item =>
                    item.Line.StartsWith("action,", StringComparison.Ordinal) &&
                    item.Line.Contains("action_name=Send\\ HTTP\\ Request", StringComparison.Ordinal));
                Assert.Contains(lines, item =>
                    item.Line.StartsWith("error,", StringComparison.Ordinal) &&
                    item.Line.Contains("error_source=webview", StringComparison.Ordinal));
                Assert.Contains(lines, item =>
                    item.Line.StartsWith("resource,", StringComparison.Ordinal) &&
                    item.Line.Contains("resource_type=xmlhttprequest", StringComparison.Ordinal));
                Assert.DoesNotContain(lines, item =>
                    item.Line.StartsWith("resource,", StringComparison.Ordinal) &&
                    item.Line.Contains("resource_provider=\"webview\"", StringComparison.Ordinal) &&
                    !item.Line.Contains("view_id=", StringComparison.Ordinal));
            }
            finally
            {
                client.DetachWebView(webView);
                window.Close();
                webView.Dispose();
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return Task.CompletedTask;
        });
    }

    [WebViewRuntimeFact]
    public async Task WebView2Runtime_MergesBrowserReplayIntoNativeWebViewSlot()
    {
        var configuredUrl = Environment.GetEnvironmentVariable(TestUrlEnvironmentVariable);
        Assert.True(
            Uri.TryCreate(configuredUrl, UriKind.Absolute, out var testUri) &&
            testUri.Scheme is "http" or "https",
            $"{TestUrlEnvironmentVariable} must be an absolute HTTP or HTTPS URL.");

        await RunStaAsync(() =>
        {
            _ = System.Windows.Application.Current ??
                new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var rumQueue = new MemoryRumQueue();
            var replayQueue = new MemoryReplayQueue();
            var client = CreateClient(rumQueue, replayQueue, sessionReplayEnabled: true);
            var webView = new WebView2();
            var window = new Window
            {
                Title = "WebView2 replay runtime smoke",
                Width = 900,
                Height = 700,
                Content = webView,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                var navigationCompleted = false;
                webView.NavigationCompleted += (_, _) => navigationCompleted = true;
                client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
                {
                    EnableWpf = true,
                    EnableWinForms = false,
                    EnableWinUI = false,
                    EnableWebView = true,
                    EnableHttpClient = false,
                    EnableUnhandledException = false,
                    EnableUiThreadBlock = false
                });
                client.AttachWebView(webView);
                window.Show();
                var initialization = webView.EnsureCoreWebView2Async();
                PumpUntil(
                    () => initialization.IsCompleted,
                    TimeSpan.FromSeconds(20),
                    "WebView2 runtime initialization did not complete.");
                initialization.GetAwaiter().GetResult();
                PumpFor(TimeSpan.FromMilliseconds(500));
                var bridgeReady = webView.ExecuteScriptAsync(
                    "Boolean(window.FTWebViewJavascriptBridge && " +
                    "window.FTWebViewJavascriptBridge.getCapabilities().includes('records'))");
                PumpUntil(
                    () => bridgeReady.IsCompleted,
                    TimeSpan.FromSeconds(10),
                    "The Session Replay bridge capability check did not complete.");
                Assert.Equal("true", bridgeReady.GetAwaiter().GetResult());
                webView.Source = testUri;
                PumpUntil(
                    () => navigationCompleted && webView.CoreWebView2 is not null,
                    TimeSpan.FromSeconds(20),
                    "WebView2 did not complete navigation.");

                var startReplay = webView.ExecuteScriptAsync(
                    "(() => {" +
                    "if (!window.DATAFLUX_RUM || typeof window.DATAFLUX_RUM.startSessionReplayRecording !== 'function') return false;" +
                    "window.DATAFLUX_RUM.startSessionReplayRecording();" +
                    "return true;" +
                    "})()");
                PumpUntil(
                    () => startReplay.IsCompleted,
                    TimeSpan.FromSeconds(10),
                    "The test page did not answer the Session Replay start request.");
                Assert.Equal("true", startReplay.GetAwaiter().GetResult());

                client.CaptureSessionReplayFullSnapshot(window);
                PumpFor(TimeSpan.FromSeconds(2));
                client.FlushAsync().GetAwaiter().GetResult();
                PumpFor(TimeSpan.FromMilliseconds(250));
                client.FlushAsync().GetAwaiter().GetResult();

                var payloads = replayQueue.Items
                    .Select(item => Encoding.UTF8.GetString(ExtractAndDecompressSegment(item.Body)))
                    .ToArray();
                var nativePayload = Assert.Single(
                    payloads
                        .Where(payload => payload.Contains("\"type\":\"webview\"", StringComparison.Ordinal))
                        .Select(payload =>
                        {
                            var marker = "\"slotId\":\"";
                            var start = payload.IndexOf(marker, StringComparison.Ordinal);
                            if (start < 0)
                            {
                                return string.Empty;
                            }
                            start += marker.Length;
                            var end = payload.IndexOf('"', start);
                            return end > start ? payload[start..end] : string.Empty;
                        })
                        .Where(slot => !string.IsNullOrWhiteSpace(slot))
                        .Distinct(StringComparer.Ordinal));
                var slotId = nativePayload;

                Assert.Contains(payloads, payload =>
                    payload.Contains("\"type\":2", StringComparison.Ordinal) &&
                    payload.Contains($"\"slotId\":\"{slotId}\"", StringComparison.Ordinal));
            }
            finally
            {
                client.DetachWebView(webView);
                window.Close();
                webView.Dispose();
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return Task.CompletedTask;
        });
    }

    [WebViewDatawayValidationFact]
    public async Task WebView2Runtime_UploadsMergedReplayToDataway()
    {
        var configuredUrl = Environment.GetEnvironmentVariable(TestUrlEnvironmentVariable);
        var datawayUrl = Environment.GetEnvironmentVariable(DatawayUrlEnvironmentVariable);
        var clientToken = Environment.GetEnvironmentVariable(ClientTokenEnvironmentVariable);
        var appId = Environment.GetEnvironmentVariable(AppIdEnvironmentVariable);
        Assert.True(
            Uri.TryCreate(configuredUrl, UriKind.Absolute, out var testUri) &&
            testUri.Scheme is "http" or "https",
            $"{TestUrlEnvironmentVariable} must be an absolute HTTP or HTTPS URL.");

        var validationRunId = "webview-slot-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        await RunStaAsync(() =>
        {
            _ = System.Windows.Application.Current ??
                new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var config = new RumConfig
            {
                DatawayUrl = datawayUrl,
                ClientToken = clientToken,
                RumAppId = appId!,
                ServiceName = "wpf-webview-replay-validation",
                Env = "local",
                Version = "0.1.0-webview-slot-validation",
                Debug = false,
                FlushInterval = TimeSpan.FromHours(1),
                HttpTimeout = TimeSpan.FromSeconds(20),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    AndroidCompatibilityMode = true,
                    SampleRate = 1,
                    FlushInterval = TimeSpan.FromHours(1),
                    TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs,
                    TouchPrivacy = SessionReplayTouchPrivacy.Show,
                    ImagePrivacy = SessionReplayImagePrivacy.MaskNone
                }
            };
            var rumQueue = new MemoryRumQueue();
            var replayQueue = new MemoryReplayQueue();
            var replayTransport = new CapturingReplayTransport(config);
            var client = new RumClient(
                config,
                rumQueue,
                new DatawayTransport(config),
                replayQueue,
                replayTransport);
            var webView = new WebView2();
            var window = new Window
            {
                Title = "WebView2 Dataway validation",
                Width = 900,
                Height = 700,
                Content = webView,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };

            try
            {
                client.AddRumGlobalContext("validation_run_id", validationRunId);
                var navigationCompleted = false;
                webView.NavigationCompleted += (_, _) => navigationCompleted = true;
                client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
                {
                    EnableWpf = true,
                    EnableWinForms = false,
                    EnableWinUI = false,
                    EnableWebView = true,
                    EnableHttpClient = false,
                    EnableUnhandledException = false,
                    EnableUiThreadBlock = false
                });
                client.AttachWebView(webView);
                window.Show();
                var initialization = webView.EnsureCoreWebView2Async();
                PumpUntil(
                    () => initialization.IsCompleted,
                    TimeSpan.FromSeconds(20),
                    "WebView2 runtime initialization did not complete.");
                initialization.GetAwaiter().GetResult();
                webView.Source = testUri;
                PumpUntil(
                    () => navigationCompleted && webView.CoreWebView2 is not null,
                    TimeSpan.FromSeconds(20),
                    "WebView2 did not complete navigation.");
                PumpUntil(
                    () => client.GetDiagnosticsSnapshot().RumEventsEnqueued >= 2,
                    TimeSpan.FromSeconds(15),
                    "Browser RUM did not establish the WebView view correlation.");

                var interaction = webView.ExecuteScriptAsync(
                    "(() => {" +
                    "if (!window.DATAFLUX_RUM || typeof window.DATAFLUX_RUM.startSessionReplayRecording !== 'function') return false;" +
                    "window.DATAFLUX_RUM.startSessionReplayRecording();" +
                    "const request=document.getElementById('http-request-btn');" +
                    "if (request) request.click();" +
                    "return true;" +
                    "})()");
                PumpUntil(
                    () => interaction.IsCompleted,
                    TimeSpan.FromSeconds(10),
                    "The test page did not complete the replay interaction.");
                Assert.Equal("true", interaction.GetAwaiter().GetResult());

                client.CaptureSessionReplayFullSnapshot(window);
                PumpFor(TimeSpan.FromSeconds(3));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                client.FlushAsync(timeout.Token).GetAwaiter().GetResult();
                var diagnostics = client.GetDiagnosticsSnapshot();

                var nativeSlotIds = replayTransport.Payloads
                    .Where(payload => payload.Contains("\"type\":\"webview\"", StringComparison.Ordinal))
                    .Select(payload => Regex.Match(
                        payload,
                        "\"type\":\"webview\",\"slotId\":\"(?<slot>[^\"]+)\""))
                    .Where(match => match.Success)
                    .Select(match => match.Groups["slot"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var slotId = Assert.Single(nativeSlotIds);
                var browserPayloads = replayTransport.Payloads
                    .Where(payload =>
                        payload.Contains("\"type\":2", StringComparison.Ordinal) &&
                        payload.Contains($"\"slotId\":\"{slotId}\"", StringComparison.Ordinal))
                    .ToArray();
                Assert.NotEmpty(browserPayloads);
                Assert.All(browserPayloads, browserPayload =>
                {
                    var browserViewMatch = Regex.Match(
                        browserPayload,
                        "\"view\":\\{\"id\":\"(?<view>[^\"]+)\"\\}");
                    Assert.True(
                        browserViewMatch.Success &&
                        !string.IsNullOrWhiteSpace(browserViewMatch.Groups["view"].Value),
                        "Uploaded Browser replay did not contain its Browser RUM view_id.");
                });

                Assert.True(diagnostics.RumUploadSuccessCount > 0, diagnostics.LastRumUploadError);
                Assert.True(diagnostics.ReplayUploadSuccessCount > 0, diagnostics.LastReplayUploadError);
                Assert.InRange(diagnostics.LastRumUploadStatusCode, 200, 299);
                Assert.InRange(diagnostics.LastReplayUploadStatusCode, 200, 299);
                Console.WriteLine(
                    $"WEBVIEW_REPLAY_VALIDATION_RESULT run={validationRunId} " +
                    $"rumStatus={diagnostics.LastRumUploadStatusCode} " +
                    $"replayStatus={diagnostics.LastReplayUploadStatusCode}");
            }
            finally
            {
                client.DetachWebView(webView);
                window.Close();
                webView.Dispose();
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return Task.CompletedTask;
        });
    }

    private sealed class WebViewRuntimeFactAttribute : FactAttribute
    {
        public WebViewRuntimeFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestUrlEnvironmentVariable)))
            {
                Skip = $"{TestUrlEnvironmentVariable} is not set; WebView2 runtime smoke test skipped.";
            }
        }
    }

    private sealed class WebViewDatawayValidationFactAttribute : FactAttribute
    {
        public WebViewDatawayValidationFactAttribute()
        {
            var required = new[]
            {
                TestUrlEnvironmentVariable,
                DatawayUrlEnvironmentVariable,
                ClientTokenEnvironmentVariable,
                AppIdEnvironmentVariable
            };
            var missing = required
                .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                .ToArray();
            if (missing.Length > 0)
            {
                Skip = "Missing environment variables: " + string.Join(", ", missing);
            }
        }
    }

    private static bool ContainsMeasurements(IRumQueue queue, params string[] measurements)
    {
        var lines = queue.PeekAsync(100, CancellationToken.None).GetAwaiter().GetResult();
        return measurements.All(measurement =>
            lines.Any(item => item.Line.StartsWith(measurement + ",", StringComparison.Ordinal)));
    }

    private static int CountMeasurements(IRumQueue queue, string measurement)
    {
        var lines = queue.PeekAsync(100, CancellationToken.None).GetAwaiter().GetResult();
        return lines.Count(item => item.Line.StartsWith(measurement + ",", StringComparison.Ordinal));
    }

    private static void PumpFor(TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }

        Assert.True(condition(), failureMessage);
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static RumClient CreateClient(
        IRumQueue rumQueue,
        MemoryReplayQueue? replayQueue = null,
        bool sessionReplayEnabled = false)
    {
        return new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "webview-runtime-smoke",
                Env = "local",
                FlushInterval = TimeSpan.FromHours(1),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = sessionReplayEnabled,
                    AndroidCompatibilityMode = sessionReplayEnabled
                }
            },
            rumQueue,
            new RetryRumTransport(),
            replayQueue ?? new MemoryReplayQueue(),
            new RetryReplayTransport());
    }

    private sealed class MemoryReplayQueue : ISessionReplayQueue
    {
        private long nextId;

        public List<QueuedSessionReplaySegment> Items { get; } = new();

        public Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedSessionReplaySegment(
                ++nextId,
                contentType,
                body,
                DateTimeOffset.UtcNow,
                body.Length));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueuedSessionReplaySegment>>(Items.Take(count).ToArray());

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
        {
            Items.RemoveAll(item => ids.Contains(item.Id));
            return Task.CompletedTask;
        }

        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] ExtractAndDecompressSegment(byte[] multipartBody)
    {
        var marker = Encoding.ASCII.GetBytes("Content-Type: application/octet-stream\r\n\r\n");
        var start = IndexOf(multipartBody, marker);
        Assert.True(start >= 0);
        start += marker.Length;
        var endMarker = Encoding.ASCII.GetBytes("\r\n--guance-rum-replay-");
        var end = IndexOf(multipartBody, endMarker, start);
        Assert.True(end > start);

        var compressedLength = end - start;
        using var compressed = new MemoryStream(multipartBody, start + 2, compressedLength - 6);
        using var zlib = new DeflateStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static int IndexOf(byte[] source, byte[] pattern, int start = 0)
    {
        for (var i = start; i <= source.Length - pattern.Length; i++)
        {
            var matches = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class RetryRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));

        public void Dispose()
        {
        }
    }

    private sealed class RetryReplayTransport : ISessionReplayTransport
    {
        public Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold replay queue for assertions"));

        public void Dispose()
        {
        }
    }

    private sealed class CapturingReplayTransport : ISessionReplayTransport
    {
        private readonly SessionReplayTransport inner;

        public CapturingReplayTransport(RumConfig config)
        {
            inner = new SessionReplayTransport(config);
        }

        public List<string> Payloads { get; } = new();

        public Task<SendResult> SendAsync(
            QueuedSessionReplaySegment segment,
            CancellationToken cancellationToken)
        {
            Payloads.Add(Encoding.UTF8.GetString(ExtractAndDecompressSegment(segment.Body)));
            return inner.SendAsync(segment, cancellationToken);
        }

        public void Dispose()
        {
            inner.Dispose();
        }
    }
}
#endif
