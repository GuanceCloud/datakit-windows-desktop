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
    public async Task RumEvents_UseOfficialWindowsCommonTagsAndProtectReservedContext()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                ServiceName = "desktop-service",
                Env = "local",
                Version = "2.3.4",
                FlushInterval = TimeSpan.FromMinutes(5)
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddGlobalContext(RumConstants.AppId, "overridden-app");
        client.AddRumGlobalContext(RumConstants.SdkName, "overridden-sdk");
        client.AddAction("Anonymous", "custom", TimeSpan.FromMilliseconds(1));
        client.SetUser("user-1", "User One", "user@example.com");
        client.AddAction("Signed In", "custom", TimeSpan.FromMilliseconds(1));
        await client.FlushAsync();

        Assert.Equal(2, rumQueue.Items.Count);
        var anonymousLine = rumQueue.Items[0].Line;
        Assert.Contains("app_id=app", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("service=desktop-service", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("env=local", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("version=2.3.4", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("sdk_name=df_windows_rum_sdk", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("session_type=user", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("is_signin=F", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("userid=", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("arch=", anonymousLine, StringComparison.Ordinal);
        Assert.Contains("locale=", anonymousLine, StringComparison.Ordinal);
        Assert.DoesNotContain("overridden-app", anonymousLine, StringComparison.Ordinal);
        Assert.DoesNotContain("overridden-sdk", anonymousLine, StringComparison.Ordinal);

        var signedInLine = rumQueue.Items[1].Line;
        Assert.Contains("is_signin=T", signedInLine, StringComparison.Ordinal);
        Assert.Contains("userid=user-1", signedInLine, StringComparison.Ordinal);
        Assert.Contains("user_name=User\\ One", signedInLine, StringComparison.Ordinal);
        Assert.Contains("user_email=user@example.com", signedInLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AndroidReplayCompatibilityMode_UsesAndroidSdkNameForRumEvents()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                Env = "local",
                FlushInterval = TimeSpan.FromMinutes(5),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    AndroidCompatibilityMode = true
                }
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddAction("Compatibility", "custom", TimeSpan.FromMilliseconds(1));
        await client.FlushAsync();

        var line = Assert.Single(rumQueue.Items).Line;
        Assert.Contains("sdk_name=df_android_rum_sdk", line, StringComparison.Ordinal);
        Assert.DoesNotContain("sdk_name=df_windows_rum_sdk", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RumConfig_RejectsUnsupportedEnvironment()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            Env = "dev"
        }.Validate());

        Assert.Contains("Env", exception.Message, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task RumEvents_MarkSessionHasReplayWhenReplaySessionIsSampled()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    SampleRate = 1.0
                }
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddAction("Save", "click", TimeSpan.FromMilliseconds(1));
        await client.FlushAsync();

        var line = Assert.Single(rumQueue.Items).Line;
        Assert.Contains("session_has_replay=true", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RumEvents_MarkSessionHasReplayFalseWhenReplayIsDisabled()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = false,
                    SampleRate = 1.0
                }
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddAction("Save", "click", TimeSpan.FromMilliseconds(1));
        await client.FlushAsync();

        var line = Assert.Single(rumQueue.Items).Line;
        Assert.Contains("session_has_replay=false", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorEvent_MarksSessionHasReplayWhenReplayIsSampledOnError()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromMinutes(5),
                SessionReplay = new RumSessionReplayConfig
                {
                    Enabled = true,
                    SampleRate = 0.0,
                    OnErrorSampleRate = 1.0
                }
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddError("stack", "message", "InvalidOperationException");
        await client.FlushAsync();

        var line = Assert.Single(rumQueue.Items).Line;
        Assert.StartsWith("error,", line, StringComparison.Ordinal);
        Assert.Contains("session_has_replay=true", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SdkRumUploadRequest_SuppressesAutomaticResourceInstrumentation()
    {
        HttpRequestMessage? captured = null;
        using var transport = new DatawayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new CapturingHttpHandler(request => captured = request)
        });

        var result = await transport.SendAsync(
            new[] { new QueuedRumEvent(1, "view,app_id=app count=1i 42\n", DateTimeOffset.UtcNow) },
            CancellationToken.None);

        Assert.False(result.RetryLater);
        Assert.NotNull(captured);
        Assert.True(captured!.Options.TryGetValue(HttpInstrumentationMarks.SuppressResourceInstrumentation, out var suppressed));
        Assert.True(suppressed);
    }

    [Fact]
    public async Task SdkSessionReplayUploadRequest_SuppressesAutomaticResourceInstrumentation()
    {
        HttpRequestMessage? captured = null;
        using var transport = new SessionReplayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new CapturingHttpHandler(request => captured = request)
        });

        var result = await transport.SendAsync(
            new QueuedSessionReplaySegment(1, "application/json", Array.Empty<byte>(), DateTimeOffset.UtcNow, 0),
            CancellationToken.None);

        Assert.False(result.RetryLater);
        Assert.NotNull(captured);
        Assert.True(captured!.Options.TryGetValue(HttpInstrumentationMarks.SuppressResourceInstrumentation, out var suppressed));
        Assert.True(suppressed);
    }

    [Fact]
    public async Task FlushAsync_WaitsForPendingQueueWrites()
    {
        var rumQueue = new DelayedRumQueue();
        await using var client = new RumClient(
            CreateTestConfig(),
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddAction("Save", "custom", TimeSpan.FromMilliseconds(1));
        var flush = client.FlushAsync();

        await Task.Delay(50);
        Assert.False(flush.IsCompleted);

        rumQueue.ReleaseWrites();
        await flush;

        Assert.Single(rumQueue.Items);
    }

    [Fact]
    public async Task FlushAsync_WaitsForAnExistingBackgroundFlush()
    {
        var rumQueue = new MemoryRumQueue();
        var transport = new BlockingRumTransport();
        await using var client = new RumClient(
            CreateTestConfig(),
            rumQueue,
            transport,
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.AddAction("Save", "custom", TimeSpan.FromMilliseconds(1));
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var flush = client.FlushAsync();
        await Task.Delay(50);
        Assert.False(flush.IsCompleted);

        transport.Release();
        await flush;
    }

    [Fact]
    public async Task WinUIInstrumentation_IgnoresLateActivationAfterWindowClosed()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            CreateTestConfig(),
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());
        var window = new FakeWinUIWindow();

        WinUIReflectionInstrumentation.Attach(client, window, viewName: null);
        window.RaiseActivated();
        window.RaiseActivated();
        window.RaiseClosed();

        var exception = Record.Exception(window.RaiseActivated);
        await client.FlushAsync();

        Assert.Null(exception);
        Assert.Single(rumQueue.Items, item => item.Line.StartsWith("view,", StringComparison.Ordinal));
    }

    private static RumConfig CreateTestConfig() => new()
    {
        DatakitUrl = "http://127.0.0.1:9529",
        RumAppId = "app",
        ServiceName = "desktop-service",
        Env = "local",
        Version = "1.0.0",
        SampleRate = 1,
        FlushInterval = TimeSpan.FromHours(1)
    };

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }

    private sealed class FakeWinUIWindow
    {
        private bool closed;

        public event EventHandler? Activated;
        public event EventHandler? Closed;
        public event EventHandler? SizeChanged;

        public string Title => closed
            ? throw new InvalidOperationException("The WinUI window is already closed.")
            : "Fake WinUI Window";

        public object? Content => closed
            ? throw new InvalidOperationException("The WinUI window is already closed.")
            : null;

        public void RaiseActivated() => Activated?.Invoke(this, EventArgs.Empty);

        public void RaiseSizeChanged() => SizeChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseClosed()
        {
            closed = true;
            Closed?.Invoke(this, EventArgs.Empty);
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

    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage> capture;

        public CapturingHttpHandler(Action<HttpRequestMessage> capture)
        {
            this.capture = capture;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
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

    private sealed class DelayedRumQueue : IRumQueue
    {
        private readonly TaskCompletionSource releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long nextId;

        public List<QueuedRumEvent> Items { get; } = new();

        public async Task EnqueueAsync(string line, CancellationToken cancellationToken)
        {
            await releaseWrites.Task.WaitAsync(cancellationToken);
            Items.Add(new QueuedRumEvent(++nextId, line, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<QueuedRumEvent>> PeekAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueuedRumEvent>>(Items.Take(count).ToArray());

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
        {
            Items.RemoveAll(item => ids.Contains(item.Id));
            return Task.CompletedTask;
        }

        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void ReleaseWrites() => releaseWrites.TrySetResult();
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

    private sealed class BlockingRumTransport : IDatawayTransport
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return SendResult.Success(200);
        }

        public void Release() => release.TrySetResult();

        public void Dispose()
        {
            release.TrySetResult();
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
