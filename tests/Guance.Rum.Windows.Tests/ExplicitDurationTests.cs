using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.SessionReplay;
using Guance.Rum.Windows.Transport;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class ExplicitDurationTests
{
    [Fact]
    public async Task ExplicitDurations_AreNonNegativeAndActionTimestampRepresentsStart()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromHours(1)
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        var beforeAction = Clock.UnixTimeNanoseconds();
        client.AddAction("Earlier", "custom", TimeSpan.FromSeconds(2));
        client.AddAction("Negative", "custom", TimeSpan.FromSeconds(-1));
        client.AddLongTask(TimeSpan.FromSeconds(-1), "negative");
        var resourceId = client.StartResource("https://example.com", "GET");
        client.StopResource(
            resourceId,
            200,
            RumResourceTiming.FromPhases(
                dns: TimeSpan.FromMilliseconds(-1),
                totalDuration: TimeSpan.FromMilliseconds(-2)));
        await client.FlushAsync();

        var items = await rumQueue.PeekAsync(10, CancellationToken.None);
        var earlierAction = Assert.Single(items, item =>
            item.Line.StartsWith("action,", StringComparison.Ordinal) &&
            item.Line.Contains("action_name=Earlier", StringComparison.Ordinal)).Line;
        var earlierTimestamp = long.Parse(earlierAction.AsSpan(earlierAction.LastIndexOf(' ') + 1).Trim());
        Assert.True(earlierTimestamp <= beforeAction - 1_500_000_000L);

        Assert.Contains(items, item =>
            item.Line.StartsWith("action,", StringComparison.Ordinal) &&
            item.Line.Contains("action_name=Negative", StringComparison.Ordinal) &&
            item.Line.Contains("duration=0i", StringComparison.Ordinal));
        Assert.Contains(items, item =>
            item.Line.StartsWith("long_task,", StringComparison.Ordinal) &&
            item.Line.Contains("duration=0i", StringComparison.Ordinal));
        Assert.Contains(items, item =>
            item.Line.StartsWith("resource,", StringComparison.Ordinal) &&
            item.Line.Contains("resource_dns=0i", StringComparison.Ordinal) &&
            item.Line.Contains("resource_timing_duration=0i", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutomaticLaunch_EnqueuesColdActionWithAndroidCompatibleFields()
    {
        var rumQueue = new MemoryRumQueue();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromHours(1)
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport());

        client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
        {
            EnableWpf = false,
            EnableWinForms = false,
            EnableWinUI = false,
            EnableWebView = false,
            EnableHttpClient = false,
            EnableUnhandledException = false,
            EnableUiThreadBlock = false,
            EnableAppLaunch = true
        });
        client.MarkApplicationWindowCreated();
        client.NotifyApplicationFrameRendered();
        await client.FlushAsync();

        var items = await rumQueue.PeekAsync(10, CancellationToken.None);
        var launch = Assert.Single(items, item =>
            item.Line.StartsWith("action,", StringComparison.Ordinal) &&
            item.Line.Contains("action_type=launch_cold", StringComparison.Ordinal)).Line;
        Assert.Contains("action_name=app\\ cold\\ start", launch, StringComparison.Ordinal);
        Assert.Contains("app_pre_application_init_time=", launch, StringComparison.Ordinal);
        Assert.Contains("app_application_init_time=", launch, StringComparison.Ordinal);
        Assert.Contains("app_first_frame_init_time=", launch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticLaunch_EnqueuesHotActionAfterLongBackground()
    {
        var rumQueue = new MemoryRumQueue();
        var launchClock = new FakeApplicationLaunchClock();
        await using var client = new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromHours(1)
            },
            rumQueue,
            new RetryRumTransport(),
            new MemoryReplayQueue(),
            new RetryReplayTransport(),
            launchClock);

        client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
        {
            EnableWpf = false,
            EnableWinForms = false,
            EnableWinUI = false,
            EnableWebView = false,
            EnableHttpClient = false,
            EnableUnhandledException = false,
            EnableUiThreadBlock = false,
            EnableAppLaunch = true
        });
        client.MarkApplicationWindowCreated();
        client.NotifyApplicationFrameRendered();
        client.NotifyApplicationBackgrounded();
        launchClock.Advance(TimeSpan.FromSeconds(10));
        client.NotifyApplicationForegrounding();
        launchClock.Advance(TimeSpan.FromMilliseconds(50));
        client.NotifyApplicationFrameRendered();
        await client.FlushAsync();

        var items = await rumQueue.PeekAsync(10, CancellationToken.None);
        var launch = Assert.Single(items, item =>
            item.Line.StartsWith("action,", StringComparison.Ordinal) &&
            item.Line.Contains("action_type=launch_hot", StringComparison.Ordinal)).Line;
        Assert.Contains("action_name=app\\ hot\\ start", launch, StringComparison.Ordinal);
    }

    private sealed class MemoryReplayQueue : ISessionReplayQueue
    {
        public Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueuedSessionReplaySegment>>(Array.Empty<QueuedSessionReplaySegment>());
        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RetryRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));
        public void Dispose() { }
    }

    private sealed class RetryReplayTransport : ISessionReplayTransport
    {
        public Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));
        public void Dispose() { }
    }

    private sealed class FakeApplicationLaunchClock : IApplicationLaunchClock
    {
        private long unixNanoseconds = 200;
        private long monotonicNanoseconds = 20;

        public long ProcessStartUnixNanoseconds => 100;

        public ApplicationLaunchMoment Now() => new(unixNanoseconds, monotonicNanoseconds);

        public long ElapsedNanoseconds(long startTimestamp, long endTimestamp) =>
            Math.Max(0, endTimestamp - startTimestamp);

        public void Advance(TimeSpan duration)
        {
            var nanoseconds = duration.Ticks * 100;
            unixNanoseconds += nanoseconds;
            monotonicNanoseconds += nanoseconds;
        }
    }
}
