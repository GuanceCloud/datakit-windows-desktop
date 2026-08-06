using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Guance.Windows.Queue;
using Guance.Windows.SessionReplay;
using Xunit;

namespace Guance.Windows.Tests;

[Trait("Category", "Phase2")]
public sealed class SessionReplayTests
{
    [Fact]
    public void SessionReplayConfig_DefaultsToReleaseSafePrivacy()
    {
        var config = new RumSessionReplayConfig();

        Assert.Equal(SessionReplayTextAndInputPrivacy.MaskAll, config.TextAndInputPrivacy);
        Assert.Equal(SessionReplayImagePrivacy.MaskAll, config.ImagePrivacy);
        Assert.True(config.CaptureImages);
    }

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
            SdkName: RumConstants.WindowsSdkName,
            SdkVersion: "0.1.0");
        var records = new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = 10,
                ["timestamp"] = 10,
                ["data"] = new Dictionary<string, object?>
                {
                    ["wireframes"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = 1,
                            ["type"] = "shape",
                            ["x"] = 0,
                            ["y"] = 0,
                            ["width"] = 100,
                            ["height"] = 100
                        }
                    }
                }
            }
        };

        var builder = new SessionReplaySegmentBuilder(context, records, hasFullSnapshot: true, creationReason: "full_snapshot", indexInView: 7, startMilliseconds: 10, endMilliseconds: 12);
        var (contentType, body) = builder.Build();
        var text = Encoding.UTF8.GetString(body);
        var segment = ExtractAndDecompressSegment(body);
        var segmentText = Encoding.UTF8.GetString(segment);

        Assert.StartsWith("multipart/form-data; boundary=guance-rum-replay-", contentType, StringComparison.Ordinal);
        Assert.Contains("name=\"app_id\"\r\n\r\napp\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"session_id\"\r\n\r\nsession\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"view_id\"\r\n\r\nview\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"source\"\r\n\r\nwindows\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"index_in_view\"\r\n\r\n7\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"has_full_snapshot\"\r\n\r\ntrue\r\n", text, StringComparison.Ordinal);
        Assert.Contains($"name=\"raw_segment_size\"\r\n\r\n{segment.Length}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("name=\"segment\"; filename=\"view\"", text, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/octet-stream", text, StringComparison.Ordinal);
        Assert.Contains("\"application\":{\"id\":\"app\"}", segmentText, StringComparison.Ordinal);
        Assert.Contains("\"session\":{\"id\":\"session\"}", segmentText, StringComparison.Ordinal);
        Assert.Contains("\"view\":{\"id\":\"view\"}", segmentText, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"windows\"", segmentText, StringComparison.Ordinal);
        Assert.Contains("\"records\":[", segmentText, StringComparison.Ordinal);
        Assert.Contains("\"type\":10", segmentText, StringComparison.Ordinal);
        Assert.Contains("\"wireframes\":[", segmentText, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"shape\"", segmentText, StringComparison.Ordinal);
        Assert.EndsWith("\n", segmentText, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentBuilder_NormalizesGuidIdsForWindowsSchema()
    {
        var appId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var viewId = Guid.NewGuid();
        var context = new SessionReplayContext(
            AppId: appId.ToString("N"),
            SessionId: sessionId.ToString("N"),
            ViewId: viewId.ToString("N"),
            Service: "svc",
            Env: "prod",
            Version: "1.2.3",
            SdkName: RumConstants.WindowsSdkName,
            SdkVersion: "0.1.0");
        var records = new object[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = 10,
                ["timestamp"] = 10,
                ["data"] = new Dictionary<string, object?>
                {
                    ["wireframes"] = Array.Empty<object>()
                }
            }
        };

        var (_, body) = new SessionReplaySegmentBuilder(context, records, true, "full_snapshot", 0, 10, 10).Build();
        var segmentText = Encoding.UTF8.GetString(ExtractAndDecompressSegment(body));

        Assert.Contains($"\"application\":{{\"id\":\"{appId:D}\"}}", segmentText, StringComparison.Ordinal);
        Assert.Contains($"\"session\":{{\"id\":\"{sessionId:D}\"}}", segmentText, StringComparison.Ordinal);
        Assert.Contains($"\"view\":{{\"id\":\"{viewId:D}\"}}", segmentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayTransport_AppendsTokenOnlyForDataway()
    {
        var datawayHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var datawayTransport = new SessionReplayTransport(new GuanceConfig
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
        using var datakitTransport = new SessionReplayTransport(new GuanceConfig
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
        using var retryTransport = new SessionReplayTransport(new GuanceConfig
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
        using var dropTransport = new SessionReplayTransport(new GuanceConfig
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
    public async Task SessionReplayTransport_IncludesResponseBodyAndRedactedUriForClientErrors()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("replay endpoint is disabled")
        };
        using var transport = new SessionReplayTransport(new GuanceConfig
        {
            DatawayUrl = "https://openway.guance.com",
            ClientToken = "secret token",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new CaptureHandler(response)
        });
        var segment = new QueuedSessionReplaySegment(1, "multipart/form-data; boundary=x", Encoding.UTF8.GetBytes("--x--\r\n"), DateTimeOffset.UtcNow, 7);

        var result = await transport.SendAsync(segment, CancellationToken.None);

        Assert.True(result.DeleteFromQueue);
        Assert.False(result.RetryLater);
        Assert.Contains("response_body=replay endpoint is disabled", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("token=redacted", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret token", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("content_type=multipart/form-data; boundary=x", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("body_bytes=7", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayTransport_RetriesTimeoutAndNetworkFailures()
    {
        using var timeoutTransport = new SessionReplayTransport(new GuanceConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new ThrowingHandler(new OperationCanceledException("timeout"))
        });
        var segment = new QueuedSessionReplaySegment(1, "multipart/form-data; boundary=x", Encoding.UTF8.GetBytes("--x--\r\n"), DateTimeOffset.UtcNow, 7);
        var timeout = await timeoutTransport.SendAsync(segment, CancellationToken.None);
        Assert.True(timeout.RetryLater);
        Assert.False(timeout.DeleteFromQueue);

        using var networkTransport = new SessionReplayTransport(new GuanceConfig
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
    public async Task SessionReplayQueue_LeasesInFifoOrderAndCompletesWholeFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "guance-rum-replay-tests", Guid.NewGuid().ToString("N"));
        await using var queue = new BatchFileSessionReplayQueue(new GuanceConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            CacheDirectory = tempDir
        });

        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-1"), CancellationToken.None);
        await queue.EnqueueAsync("multipart/b", Encoding.UTF8.GetBytes("body-2"), CancellationToken.None);

        var firstBatch = Assert.IsType<QueueBatch<QueuedSessionReplaySegment>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("body-1", Encoding.UTF8.GetString(Assert.Single(firstBatch.Items).Body));

        await queue.CompleteAsync(firstBatch.LeaseId, CancellationToken.None);
        var secondBatch = Assert.IsType<QueueBatch<QueuedSessionReplaySegment>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("body-2", Encoding.UTF8.GetString(Assert.Single(secondBatch.Items).Body));
    }

    [Fact]
    public async Task SessionReplayQueue_DropsOldestWhenSharedFileLimitIsExceeded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "guance-rum-replay-tests", Guid.NewGuid().ToString("N"));
        await using var queue = new BatchFileSessionReplayQueue(new GuanceConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            CacheDirectory = tempDir,
            Cache = new CacheOptions
            {
                MaxDiskBytes = 4L * 1024 * 1024,
                MaxFiles = 2
            }
        });

        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-1"), CancellationToken.None);
        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-2"), CancellationToken.None);
        await queue.EnqueueAsync("multipart/a", Encoding.UTF8.GetBytes("body-3"), CancellationToken.None);
        var first = Assert.IsType<QueueBatch<QueuedSessionReplaySegment>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("body-2", Encoding.UTF8.GetString(Assert.Single(first.Items).Body));
        await queue.CompleteAsync(first.LeaseId, CancellationToken.None);
        var second = Assert.IsType<QueueBatch<QueuedSessionReplaySegment>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("body-3", Encoding.UTF8.GetString(Assert.Single(second.Items).Body));
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
        var textBox = new FakeWinUITextBox { Text = "ordinary input", PlaceholderText = "Display name", ActualWidth = 160, ActualHeight = 32 };
        var comboBox = new FakeWinUIComboBox { SelectedItem = "Enterprise", SelectedIndex = 2, ActualWidth = 120, ActualHeight = 32 };
        root.Children.Add(button);
        root.Children.Add(textBox);
        root.Children.Add(comboBox);

        var mapped = SessionReplayTreeMapper.Map(
            root,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs });

        Assert.NotNull(mapped);
        Assert.Equal("button", mapped!.Children[0].TagName);
        Assert.Equal("Save", mapped.Children[0].Text);
        Assert.Equal("true", mapped.Children[0].Attributes["aria-disabled"]);
        Assert.Equal("input", mapped.Children[1].TagName);
        Assert.Equal("ordinary input", mapped.Children[1].Text);
        Assert.Equal("Display name", mapped.Children[1].Attributes["placeholder"]);
        Assert.Equal("select", mapped.Children[2].TagName);
        Assert.Equal("Enterprise", mapped.Children[2].Text);
        Assert.Equal("2", mapped.Children[2].Attributes["data-selected-index"]);
    }

    [Fact]
    public void TreeMapper_AppliesWinUiTextAndInputPrivacyLevels()
    {
        var root = new FakePanel { Name = "root", Width = 500, Height = 300 };
        root.Children.Add(new FakeWinUITextBox { Text = "ordinary input", ActualWidth = 160, ActualHeight = 32 });
        root.Children.Add(new FakeWinUITextBox { Name = "EmailBox", Text = "customer@example.com", ActualWidth = 160, ActualHeight = 32 });
        root.Children.Add(new FakeWinUIPasswordBox { Text = "replay-secret", ActualWidth = 160, ActualHeight = 32 });
        var comboBox = new FakeWinUIComboBox { SelectedItem = "Enterprise", SelectedIndex = 2, ActualWidth = 120, ActualHeight = 32 };
        comboBox.Children.Add(new FakeLabel { Text = "Enterprise", ActualWidth = 100, ActualHeight = 20 });
        root.Children.Add(comboBox);
        root.Children.Add(new FakeWinUIToggleSwitch { IsOn = true, ActualWidth = 80, ActualHeight = 32 });
        root.Children.Add(new FakeLabel { Text = "Public label", ActualWidth = 100, ActualHeight = 20 });

        var sensitiveOnly = SessionReplayTreeMapper.Map(
            root,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs });

        Assert.NotNull(sensitiveOnly);
        Assert.Equal("ordinary input", sensitiveOnly!.Children[0].Text);
        Assert.Equal("********", sensitiveOnly.Children[1].Text);
        Assert.Equal("********", sensitiveOnly.Children[2].Text);
        Assert.Equal("Enterprise", sensitiveOnly.Children[3].Text);
        Assert.Equal("2", sensitiveOnly.Children[3].Attributes["data-selected-index"]);
        Assert.Equal("true", sensitiveOnly.Children[4].Attributes["aria-checked"]);
        Assert.Equal("Public label", sensitiveOnly.Children[5].Text);

        var allInputs = SessionReplayTreeMapper.Map(
            root,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAllInputs });

        Assert.NotNull(allInputs);
        Assert.Equal("********", allInputs!.Children[0].Text);
        Assert.Equal("********", allInputs.Children[1].Text);
        Assert.Equal("********", allInputs.Children[2].Text);
        Assert.Equal("********", allInputs.Children[3].Text);
        Assert.Equal("********", Assert.Single(allInputs.Children[3].Children).Text);
        Assert.False(allInputs.Children[3].Attributes.ContainsKey("data-selected-index"));
        Assert.False(allInputs.Children[4].Attributes.ContainsKey("aria-checked"));
        Assert.Equal("Public label", allInputs.Children[5].Text);

        var allText = SessionReplayTreeMapper.Map(
            root,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskAll });

        Assert.NotNull(allText);
        Assert.Equal("********", allText!.Children[5].Text);
        Assert.False(allText.Children[4].Attributes.ContainsKey("aria-checked"));
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

        var mapped = SessionReplayTreeMapper.Map(
            root,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig { TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs });

        Assert.NotNull(mapped);
        Assert.Equal("********", mapped!.Children[0].Text);
        Assert.Equal("42", mapped.Children[1].Attributes["aria-valuenow"]);
        Assert.Equal("0", mapped.Children[1].Attributes["aria-valuemin"]);
        Assert.Equal("100", mapped.Children[1].Attributes["aria-valuemax"]);
        Assert.Equal("true", mapped.Children[2].Attributes["aria-checked"]);
        Assert.Equal("iframe", mapped.Children[3].TagName);
        Assert.Equal("https://example.com", mapped.Children[3].Attributes["src"]);
        Assert.True(long.TryParse(
            mapped.Children[3].Attributes["data-guance-webview-slot-id"],
            out _));
        Assert.Null(mapped.Children[3].Text);
    }

    [Fact]
    public void TreeMapper_ReusesStableWebViewSlotId()
    {
        var webView = new FakeWinUIWebView
        {
            Source = "https://example.com",
            ActualWidth = 300,
            ActualHeight = 180
        };
        var firstRoot = new FakePanel { Width = 400, Height = 300 };
        firstRoot.Children.Add(webView);
        var secondRoot = new FakePanel { Width = 400, Height = 300 };
        secondRoot.Children.Add(webView);

        var first = SessionReplayTreeMapper.Map(
            firstRoot,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig());
        var second = SessionReplayTreeMapper.Map(
            secondRoot,
            new SessionReplayPrivacyOverrides(),
            new RumSessionReplayConfig());

        var firstSlot = Assert.Single(first!.Children).Attributes["data-guance-webview-slot-id"];
        var secondSlot = Assert.Single(second!.Children).Attributes["data-guance-webview-slot-id"];
        Assert.Equal(firstSlot, secondSlot);
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
                TextAndInputPrivacy = SessionReplayTextAndInputPrivacy.Allow,
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
            new GuanceConfig
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
            new GuanceConfig
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

        var body = Encoding.UTF8.GetString(ExtractAndDecompressSegment(Assert.Single(queue.Items).Body));
        Assert.Contains("\"source\":4", body, StringComparison.Ordinal);
        Assert.Contains("\"width\":800", body, StringComparison.Ordinal);
        Assert.Contains("\"height\":600", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayManager_CoalescesRapidResizeRecords()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new GuanceConfig
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

        var item = Assert.Single(queue.Items);
        var multipart = Encoding.UTF8.GetString(item.Body);
        var body = Encoding.UTF8.GetString(ExtractAndDecompressSegment(item.Body));
        Assert.Contains("name=\"records_count\"\r\n\r\n1\r\n", multipart, StringComparison.Ordinal);
        Assert.Contains("\"width\":801", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"width\":800", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionReplayManager_WritesViewportMetaBeforeFullSnapshot()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig { Enabled = true }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");
        var root = new SessionReplayNode("window", 0, 0, 800, 600) { Text = "MainWindow" };

        manager.CaptureFullSnapshot(context, root);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        var payload = ExtractAndDecompressSegment(Assert.Single(queue.Items).Body);
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var records = json.RootElement.GetProperty("records");
        Assert.Equal(new[] { 4, 10 }, records.EnumerateArray().Select(record => record.GetProperty("type").GetInt32()));
        var meta = records[0].GetProperty("data");
        Assert.Equal(800, meta.GetProperty("width").GetInt32());
        Assert.Equal(600, meta.GetProperty("height").GetInt32());
        Assert.Equal(string.Empty, meta.GetProperty("href").GetString());
        Assert.True(json.RootElement.GetProperty("has_full_snapshot").GetBoolean());
    }

    [Fact]
    public async Task SessionReplayManager_WritesViewEndAfterSnapshotRecords()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig { Enabled = true }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");
        var root = new SessionReplayNode("window", 0, 0, 800, 600) { Text = "MainWindow" };

        manager.CaptureFullSnapshot(context, root);
        manager.CaptureViewEnd(context);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        var payload = ExtractAndDecompressSegment(Assert.Single(queue.Items).Body);
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var records = json.RootElement.GetProperty("records");
        Assert.Equal(new[] { 4, 10, 7 }, records.EnumerateArray().Select(record => record.GetProperty("type").GetInt32()));
        Assert.True(records[2].GetProperty("timestamp").GetInt64() > records[1].GetProperty("timestamp").GetInt64());
    }

    [Fact]
    public async Task SessionReplayManager_WritesStyledImageWireframe()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig { Enabled = true }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");
        var root = new SessionReplayNode("window", 0, 0, 320, 240);
        var pngBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        root.Children.Add(new SessionReplayNode("img", 16, 24, 96, 64)
        {
            ImageBase64 = pngBase64,
            ImageMimeType = "png",
            BackgroundColor = "#EFF6FFFF",
            BorderColor = "#2563EBFF",
            BorderWidth = 2,
            CornerRadius = 6
        });

        manager.CaptureFullSnapshot(context, root);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        var payload = ExtractAndDecompressSegment(Assert.Single(queue.Items).Body);
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        var fullSnapshot = Assert.Single(
            json.RootElement.GetProperty("records").EnumerateArray(),
            record => record.GetProperty("type").GetInt32() == 10);
        var image = Assert.Single(
            fullSnapshot.GetProperty("data").GetProperty("wireframes").EnumerateArray(),
            wireframe => wireframe.GetProperty("type").GetString() == "image");
        Assert.Equal("png", image.GetProperty("mimeType").GetString());
        Assert.Equal(pngBase64, image.GetProperty("base64").GetString());
        Assert.False(image.GetProperty("isEmpty").GetBoolean());
        Assert.Equal("#EFF6FFFF", image.GetProperty("shapeStyle").GetProperty("backgroundColor").GetString());
        Assert.Equal(6, image.GetProperty("shapeStyle").GetProperty("cornerRadius").GetInt32());
        Assert.Equal("#2563EBFF", image.GetProperty("border").GetProperty("color").GetString());
        Assert.Equal(2, image.GetProperty("border").GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task SessionReplayManager_MergesWebViewRecordsByServerOwnedSlotId()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig { Enabled = true }
            },
            queue,
            new SessionReplayPrivacyOverrides());
        var context = new SessionReplayContext("app", "session", "view", "svc", "prod", "1.0.0", "sdk", "0.1.0");
        var root = new SessionReplayNode("window", 0, 0, 800, 600);
        root.Children.Add(new SessionReplayNode("iframe", 20, 30, 640, 480)
        {
            WebViewSlotId = "1"
        });
        using var browserRecord = JsonDocument.Parse(
            $$"""
            {
              "type": 2,
              "timestamp": {{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000}},
              "slotId": "untrusted-page-value",
              "data": { "node": { "type": 0, "id": 1, "childNodes": [] } }
            }
            """);

        manager.CaptureFullSnapshot(context, root);
        manager.CaptureWebViewRecord(context, "1", browserRecord.RootElement);
        await manager.FlushPendingRecordsAsync(CancellationToken.None);

        var payload = ExtractAndDecompressSegment(Assert.Single(queue.Items).Body);
        using var json = JsonDocument.Parse(payload);
        var records = json.RootElement.GetProperty("records");
        var nativeSnapshot = Assert.Single(
            records.EnumerateArray(),
            record => record.GetProperty("type").GetInt32() == 10);
        var webViewWireframe = Assert.Single(
            nativeSnapshot.GetProperty("data").GetProperty("wireframes").EnumerateArray(),
            wireframe => wireframe.GetProperty("type").GetString() == "webview");
        var browserSnapshot = Assert.Single(
            records.EnumerateArray(),
            record => record.GetProperty("type").GetInt32() == 2);
        var rootWireframe = Assert.Single(
            nativeSnapshot.GetProperty("data").GetProperty("wireframes").EnumerateArray(),
            wireframe => wireframe.GetProperty("type").GetString() != "webview");

        Assert.Equal("1", webViewWireframe.GetProperty("slotId").GetString());
        Assert.Equal(1, webViewWireframe.GetProperty("id").GetInt64());
        Assert.NotEqual(1, rootWireframe.GetProperty("id").GetInt64());
        Assert.True(webViewWireframe.GetProperty("isVisible").GetBoolean());
        Assert.Equal("1", browserSnapshot.GetProperty("slotId").GetString());
        Assert.True(json.RootElement.GetProperty("has_full_snapshot").GetBoolean());
    }

    [Fact]
    public async Task SessionReplayManager_KeepsErrorSampledRingBufferUntilError()
    {
        var queue = new MemoryReplayQueue();
        await using var manager = new SessionReplayManager(
            new GuanceConfig
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
        var multipart = Encoding.UTF8.GetString(item.Body);
        var body = Encoding.UTF8.GetString(ExtractAndDecompressSegment(item.Body));
        Assert.Contains("name=\"has_full_snapshot\"\r\n\r\ntrue\r\n", multipart, StringComparison.Ordinal);
        Assert.Contains("\"has_full_snapshot\":true", body, StringComparison.Ordinal);
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
        Assert.True(compressedLength > 6);
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

        public Task<BatchAppendResult> EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken)
        {
            Items.Add(new QueuedSessionReplaySegment(++nextId, contentType, body, DateTimeOffset.UtcNow, body.Length));
            return Task.FromResult(BatchAppendResult.Ready);
        }

        public Task<QueueBatch<QueuedSessionReplaySegment>?> AcquireAsync(CancellationToken cancellationToken) =>
            TestQueueLease.AcquireAsync(Items, item => item.Size, cancellationToken, maximumItems: 1);

        public Task CompleteAsync(string leaseId, CancellationToken cancellationToken) =>
            TestQueueLease.CompleteAsync(Items, leaseId, cancellationToken);

        public Task AbandonAsync(string leaseId, CancellationToken cancellationToken) =>
            TestQueueLease.AbandonAsync(Items, leaseId, cancellationToken);

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

    private sealed class FakeWinUIPasswordBox : FakePanel
    {
        public string? Text { get; init; }
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
