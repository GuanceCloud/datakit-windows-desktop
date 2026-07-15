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
}
