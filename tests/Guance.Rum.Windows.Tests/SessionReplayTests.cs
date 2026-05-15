using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Guance.Rum.Windows.SessionReplay;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class SessionReplayTests
{
    [Fact]
    public void SegmentBuilder_WritesExpectedMultipartFieldsAndSegmentFile()
    {
        var context = new SessionReplayContext(
            AppId: "app",
            SessionId: "session",
            ViewId: "view",
            Service: "svc",
            Env: "prod",
            Version: "1.2.3",
            SdkName: "guance-rum-windows",
            SdkVersion: "0.1.0");
        var records = new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = 2,
                ["timestamp"] = 10,
                ["data"] = new Dictionary<string, object?> { ["node"] = new Dictionary<string, object?> { ["tagName"] = "html" } }
            }
        };

        var builder = new SessionReplaySegmentBuilder(context, records, hasFullSnapshot: true, creationReason: "full_snapshot", indexInView: 7, startMilliseconds: 10, endMilliseconds: 12);
        var (contentType, body) = builder.Build();
        var text = Encoding.UTF8.GetString(body);
        var segment = JsonSerializer.SerializeToUtf8Bytes(records, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.StartsWith("multipart/form-data; boundary=guance-rum-replay-", contentType, StringComparison.Ordinal);
        Assert.Contains("name=\"app_id\"\r\n\r\napp\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"session_id\"\r\n\r\nsession\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"view_id\"\r\n\r\nview\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"source\"\r\n\r\nwindows\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"index_in_view\"\r\n\r\n7\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"has_full_snapshot\"\r\n\r\ntrue\r\n", text, StringComparison.Ordinal);
        Assert.Contains($"name=\"raw_segment_size\"\r\n\r\n{segment.Length}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"segment\"; filename=\"segment\"", text, StringComparison.Ordinal);
        Assert.Contains("\"tagName\":\"html\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayTransport_AppendsTokenOnlyForDataway()
    {
        var datawayHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var datawayTransport = new SessionReplayTransport(new RumConfig
        {
            DatawayUrl = "https://openway.guance.com",
            ClientToken = "token value",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => datawayHandler
        });

        var segment = new QueuedSessionReplaySegment(1, "multipart/form-data; boundary=x", Encoding.UTF8.GetBytes("--x--\r\n"), DateTimeOffset.UtcNow, 7);
        var result = await datawayTransport.SendAsync(segment, CancellationToken.None);

        Assert.True(result.DeleteFromQueue);
        Assert.Equal("https://openway.guance.com/v1/write/rum/replay?token=token%20value&to_headless=true", datawayHandler.RequestUri!.AbsoluteUri);
        Assert.Equal("multipart/form-data; boundary=x", datawayHandler.ContentType);

        var datakitHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var datakitTransport = new SessionReplayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => datakitHandler
        });
        await datakitTransport.SendAsync(segment, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:9529/v1/write/rum/replay", datakitHandler.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SessionReplayTransport_RetriesServerErrorsAndDropsClientErrors()
    {
        var retryHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var retryTransport = new SessionReplayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => retryHandler
        });
        var segment = new QueuedSessionReplaySegment(1, "multipart/form-data; boundary=x", Encoding.UTF8.GetBytes("--x--\r\n"), DateTimeOffset.UtcNow, 7);
        var retry = await retryTransport.SendAsync(segment, CancellationToken.None);
        Assert.True(retry.RetryLater);
        Assert.False(retry.DeleteFromQueue);

        var dropHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var dropTransport = new SessionReplayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => dropHandler
        });
        var drop = await dropTransport.SendAsync(segment, CancellationToken.None);
        Assert.True(drop.DeleteFromQueue);
        Assert.False(drop.RetryLater);
    }

    [Fact]
    public async Task SessionReplayTransport_RetriesTimeoutAndNetworkFailures()
    {
        using var timeoutTransport = new SessionReplayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new ThrowingHandler(new OperationCanceledException("timeout"))
        });
        var segment = new QueuedSessionReplaySegment(1, "multipart/form-data; boundary=x", Encoding.UTF8.GetBytes("--x--\r\n"), DateTimeOffset.UtcNow, 7);
        var timeout = await timeoutTransport.SendAsync(segment, CancellationToken.None);
        Assert.True(timeout.RetryLater);
        Assert.False(timeout.DeleteFromQueue);

        using var networkTransport = new SessionReplayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new ThrowingHandler(new HttpRequestException("network down"))
        });
        var network = await networkTransport.SendAsync(segment, CancellationToken.None);
        Assert.True(network.RetryLater);
        Assert.False(network.DeleteFromQueue);
    }


    [Fact]
    public async Task SessionReplayQueue_PeeksInFifoOrderAndDeletesById()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "guance-rum-replay-tests", Guid.NewGuid().ToString("N"));
        await using var queue = new SqliteSessionReplayQueue(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            CacheDirectory = tempDir
        });

        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-1"), CancellationToken.None);
        await queue.EnqueueAsync("multipart/b", Encoding.UTF8.GetBytes("body-2"), CancellationToken.None);

        var firstBatch = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal(new[] { "body-1", "body-2" }, firstBatch.Select(item => Encoding.UTF8.GetString(item.Body)).ToArray());

        await queue.DeleteAsync(new[] { firstBatch[0].Id }, CancellationToken.None);
        var secondBatch = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal("body-2", Encoding.UTF8.GetString(Assert.Single(secondBatch).Body));
    }

    [Fact]
    public async Task SessionReplayQueue_TrimDropsOldestWhenItemLimitIsExceeded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "guance-rum-replay-tests", Guid.NewGuid().ToString("N"));
        await using var queue = new SqliteSessionReplayQueue(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            CacheDirectory = tempDir,
            SessionReplay = new RumSessionReplayConfig
            {
                MaxQueueItems = 2
            }
        });

        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-1"), CancellationToken.None);
        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-2"), CancellationToken.None);
        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-3"), CancellationToken.None);
        await queue.TrimAsync(CancellationToken.None);

        var batch = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal(new[] { "body-2", "body-3" }, batch.Select(item => Encoding.UTF8.GetString(item.Body)).ToArray());
    }

    [Fact]
    public void TreeMapper_MasksSensitiveInputsAndHidesSubtrees()
    {
        var root = new FakePanel { Name = "root", Width = 300, Height = 200 };
        var label = new FakeLabel { Text = "Keep me", X = 1, Y = 2, Width = 40, Height = 10 };
        var input = new FakeTextBox { Text = "secret@example.com", X = 5, Y = 6, Width = 100, Height = 20 };
        var hidden = new FakePanel { Name = "hidden", X = 10, Y = 10, Width = 100, Height = 50 };
        hidden.Children.Add(new FakeLabel { Text = "do not capture", Width = 50, Height = 10 });
        root.Children.Add(label);
        root.Children.Add(input);
        root.Children.Add(hidden);

        var overrides = new SessionReplayPrivacyOverrides();
        overrides.SetHidden(hidden, hidden: true);

        var mapped = SessionReplayTreeMapper.Map(root, overrides, new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs });

        Assert.NotNull(mapped);
        Assert.Equal("Keep me", mapped!.Children[0].Text);
        Assert.Equal("********", mapped.Children[1].Text);
        Assert.True(mapped.Children[2].Hidden);
        Assert.Empty(mapped.Children[2].Children);
    }

    [Fact]
    public void TreeMapper_MapsWinUiLikeCommonControls()
    {
        var root = new FakePanel { Name = "root", Width = 400, Height = 300 };
        var button = new FakeWinUIButton { Content = "Save", ActualWidth = 90, ActualHeight = 30, IsEnabled = false };
        var textBox = new FakeWinUITextBox { Text = "customer@example.com", PlaceholderText = "Email", ActualWidth = 160, ActualHeight = 32 };
        var comboBox = new FakeWinUIComboBox { SelectedItem = "Enterprise", SelectedIndex = 2, ActualWidth = 120, ActualHeight = 32 };
        root.Children.Add(button);
        root.Children.Add(textBox);
        root.Children.Add(comboBox);

        var mapped = SessionReplayTreeMapper.Map(root, new SessionReplayPrivacyOverrides(), new RumSessionReplayConfig());

        Assert.NotNull(mapped);
        Assert.Equal("button", mapped!.Children[0].TagName);
        Assert.Equal("Save", mapped.Children[0].Text);
        Assert.Equal("true", mapped.Children[0].Attributes["aria-disabled"]);
        Assert.Equal("input", mapped.Children[1].TagName);
        Assert.Equal("********", mapped.Children[1].Text);
        Assert.Equal("Email", mapped.Children[1].Attributes["placeholder"]);
        Assert.Equal("select", mapped.Children[2].TagName);
        Assert.Equal("********", mapped.Children[2].Text);
        Assert.Equal("2", mapped.Children[2].Attributes["data-selected-index"]);
    }

    [Fact]
    public void TreeMapper_MasksSensitiveLabelTextAndMapsWinUiAttributes()
    {
        var root = new FakePanel { Name = "root", Width = 400, Height = 300 };
        var label = new FakeLabel { Text = "customer@example.com", ActualWidth = 160, ActualHeight = 20 };
        var slider = new FakeWinUISlider { Value = 42, Minimum = 0, Maximum = 100, ActualWidth = 200, ActualHeight = 32 };
        var toggle = new FakeWinUIToggleSwitch { IsOn = true, ActualWidth = 80, ActualHeight = 32 };
        var webView = new FakeWinUIWebView { Source = "https://example.com", ActualWidth = 300, ActualHeight = 180 };
        root.Children.Add(label);
        root.Children.Add(slider);
        root.Children.Add(toggle);
        root.Children.Add(webView);

        var mapped = SessionReplayTreeMapper.Map(root, new SessionReplayPrivacyOverrides(), new RumSessionReplayConfig());

        Assert.NotNull(mapped);
        Assert.Equal("********", mapped!.Children[0].Text);
        Assert.Equal("42", mapped.Children[1].Attributes["aria-valuenow"]);
        Assert.Equal("0", mapped.Children[1].Attributes["aria-valuemin"]);
        Assert.Equal("100", mapped.Children[1].Attributes["aria-valuemax"]);
        Assert.Equal("true", mapped.Children[2].Attributes["aria-checked"]);
        Assert.Equal("iframe", mapped.Children[3].TagName);
        Assert.Equal("https://example.com", mapped.Children[3].Attributes["src"]);
    }

    [Fact]
    public void TreeMapper_UsesPlaceholderForCustomRenderedSubtrees()
    {
        var root = new FakePanel { Name = "root", Width = 400, Height = 300 };
        var surface = new FakeDirectXSurface { ActualWidth = 320, ActualHeight = 180 };
        surface.Children.Add(new FakeLabel { Text = "inside custom renderer", ActualWidth = 100, ActualHeight = 20 });
        root.Children.Add(surface);

        var mapped = SessionReplayTreeMapper.Map(root, new SessionReplayPrivacyOverrides(), new RumSessionReplayConfig());

        Assert.NotNull(mapped);
        var node = Assert.Single(mapped!.Children);
        Assert.Equal("Custom rendered content", node.Text);
        Assert.Equal("true", node.Attributes["data-guance-custom-rendered"]);
        Assert.Equal(nameof(FakeDirectXSurface), node.Attributes["data-guance-renderer"]);
        Assert.Empty(node.Children);
    }

    [Fact]
    public void TreeMapper_EnforcesNodeDepthTextAndAttributeLimits()
    {
        var root = new FakePanel { Name = new string('n', 80), Width = 400, Height = 300 };
        root.Children.Add(new FakeLabel { Text = new string('a', 80), Width = 10, Height = 10 });
        root.Children.Add(new FakeLabel { Text = "second", Width = 10, Height = 10 });
        root.Children.Add(new FakeLabel { Text = "third", Width = 10, Height = 10 });

        var mapped = SessionReplayTreeMapper.Map(
            root,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig
            {
                MaxNodeCount = 2,
                MaxTreeDepth = 8,
                MaxTextLength = 12,
                MaxAttributeValueLength = 16
            });

        Assert.NotNull(mapped);
        Assert.Equal("aaaaaaaaaaaa...", mapped!.Children[0].Text);
        Assert.Equal("Truncated", mapped.Children[1].Text);
        Assert.Equal("true", mapped.Children[1].Attributes["data-guance-truncated"]);
        Assert.Equal(2, mapped.Children.Count);
        Assert.EndsWith("...", mapped.Attributes["data-control-name"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayManager_UsesIncrementalByteEstimateForSegmentLimit()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    SegmentBytesLimit = 256
                }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");

        for (var i = 0; i < 20; i++)
        {
            manager.CaptureIncrementalEvent(context, "input", "Input" + i, 0, 0, new string('x', 40));
        }

        await Task.Delay(100);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        Assert.True(queue.Items.Count > 1);
    }

    [Fact]
    public async Task SessionReplayManager_WritesResizeIncrementalRecord()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig { Enabled = true }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");

        manager.CaptureResizeEvent(context, "MainWindow", 800, 600);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        var body = Encoding.UTF8.GetString(Assert.Single(queue.Items).Body);
        Assert.Contains("\"source\":\"resize\"", body, StringComparison.Ordinal);
        Assert.Contains("\"width\":800", body, StringComparison.Ordinal);
        Assert.Contains("\"height\":600", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayManager_CoalescesRapidResizeRecords()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig { Enabled = true }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");

        manager.CaptureResizeEvent(context, "MainWindow", 800, 600);
        manager.CaptureResizeEvent(context, "MainWindow", 801, 601);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        var body = Encoding.UTF8.GetString(Assert.Single(queue.Items).Body);
        Assert.Contains("name=\"records_count\"\r\n\r\n1\r\n", body, StringComparison.Ordinal);
        Assert.Contains("\"width\":801", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"width\":800", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayManager_KeepsErrorSampledRingBufferUntilError()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    SampleRate = 0.0,
                    OnErrorSampleRate = 1.0
                }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");
        var root = new SessionReplayNode("div", 0, 0, 100, 100) { Text = "buffered" };

        manager.CaptureFullSnapshot(context, root);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);
        Assert.Empty(queue.Items);

        manager.NotifyError(context);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        var item = Assert.Single(queue.Items);
        Assert.Contains("name=\"creation_reason\"\r\n\r\nfull_snapshot\r\n", Encoding.UTF8.GetString(item.Body), StringComparison.Ordinal);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public CaptureHandler(HttpResponseMessage response)
        {
            this.response = response;
        }

        public Uri? RequestUri { get; private set; }
        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            if (request.Content is not null)
            {
                await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            return response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception exception;

        public ThrowingHandler(Exception exception)
        {
            this.exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class MemoryReplayQueue : ISessionReplayQueue
    {
        private long nextId;

        public List<QueuedSessionReplaySegment> Items { get; } = new();

        public Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedSessionReplaySegment(++nextId, contentType, body, DateTimeOffset.UtcNow, body.Length));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<QueuedSessionReplaySegment>>(Items.Take(count).ToArray());
        }

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
        {
            Items.RemoveAll(item => ids.Contains(item.Id));
            return Task.CompletedTask;
        }

        public Task TrimAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private class FakePanel
    {
        public string? Name { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public List<object> Children { get; } = new();
    }

    private sealed class FakeLabel : FakePanel
    {
        public string? Text { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
    }

    private sealed class FakeTextBox : FakePanel
    {
        public string? Text { get; init; }
    }

    private sealed class FakeWinUIButton : FakePanel
    {
        public string? Content { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
        public bool IsEnabled { get; init; } = true;
    }

    private sealed class FakeWinUITextBox : FakePanel
    {
        public string? Text { get; init; }
        public string? PlaceholderText { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
    }

    private sealed class FakeWinUIComboBox : FakePanel
    {
        public string? SelectedItem { get; init; }
        public int SelectedIndex { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
    }

    private sealed class FakeWinUISlider : FakePanel
    {
        public double Value { get; init; }
        public double Minimum { get; init; }
        public double Maximum { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
    }

    private sealed class FakeWinUIToggleSwitch : FakePanel
    {
        public bool IsOn { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
    }

    private sealed class FakeWinUIWebView : FakePanel
    {
        public string? Source { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
    }

    private sealed class FakeDirectXSurface : FakePanel
    {
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
    }
}
