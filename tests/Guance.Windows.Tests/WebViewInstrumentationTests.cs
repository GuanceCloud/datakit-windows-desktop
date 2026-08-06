using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Guance.Windows.Queue;
using Guance.Windows.SessionReplay;
using Guance.Windows.Transport;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class WebViewInstrumentationTests
{
    [Fact]
    public async Task AttachedWebView_CollectsPageActionErrorAndResourceEvents()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = CreateClient(rumQueue);
        var webView = new FakeWebView2();

        client.StartView("Native host");
        client.AttachWebView(webView);
        var script = await webView.Core.WaitForInjectedScriptAsync();
        var token = ReadBridgeToken(script);
        const string pageUrl = "https://webview.example.test/checkout?token=source-secret";

        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "view",
            url = pageUrl,
            title = ""
        });
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "action",
            name = "Place order",
            actionType = "submit",
            durationMs = 12.5
        });
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "action",
            name = "Keyboard order",
            actionType = "key",
            durationMs = 0
        });
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "error",
            message = "checkout failed",
            stack = "at checkout.js:10:2",
            errorType = "TypeError"
        });
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "resource",
            url = "https://api.example.test/orders?token=secret",
            method = "POST",
            status = 201,
            durationMs = 42,
            responseSize = 128,
            requestSize = 64,
            resourceType = "fetch"
        });
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "resource",
            url = "https://api.example.test/unavailable",
            method = "GET",
            status = 0,
            durationMs = 17,
            resourceType = "fetch"
        });
        client.StopView();

        var lines = (await rumQueue.PeekAsync(20, CancellationToken.None))
            .Select(item => item.Line)
            .ToArray();

        Assert.Contains(lines, line =>
            line.StartsWith("view,", StringComparison.Ordinal) &&
            line.Contains(
                "view_name=https://webview.example.test/checkout?token\\=%3Credacted%3E",
                StringComparison.Ordinal) &&
            line.Contains("view_type=\"webview\"", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.StartsWith("action,", StringComparison.Ordinal) &&
            line.Contains("action_name=Place\\ order", StringComparison.Ordinal) &&
            line.Contains("action_type=click", StringComparison.Ordinal) &&
            line.Contains("action_source=\"webview\"", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.StartsWith("action,", StringComparison.Ordinal) &&
            line.Contains("action_name=Keyboard\\ order", StringComparison.Ordinal) &&
            line.Contains("action_type=key", StringComparison.Ordinal) &&
            line.Contains("action_source=\"webview\"", StringComparison.Ordinal));
        Assert.All(
            lines.Where(line => line.StartsWith("action,", StringComparison.Ordinal)),
            line => Assert.True(
                line.Contains(",action_type=click,", StringComparison.Ordinal) ||
                line.Contains(",action_type=key,", StringComparison.Ordinal),
                $"Unexpected automatic WebView action type: {line}"));
        Assert.Contains(lines, line =>
            line.StartsWith("error,", StringComparison.Ordinal) &&
            line.Contains("error_source=webview", StringComparison.Ordinal) &&
            line.Contains("error_message=\"checkout failed\"", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.StartsWith("resource,", StringComparison.Ordinal) &&
            line.Contains("resource_url=https://api.example.test/orders?token\\=%3Credacted%3E", StringComparison.Ordinal) &&
            line.Contains("resource_status=201", StringComparison.Ordinal) &&
            line.Contains(" duration=42000000i,", StringComparison.Ordinal) &&
            line.Contains("resource_timing_duration=42000000i", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.StartsWith("resource,", StringComparison.Ordinal) &&
            line.Contains("resource_url=https://api.example.test/unavailable", StringComparison.Ordinal) &&
            line.Contains("resource_status=0", StringComparison.Ordinal) &&
            line.Contains(" duration=17000000i,", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("source-secret", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.StartsWith("resource,", StringComparison.Ordinal) &&
            line.Contains("webview_host_view_name=\"Native host\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachedWebView_RejectsMalformedMessagesAndDetachesWithoutDuplicateInjection()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = CreateClient(rumQueue);
        var webView = new FakeWebView2();

        client.AttachWebView(webView);
        client.AttachWebView(webView);
        var script = await webView.Core.WaitForInjectedScriptAsync();
        var token = ReadBridgeToken(script);
        const string pageUrl = "https://webview.example.test/settings";

        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "action",
            name = "Malformed action",
            actionType = new { unexpected = true }
        });
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "resource",
            url = "https://api.example.test/malformed",
            method = "GET",
            status = "not-a-number",
            durationMs = -1
        });
        client.DetachWebView(webView);
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "action",
            name = "After detach",
            actionType = "click"
        });
        client.AttachWebView(webView);
        await webView.Core.WaitForInjectionCountAsync(2);
        var replacementToken = ReadBridgeToken(webView.Core.InjectedScripts[^1]);
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token = replacementToken,
            type = "action",
            name = "After reattach",
            actionType = "click"
        });

        var lines = await rumQueue.PeekAsync(20, CancellationToken.None);
        Assert.DoesNotContain(lines, item => item.Line.Contains("After\\ detach", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, item => item.Line.StartsWith("resource,", StringComparison.Ordinal));
        Assert.Contains(lines, item => item.Line.Contains("After\\ reattach", StringComparison.Ordinal));
        Assert.Equal(2, webView.Core.ScriptInjectionCount);
        Assert.Equal(1, webView.Core.ScriptRemovalCount);
        Assert.Contains(webView.Core.ExecutedScripts, scriptText =>
            scriptText.Contains("__guanceRumWebViewBridge.cleanup()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachedWinUiWindow_AutomaticallyDiscoversNestedWebView()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = CreateClient(rumQueue);
        var webView = new FakeWebView2();
        var window = new FakeWinUiWindow { Content = webView };

        client.AttachWinUIWindow(window, "WebView host");

        var script = await webView.Core.WaitForInjectedScriptAsync();
        Assert.Contains("guance-rum-webview", script, StringComparison.Ordinal);
        Assert.Equal(1, webView.Core.ScriptInjectionCount);
    }

    [Fact]
    public async Task AttachedWebView_ControlDisposalDetachesBridgeHandlers()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = CreateClient(rumQueue);
        var webView = new FakeWebView2();

        client.AttachWebView(webView);
        var token = ReadBridgeToken(await webView.Core.WaitForInjectedScriptAsync());
        webView.DisposeControl();
        webView.Core.PostBridgeMessage("https://webview.example.test/disposed", new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "action",
            name = "After control disposal",
            actionType = "click"
        });

        var lines = await rumQueue.PeekAsync(20, CancellationToken.None);
        Assert.DoesNotContain(lines, item => item.Line.StartsWith("action,", StringComparison.Ordinal));
        Assert.Equal(1, webView.Core.ScriptRemovalCount);
    }

    [Fact]
    public async Task AttachedWebView_DetachDuringScriptRegistrationRemovesLateRegistration()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = CreateClient(rumQueue);
        var core = new FakeCoreWebView2 { DelayScriptRegistration = true };
        var webView = new FakeWebView2(core);

        client.AttachWebView(webView);
        await core.WaitForInjectedScriptAsync();
        client.DetachWebView(webView);
        core.CompleteScriptRegistration();
        await core.WaitForRemovalCountAsync(1);

        Assert.Equal(1, core.ScriptRemovalCount);
    }

    [Fact]
    public void BridgeScript_ClassifiesClickChangeAndSubmitByInputSource()
    {
        var script = WebViewBridgeScript.Create("test-token");

        Assert.Contains(
            "closest('button,a,[role=\"button\"],[data-guance-action]')",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "closest('button,a,input,select,textarea",
            script,
            StringComparison.Ordinal);
        Assert.Contains("if (!element)", script, StringComparison.Ordinal);
        Assert.Contains("isSubmitControl", script, StringComparison.Ordinal);
        Assert.Contains("isChangeControl", script, StringComparison.Ordinal);
        Assert.Contains("on(document, 'pointerdown'", script, StringComparison.Ordinal);
        Assert.Contains("on(document, 'keydown'", script, StringComparison.Ordinal);
        Assert.Contains("actionType: currentActionType()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("actionType: 'input'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("actionType: 'submit'", script, StringComparison.Ordinal);
        Assert.Contains("title: text(document.title)", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "text(document.title) || String(location.href)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Phase2")]
    public void BridgeScript_ExposesSessionReplayRecordsCapability()
    {
        var script = WebViewBridgeScript.Create(
            "test-token",
            sessionReplayEnabled: true,
            sessionReplayPrivacyLevel: "mask-user-input");

        Assert.Contains("window.FTWebViewJavascriptBridge = nativeBridge", script, StringComparison.Ordinal);
        Assert.Contains("return JSON.stringify(REPLAY_ENABLED ? ['records'] : [])", script, StringComparison.Ordinal);
        Assert.Contains("event.name === 'session_replay'", script, StringComparison.Ordinal);
        Assert.Contains("window.DATAFLUX_RUM.takeSubsequentFullSnapshot()", script, StringComparison.Ordinal);
        Assert.Contains("const REPLAY_PRIVACY = \"mask-user-input\"", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Phase2")]
    public async Task AttachedWebView_MergesNativeAndBrowserReplayWithSameSlotId()
    {
        var rumQueue = new MemoryRumQueue();
        var replayQueue = new MemoryReplayQueue();
        await using var client = CreateClient(rumQueue, replayQueue, sessionReplayEnabled: true);
        var webView = new FakeWebView2();
        var root = new FakeReplayRoot();
        root.Children.Add(webView);

        client.StartView("Native host");
        client.AttachWebView(webView);
        var script = await webView.Core.WaitForInjectedScriptAsync();
        var token = ReadBridgeToken(script);
        client.CaptureSessionReplayFullSnapshot(root);
        var browserViewId = Guid.NewGuid().ToString("D");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000;
        const string pageUrl = "https://webview.example.test/replay";

        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "rum",
            record = new
            {
                measurement = "view",
                time = timestamp,
                tags = new
                {
                    view_id = browserViewId,
                    view_name = "Browser replay view",
                    view_url = "https://webview.example.test/replay?token=browser-secret",
                    app_id = "untrusted-app",
                    session_id = "untrusted-session",
                    service = "untrusted-service",
                    env = "untrusted-env",
                    version = "untrusted-version",
                    sdk_name = "untrusted-sdk"
                },
                fields = new { time_spent = 0 }
            }
        });
        webView.Core.PostBridgeMessage(pageUrl, new
        {
            channel = "guance-rum-webview",
            version = 1,
            token,
            type = "session_replay",
            record = new
            {
                type = 2,
                timestamp,
                slotId = "untrusted-page-value",
                data = new
                {
                    node = new { type = 0, id = 1, childNodes = Array.Empty<object>() }
                }
            }
        });

        await Task.Delay(100);
        await client.FlushAsync();
        await Task.Delay(100);
        await client.FlushAsync();

        var rumLines = await rumQueue.PeekAsync(20, CancellationToken.None);
        Assert.Contains(rumLines, item =>
            item.Line.StartsWith("view,", StringComparison.Ordinal) &&
            item.Line.Contains("is_web_view=True", StringComparison.Ordinal) &&
            item.Line.Contains("container=", StringComparison.Ordinal) &&
            item.Line.Contains("container={\"source\":\"windows\"", StringComparison.Ordinal));
        Assert.DoesNotContain(rumLines, item => item.Line.Contains("browser-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(rumLines, item => item.Line.Contains("untrusted-", StringComparison.Ordinal));
        Assert.Contains(rumLines, item =>
            item.Line.StartsWith("view,", StringComparison.Ordinal) &&
            item.Line.Contains($"view_id={browserViewId}", StringComparison.Ordinal) &&
            item.Line.Contains("service=df_rum_windows", StringComparison.Ordinal) &&
            item.Line.Contains("env=local", StringComparison.Ordinal));

        var payloads = replayQueue.Items
            .Select(item => Encoding.UTF8.GetString(ExtractAndDecompressSegment(item.Body)))
            .ToArray();
        var nativePayload = Assert.Single(payloads, payload => payload.Contains("\"type\":\"webview\"", StringComparison.Ordinal));
        var slotMatch = Regex.Match(nativePayload, "\"type\":\"webview\",\"slotId\":\"(?<slot>[^\"]+)\"");
        Assert.True(slotMatch.Success, "Native replay payload did not contain a WebView slot.");
        var slotId = slotMatch.Groups["slot"].Value;
        Assert.Contains(payloads, payload =>
            payload.Contains("\"type\":2", StringComparison.Ordinal) &&
            payload.Contains($"\"slotId\":\"{slotId}\"", StringComparison.Ordinal) &&
            payload.Contains($"\"view\":{{\"id\":\"{browserViewId}\"}}", StringComparison.Ordinal));
        Assert.DoesNotContain(payloads, payload =>
            payload.Contains("\"slotId\":\"untrusted-page-value\"", StringComparison.Ordinal));
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

    [Fact]
    public async Task AttachedWebView_ReportsFailedNavigation()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = CreateClient(rumQueue);
        var webView = new FakeWebView2();

        client.AttachWebView(webView);
        await webView.Core.WaitForInjectedScriptAsync();
        webView.Core.FailNavigation(
            "https://webview.example.test/failed?token=navigation-secret",
            "ConnectionAborted");

        var lines = await rumQueue.PeekAsync(20, CancellationToken.None);
        Assert.Contains(lines, item =>
            item.Line.StartsWith("error,", StringComparison.Ordinal) &&
            item.Line.Contains("error_type=WebView2NavigationError", StringComparison.Ordinal) &&
            item.Line.Contains("webview_navigation_status=\"ConnectionAborted\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, item => item.Line.Contains("navigation-secret", StringComparison.Ordinal));
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
                RumAppId = "app",
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

    private static string ReadBridgeToken(string script)
    {
        var match = Regex.Match(script, @"const TOKEN = (?<token>""[^""]+"");", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The injected bridge script did not contain its scoped token.");
        return JsonSerializer.Deserialize<string>(match.Groups["token"].Value)!;
    }

    private sealed class FakeWebView2
    {
        public FakeWebView2()
            : this(new FakeCoreWebView2())
        {
        }

        public FakeWebView2(FakeCoreWebView2 core)
        {
            Core = core;
        }

        public FakeCoreWebView2 Core { get; }

        public FakeCoreWebView2 CoreWebView2 => Core;

        public double ActualWidth { get; } = 640;

        public double ActualHeight { get; } = 480;

        public event EventHandler? Disposed;

        public void DisposeControl()
        {
            Disposed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeWinUiWindow
    {
        public string? Title { get; set; }

        public object? Content { get; set; }
    }

    private sealed class FakeReplayRoot
    {
        public double Width { get; } = 800;

        public double Height { get; } = 600;

        public List<object> Children { get; } = new();
    }

    private sealed class FakeCoreWebView2
    {
        private readonly TaskCompletionSource<string> injectedScript = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> scriptRegistration = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<FakeWebMessageReceivedEventArgs>? WebMessageReceived;

        public event EventHandler<FakeNavigationStartingEventArgs>? NavigationStarting;

        public event EventHandler<FakeNavigationCompletedEventArgs>? NavigationCompleted;

        public List<string> InjectedScripts { get; } = new();

        public List<string> ExecutedScripts { get; } = new();

        public bool DelayScriptRegistration { get; init; }

        public int ScriptInjectionCount { get; private set; }

        public int ScriptRemovalCount { get; private set; }

        public Task<string> AddScriptToExecuteOnDocumentCreatedAsync(string javaScript)
        {
            ScriptInjectionCount++;
            InjectedScripts.Add(javaScript);
            injectedScript.TrySetResult(javaScript);
            return DelayScriptRegistration
                ? scriptRegistration.Task
                : Task.FromResult("script-id");
        }

        public Task<string> ExecuteScriptAsync(string javaScript)
        {
            ExecutedScripts.Add(javaScript);
            return Task.FromResult("null");
        }

        public void RemoveScriptToExecuteOnDocumentCreated(string scriptId)
        {
            ScriptRemovalCount++;
        }

        public Task<string> WaitForInjectedScriptAsync()
        {
            return injectedScript.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task WaitForInjectionCountAsync(int expectedCount)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (ScriptInjectionCount < expectedCount && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            Assert.True(ScriptInjectionCount >= expectedCount, $"Expected {expectedCount} injected scripts.");
        }

        public void CompleteScriptRegistration()
        {
            scriptRegistration.TrySetResult("script-id");
        }

        public async Task WaitForRemovalCountAsync(int expectedCount)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (ScriptRemovalCount < expectedCount && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            Assert.True(ScriptRemovalCount >= expectedCount, $"Expected {expectedCount} removed scripts.");
        }

        public void PostBridgeMessage(string source, object message)
        {
            WebMessageReceived?.Invoke(
                this,
                new FakeWebMessageReceivedEventArgs(source, JsonSerializer.Serialize(message)));
        }

        public void FailNavigation(string uri, string status)
        {
            NavigationStarting?.Invoke(this, new FakeNavigationStartingEventArgs(uri));
            NavigationCompleted?.Invoke(this, new FakeNavigationCompletedEventArgs(false, status));
        }
    }

    private sealed record FakeNavigationStartingEventArgs(string Uri);

    private sealed record FakeNavigationCompletedEventArgs(bool IsSuccess, string WebErrorStatus);

    private sealed class FakeWebMessageReceivedEventArgs : EventArgs
    {
        public FakeWebMessageReceivedEventArgs(string source, string webMessageAsJson)
        {
            Source = source;
            WebMessageAsJson = webMessageAsJson;
        }

        public string Source { get; }

        public string WebMessageAsJson { get; }
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
}
