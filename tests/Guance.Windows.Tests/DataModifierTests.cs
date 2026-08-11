using Guance.Windows.Queue;
using Guance.Windows.Transport;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class DataModifierTests
{
    [Fact]
    public async Task ResourceEvent_AppliesPrivacyThenDataAndLineModifiersBeforeQueueing()
    {
        object? valueSeenByDataModifier = null;
        object? valueSeenByLineModifier = null;
        var queue = new MemoryRumQueue();
        var config = new GuanceConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            FlushInterval = TimeSpan.FromMinutes(5),
            Privacy = new RumPrivacyConfig
            {
                RedactedHeaderNames = new[] { "Authorization" },
                RedactedQueryParameterNames = new[] { "token" },
                RedactedValue = "hidden"
            },
            DataModifier = (key, value) =>
            {
                if (key == RumConstants.ResourceUrl)
                {
                    valueSeenByDataModifier = value;
                }

                return key == "customer_secret" ? "data-modified" : null;
            },
            LineDataModifier = (measurement, data) =>
            {
                Assert.Equal(RumConstants.MeasurementResource, measurement);
                valueSeenByLineModifier = data["customer_secret"];
                return new Dictionary<string, object?>
                {
                    ["customer_secret"] = "line-modified",
                    ["unknown_key"] = "must-not-be-added",
                    [RumConstants.ResourceMethod] = null
                };
            }
        };

        await using var client = new GuanceClient(config, queue, new RetryRumTransport());
        var resourceId = client.StartResource(
            "https://api.example.test/items?token=secret&keep=1",
            "GET",
            new Dictionary<string, object?> { ["customer_secret"] = "raw" });
        client.StopResource(
            resourceId,
            200,
            requestHeader: "Authorization: bearer-secret\r\nX-Keep: visible",
            responseHeader: "Set-Cookie: session-secret");

        var line = Assert.Single(await queue.PeekAsync(10, CancellationToken.None)).Line;
        Assert.Equal("https://api.example.test/items?token=secret&keep=1", valueSeenByDataModifier);
        Assert.Equal("data-modified", valueSeenByLineModifier);
        Assert.Contains("customer_secret=\"line-modified\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_method=GET", line, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown_key", line, StringComparison.Ordinal);
        Assert.Contains("token\\=hidden&keep\\=1", line, StringComparison.Ordinal);
        Assert.Contains("Authorization: hidden", line, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ModifierPipeline_AppliesToLogEvents()
    {
        var config = new GuanceConfig
        {
            DataModifier = (key, _) => key == "message" ? "data-modified" : null,
            LineDataModifier = (measurement, data) =>
            {
                Assert.Equal(RumConstants.WindowsLogSource, measurement);
                Assert.Equal("data-modified", data["message"]);
                return new Dictionary<string, object?> { ["message"] = "line-modified" };
            }
        };
        var logEvent = new LogEvent(1).WithField("message", "raw");

        TelemetryModifierPipeline.Apply(logEvent, config);

        Assert.Equal("line-modified", logEvent.Fields["message"]);
    }

    [Fact]
    public async Task FailedResource_DoesNotCopyRawUrlIntoDerivedErrorMessage()
    {
        var queue = new MemoryRumQueue();
        var config = new GuanceConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            FlushInterval = TimeSpan.FromMinutes(5),
            Privacy = new RumPrivacyConfig
            {
                RedactedQueryParameterNames = new[] { "token" },
                RedactedValue = "hidden"
            }
        };

        await using var client = new GuanceClient(config, queue, new RetryRumTransport());
        var resourceId = client.StartResource("https://api.example.test/items?token=secret", "GET");
        client.StopResource(resourceId, 500);

        var lines = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, item => item.Line.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(lines, item =>
            item.Line.StartsWith("error,", StringComparison.Ordinal) &&
            item.Line.Contains("error_type=network_error", StringComparison.Ordinal) &&
            item.Line.Contains("error_source=network", StringComparison.Ordinal) &&
            item.Line.Contains("token=hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void ModifierPipeline_IgnoresCallbackExceptions()
    {
        var config = new GuanceConfig
        {
            DataModifier = (_, _) => throw new InvalidOperationException("data callback failed"),
            LineDataModifier = (_, _) => throw new InvalidOperationException("line callback failed")
        };
        var logEvent = new LogEvent(1).WithField("message", "original");

        TelemetryModifierPipeline.Apply(logEvent, config);

        Assert.Equal("original", logEvent.Fields["message"]);
    }

    [Fact]
    public void ModifierPipeline_WebViewUrlsRemainRawUntilFinalPrivacyLayer()
    {
        object? valueSeenByModifier = null;
        var config = new GuanceConfig
        {
            Privacy = new RumPrivacyConfig
            {
                RedactedQueryParameterNames = new[] { "token" },
                RedactedValue = "hidden"
            },
            DataModifier = (key, value) =>
            {
                if (key == "webview_url") valueSeenByModifier = value;
                return null;
            }
        };
        var rumEvent = new RumEvent(RumConstants.MeasurementView, 1)
            .WithTag("view_type", "webview")
            .WithTag(RumConstants.ViewName, "https://web.example.test/page?token=secret")
            .WithTag("webview_url", "https://web.example.test/page?token=secret")
            .WithField("document_referrer", "https://ref.example.test/?token=secret")
            .WithField("required", true);

        TelemetryModifierPipeline.Apply(rumEvent, config);

        Assert.Equal("https://web.example.test/page?token=secret", valueSeenByModifier);
        Assert.Equal("https://web.example.test/page?token=hidden", rumEvent.Tags[RumConstants.ViewName]);
        Assert.Equal("https://web.example.test/page?token=hidden", rumEvent.Tags["webview_url"]);
        Assert.Equal("https://ref.example.test/?token=hidden", rumEvent.Fields["document_referrer"]);
    }

    private sealed class RetryRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(
            IReadOnlyList<QueuedRumEvent> events,
            CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));

        public void Dispose()
        {
        }
    }
}
