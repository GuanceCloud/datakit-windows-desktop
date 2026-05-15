using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.SessionReplay;
using Guance.Rum.Windows.Transport;
using System.Reflection;
using System.Net;
using System.Net.Http;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class RumClientDiagnosticsTests
{
    [Fact]
    public async Task ResourceEvents_RedactQueryAndCarryNetworkCorrelationFields()
    {
        var rumQueue = new MemoryRumQueue();
        var diagnostics = new List<RumDiagnosticEvent>();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                DiagnosticListener = diagnostics.Add,
                Privacy = new RumPrivacyConfig
                {
                    RedactedQueryParameterNames = new[] { "token" },
                    RedactedValue = "redacted"
                }
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        var resourceId = client.StartResource("https://api.example.com/v1/items?token=secret&keep=1", "GET");
        client.StopResource(
            resourceId,
            200,
            properties: new Dictionary<string, object?>
            {
                [RumConstants.TraceId] = "trace-1",
                [RumConstants.SpanId] = "span-1",
                [RumConstants.ResourceHttpProtocol] = "HTTP/2",
                [RumConstants.ResourceTtfb] = 123L,
                [RumConstants.NetworkInstrumentation] = "unit-test",
                [RumConstants.ResourceTimingPrecision] = "manual",
                [RumConstants.ResourceTimingPhase] = "dns_tcp_ssl_ttfb",
                [RumConstants.ResourceTimingDuration] = 456L,
                [RumConstants.ResourceTtfbEstimated] = false
            });
        await client.FlushAsync();

        var line = Assert.Single(rumQueue.Items).Line;
        Assert.Contains("resource_url=https://api.example.com/v1/items?token\\=redacted&keep\\=1", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
        Assert.Contains("trace_id=trace-1", line, StringComparison.Ordinal);
        Assert.Contains("span_id=span-1", line, StringComparison.Ordinal);
        Assert.Contains("resource_http_protocol=HTTP/2", line, StringComparison.Ordinal);
        Assert.Contains("resource_ttfb=123i", line, StringComparison.Ordinal);
        Assert.Contains("network_instrumentation=\"unit-test\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_precision=\"manual\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_phase=\"dns_tcp_ssl_ttfb\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_duration=456i", line, StringComparison.Ordinal);
        Assert.Contains("resource_ttfb_estimated=false", line, StringComparison.Ordinal);

        var snapshot = client.GetDiagnosticsSnapshot();
        Assert.True(snapshot.RumEventsEnqueued >= 1);
        Assert.True(snapshot.RumUploadRetryCount >= 1);
        Assert.Equal(500, snapshot.LastRumUploadStatusCode);
        Assert.Equal("hold queue for assertions", snapshot.LastRumUploadError);
        Assert.Contains(diagnostics, item => item.Source == "rum_upload" && item.Level == RumDiagnosticLevel.Warning);
    }

    [Fact]
    public async Task UiThreadBlockMonitor_CoalescesLongTaskReportsDuringCooldown()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5)
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());
        using var monitor = new UiThreadBlockMonitor(
            client,
            new InlineSynchronizationContext(),
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero,
            TimeSpan.FromHours(1));
        var probe = typeof(UiThreadBlockMonitor).GetMethod("Probe", BindingFlags.Instance | BindingFlags.NonPublic)!;

        probe.Invoke(monitor, null);
        probe.Invoke(monitor, null);
        await client.FlushAsync();

        var longTaskLines = rumQueue.Items
            .Select(item => item.Line)
            .Where(line => line.StartsWith("long_task,", StringComparison.Ordinal))
            .ToArray();
        var line = Assert.Single(longTaskLines);
        Assert.Contains("long_task_source=\"ui_thread_block_monitor\"", line, StringComparison.Ordinal);
        Assert.Contains("long_task_suppressed_count=0i", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopResource_WithManualTimingMapsPhaseDurations()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5)
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        var resourceId = client.StartResource("https://api.example.com/v1/items", "POST");
        client.StopResource(
            resourceId,
            201,
            RumResourceTiming.FromPhases(
                dns: TimeSpan.FromMilliseconds(1),
                tcp: TimeSpan.FromMilliseconds(2),
                ssl: TimeSpan.FromMilliseconds(3),
                ttfb: TimeSpan.FromMilliseconds(4),
                totalDuration: TimeSpan.FromMilliseconds(12),
                source: "unit-phase-clock"));
        await client.FlushAsync();

        var line = Assert.Single(rumQueue.Items).Line;
        Assert.Contains("resource_dns=1000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_tcp=2000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_ssl=3000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_ttfb=4000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_duration=12000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_source=\"unit-phase-clock\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_precision=\"phase\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_phase=\"dns_tcp_ssl_ttfb\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_ttfb_estimated=false", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpMessageHandler_UsesConfiguredResourceTimingProvider()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                HttpResourceTimingProvider = (_, response, elapsed, exception) =>
                {
                    Assert.NotNull(response);
                    Assert.Null(exception);
                    Assert.True(elapsed >= TimeSpan.Zero);
                    return RumResourceTiming.FromPhases(
                        dns: TimeSpan.FromMilliseconds(5),
                        tcp: TimeSpan.FromMilliseconds(6),
                        ssl: TimeSpan.FromMilliseconds(7),
                        ttfb: TimeSpan.FromMilliseconds(8),
                        totalDuration: TimeSpan.FromMilliseconds(30),
                        source: "provider-test");
                }
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());
        using var http = new HttpClient(new RumHttpMessageHandler(client, new StaticHttpHandler()));

        using var response = await http.GetAsync("https://api.example.com/provider");
        await client.FlushAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(rumQueue.Items).Line;
        Assert.Contains("resource_dns=5000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_tcp=6000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_ssl=7000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_ttfb=8000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_duration=30000000i", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_source=\"provider-test\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpMessageHandler_FallsBackWhenResourceTimingProviderFails()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                HttpResourceTimingProvider = (_, _, _, _) => throw new InvalidOperationException("provider failed")
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());
        using var http = new HttpClient(new RumHttpMessageHandler(client, new StaticHttpHandler()));

        using var response = await http.GetAsync("https://api.example.com/fallback");
        await client.FlushAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(rumQueue.Items).Line;
        Assert.Contains("resource_timing_source=\"RumHttpMessageHandler\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_timing_precision=\"total_elapsed_fallback\"", line, StringComparison.Ordinal);
        Assert.Contains("resource_ttfb_estimated=true", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongTaskAndCrashProperties_AreIncludedAsFields()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5)
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddLongTask(
            TimeSpan.FromMilliseconds(250),
            "delay",
            new Dictionary<string, object?>
            {
                [RumConstants.LongTaskSource] = "unit",
                [RumConstants.LongTaskDelay] = 250_000_000L,
                [RumConstants.LongTaskThreshold] = 100_000_000L
            });
        client.AddError(
            "stack",
            "message",
            "InvalidOperationException",
            "crash",
            new Dictionary<string, object?>
            {
                [RumConstants.IsCrash] = true,
                [RumConstants.CrashSource] = "unit"
            });
        await client.FlushAsync();

        Assert.Contains(rumQueue.Items, item =>
            item.Line.StartsWith("long_task,", StringComparison.Ordinal) &&
            item.Line.Contains("long_task_source=\"unit\"", StringComparison.Ordinal) &&
            item.Line.Contains("long_task_delay=250000000i", StringComparison.Ordinal));
        Assert.Contains(rumQueue.Items, item =>
            item.Line.StartsWith("error,", StringComparison.Ordinal) &&
            item.Line.Contains("is_crash=true", StringComparison.Ordinal) &&
            item.Line.Contains("crash_source=\"unit\"", StringComparison.Ordinal));
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }

    private sealed class StaticHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }
    }

    private sealed class MemoryRumQueue : IRumQueue
    {
        private readonly object gate = new();
        private long nextId;

        public List<QueuedRumEvent> Items { get; } = new();

        public Task EnqueueAsync(string line, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Items.Add(new QueuedRumEvent(++nextId, line, DateTimeOffset.UtcNow));
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QueuedRumEvent>> PeekAsync(int count, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<QueuedRumEvent>>(Items.Take(count).ToArray());
            }
        }

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                Items.RemoveAll(item => ids.Contains(item.Id));
            }

            return Task.CompletedTask;
        }

        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryReplayQueue : ISessionReplayQueue
    {
        public Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QueuedSessionReplaySegment>>(Array.Empty<QueuedSessionReplaySegment>());
        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RetryRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken)
        {
            return Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class RetryReplayTransport : ISessionReplayTransport
    {
        public Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken)
        {
            return Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));
        }

        public void Dispose()
        {
        }
    }
}
