#if WINDOWS
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Guance.Windows.Queue;
using Guance.Windows.Samples;
using Guance.Windows.SessionReplay;
using Guance.Windows.Transport;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace Guance.Windows.Tests;

[Collection(WpfUiTestCollection.Name)]
[Trait("Execution", "DesktopUiRuntime")]
public sealed class WebView2RuntimeSmokeTests
{
    private const string TestUrlEnvironmentVariable = "GUANCE_RUM_WEBVIEW_TEST_URL";
    private const string DatawayUrlEnvironmentVariable = "GUANCE_RUM_DATAWAY_URL";
    private const string ClientTokenEnvironmentVariable = "GUANCE_RUM_CLIENT_TOKEN";
    private const string AppIdEnvironmentVariable = "GUANCE_RUM_APP_ID";

    [WebViewRuntimeFact]
    public async Task WebView2Runtime_CollectsConfiguredPageInteractionErrorAndRequest()
    {
        var settings = LoadRuntimeSettings();
        var configuredUrl = settings.WebViewUrl;
        Assert.True(
            Uri.TryCreate(configuredUrl, UriKind.Absolute, out var testUri) &&
            testUri.Scheme is "http" or "https",
            $"webViewUrl/{TestUrlEnvironmentVariable} must be an absolute HTTP or HTTPS URL.");

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
                    "submit.click();" +
                    "const keyboardButton=document.createElement('button');" +
                    "keyboardButton.setAttribute('aria-label','Keyboard action');" +
                    "document.body.appendChild(keyboardButton);" +
                    "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));" +
                    "keyboardButton.click();" +
                    "document.dispatchEvent(new KeyboardEvent('keyup',{key:'Enter',bubbles:true}));");
                PumpUntil(
                    () => controlCheck.IsCompleted,
                    TimeSpan.FromSeconds(10),
                    "WebView2 did not execute the single-action control check.");
                controlCheck.GetAwaiter().GetResult();
                PumpUntil(
                    () => CountMeasurements(rumQueue, "action") >= actionCountBeforeControlCheck + 3,
                    TimeSpan.FromSeconds(10),
                    "WebView2 bridge did not collect the control actions.");
                PumpFor(TimeSpan.FromMilliseconds(250));
                Assert.Equal(
                    actionCountBeforeControlCheck + 3,
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
                    item.Line.StartsWith("action,", StringComparison.Ordinal) &&
                    item.Line.Contains("action_name=Keyboard\\ action", StringComparison.Ordinal) &&
                    item.Line.Contains("action_type=key", StringComparison.Ordinal));
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
    [Trait("Category", "Phase2")]
    public async Task WebView2Runtime_MergesBrowserReplayIntoNativeWebViewSlot()
    {
        var settings = LoadRuntimeSettings();
        var configuredUrl = settings.WebViewUrl;
        Assert.True(
            Uri.TryCreate(configuredUrl, UriKind.Absolute, out var testUri) &&
            testUri.Scheme is "http" or "https",
            $"webViewUrl/{TestUrlEnvironmentVariable} must be an absolute HTTP or HTTPS URL.");

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
                var nativePayloads = payloads
                    .Where(payload => payload.Contains("\"type\":\"webview\"", StringComparison.Ordinal))
                    .ToArray();
                Assert.NotEmpty(nativePayloads);
                Assert.All(nativePayloads, payload =>
                {
                    var webViewWireframe = GetWebViewWireframe(payload, slotId);
                    Assert.Equal(long.Parse(slotId), webViewWireframe.GetProperty("id").GetInt64());
                    Assert.True(webViewWireframe.GetProperty("width").GetInt64() > 0);
                    Assert.True(webViewWireframe.GetProperty("height").GetInt64() > 0);
                });

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
    [Trait("Category", "Phase2")]
    public async Task WebView2Runtime_UploadsMergedReplayToDataway()
    {
        var settings = LoadRuntimeSettings();
        var configuredUrl = settings.WebViewUrl;
        var datawayUrl = settings.Rum.DatawayUrl;
        var clientToken = settings.Rum.ClientToken;
        var appId = settings.Rum.RumAppId;
        Assert.True(
            Uri.TryCreate(configuredUrl, UriKind.Absolute, out var testUri) &&
            testUri.Scheme is "http" or "https",
            $"webViewUrl/{TestUrlEnvironmentVariable} must be an absolute HTTP or HTTPS URL.");

        var validationRunId = "webview-slot-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        await RunStaAsync(() =>
        {
            _ = System.Windows.Application.Current ??
                new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var config = new GuanceConfig
            {
                DatawayUrl = datawayUrl,
                ClientToken = clientToken,
                RumAppId = appId!,
                ServiceName = "wpf-webview-replay-validation",
                Env = "local",
                Version = "0.1.0-webview-slot-validation",
                FlushInterval = TimeSpan.FromHours(1),
                HttpTimeout = TimeSpan.FromSeconds(20),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
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
            var client = new GuanceClient(
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
                var nativePayloads = replayTransport.Payloads
                    .Where(payload => payload.Contains("\"type\":\"webview\"", StringComparison.Ordinal))
                    .ToArray();
                Assert.NotEmpty(nativePayloads);
                Assert.All(nativePayloads, payload =>
                {
                    var webViewWireframe = GetWebViewWireframe(payload, slotId);
                    Assert.Equal(long.Parse(slotId), webViewWireframe.GetProperty("id").GetInt64());
                    Assert.True(webViewWireframe.GetProperty("width").GetInt64() > 0);
                    Assert.True(webViewWireframe.GetProperty("height").GetInt64() > 0);
                });
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
            if (string.IsNullOrWhiteSpace(LoadRuntimeSettings().WebViewUrl))
            {
                Skip = $"webViewUrl or {TestUrlEnvironmentVariable} is not set; WebView2 runtime smoke test skipped.";
            }
        }
    }

    private sealed class WebViewDatawayValidationFactAttribute : FactAttribute
    {
        public WebViewDatawayValidationFactAttribute()
        {
            var settings = LoadRuntimeSettings();
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(settings.WebViewUrl))
            {
                missing.Add($"webViewUrl/{TestUrlEnvironmentVariable}");
            }
            if (string.IsNullOrWhiteSpace(settings.Rum.DatawayUrl))
            {
                missing.Add($"datawayUrl/{DatawayUrlEnvironmentVariable}");
            }
            if (string.IsNullOrWhiteSpace(settings.Rum.ClientToken))
            {
                missing.Add($"clientToken/{ClientTokenEnvironmentVariable}");
            }
            if (string.IsNullOrWhiteSpace(settings.Rum.RumAppId))
            {
                missing.Add($"rumAppId/{AppIdEnvironmentVariable}");
            }
            if (missing.Count > 0)
            {
                Skip = "Missing RUM settings: " + string.Join(", ", missing);
            }
        }
    }

    private static SampleRumSettings LoadRuntimeSettings()
    {
        return SampleGuanceConfig.Resolve("", "", Array.Empty<string>());
    }

    private static bool ContainsMeasurements(MemoryRumQueue queue, params string[] measurements)
    {
        var lines = queue.PeekAsync(100, CancellationToken.None).GetAwaiter().GetResult();
        return measurements.All(measurement =>
            lines.Any(item => item.Line.StartsWith(measurement + ",", StringComparison.Ordinal)));
    }

    private static int CountMeasurements(MemoryRumQueue queue, string measurement)
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

    private static GuanceClient CreateClient(
        IRumQueue rumQueue,
        MemoryReplayQueue? replayQueue = null,
        bool sessionReplayEnabled = false)
    {
        return new GuanceClient(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "webview-runtime-smoke",
                Env = "local",
                FlushInterval = TimeSpan.FromHours(1),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = sessionReplayEnabled
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

        public Task<BatchAppendResult> EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedSessionReplaySegment(
                ++nextId,
                contentType,
                body,
                DateTimeOffset.UtcNow,
                body.Length));
            return Task.FromResult(BatchAppendResult.Ready);
        }

        public Task<QueueBatch<QueuedSessionReplaySegment>?> AcquireAsync(CancellationToken cancellationToken) =>
            TestQueueLease.AcquireAsync(Items, item => item.Size, cancellationToken, maximumItems: 1);

        public Task CompleteAsync(string leaseId, CancellationToken cancellationToken) =>
            TestQueueLease.CompleteAsync(Items, leaseId, cancellationToken);

        public Task AbandonAsync(string leaseId, CancellationToken cancellationToken) =>
            TestQueueLease.AbandonAsync(Items, leaseId, cancellationToken);

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

    private static JsonElement GetWebViewWireframe(string payload, string slotId)
    {
        using var json = JsonDocument.Parse(payload);
        var wireframes = json.RootElement
            .GetProperty("records")
            .EnumerateArray()
            .Where(record => record.GetProperty("type").GetInt32() == 10)
            .SelectMany(record => record.GetProperty("data").GetProperty("wireframes").EnumerateArray())
            .Where(wireframe =>
                wireframe.GetProperty("type").GetString() == "webview" &&
                wireframe.GetProperty("slotId").GetString() == slotId)
            .Select(wireframe => wireframe.Clone())
            .ToArray();
        return Assert.Single(wireframes);
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

        public CapturingReplayTransport(GuanceConfig config)
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
