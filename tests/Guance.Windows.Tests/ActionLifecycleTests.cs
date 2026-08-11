using Guance.Windows.Queue;
using Guance.Windows.Transport;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class ActionLifecycleTests
{
    [Fact]
    public async Task StartAction_ProtectsFrequentCallsAndReplacesPreviousNormalAction()
    {
        var queue = new MemoryRumQueue();
        await using var client = CreateClient(
            queue,
            new ActionTrackingTiming(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200)));
        client.StartView("Checkout");

        using var first = client.StartAction("First", "click");
        using var protectedCall = client.StartAction("Protected", "click");

        Assert.True(first.IsAccepted);
        Assert.False(protectedCall.IsAccepted);

        first.Dispose();
        using var stillProtected = client.StartAction("StillProtected", "click");
        Assert.False(stillProtected.IsAccepted);

        client.AddAction("KnownDurationOne", "custom", TimeSpan.FromMilliseconds(5));
        client.AddAction("KnownDurationTwo", "custom", TimeSpan.FromMilliseconds(5));
        await Task.Delay(45);
        using var replacement = client.StartAction("Replacement", "click");
        Assert.True(replacement.IsAccepted);

        await client.ShutdownAsync();
        var items = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Contains(items, item => ContainsAction(item, "First"));
        Assert.Contains(items, item => ContainsAction(item, "Replacement"));
        Assert.Contains(items, item => ContainsAction(item, "KnownDurationOne"));
        Assert.Contains(items, item => ContainsAction(item, "KnownDurationTwo"));
        Assert.DoesNotContain(items, item => ContainsAction(item, "Protected"));
        Assert.DoesNotContain(items, item => ContainsAction(item, "StillProtected"));
    }

    [Fact]
    public async Task StartAction_NeedWaitRequiresMatchingStop()
    {
        var queue = new MemoryRumQueue();
        await using var client = CreateClient(
            queue,
            new ActionTrackingTiming(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200)));
        client.StartView("Checkout");

        using var waiting = client.StartAction("Waiting", "custom", needWait: true);
        await Task.Delay(35);

        using var blocked = client.StartAction("Blocked", "click");
        Assert.False(blocked.IsAccepted);

        waiting.Dispose();
        using var accepted = client.StartAction("Accepted", "click");
        Assert.True(accepted.IsAccepted);

        await client.ShutdownAsync();
        var items = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Contains(items, item => ContainsAction(item, "Waiting"));
        Assert.Contains(items, item => ContainsAction(item, "Accepted"));
        Assert.DoesNotContain(items, item => ContainsAction(item, "Blocked"));
    }

    [Fact]
    public async Task StartAction_ForceClosesAtMaximumDuration()
    {
        var queue = new MemoryRumQueue();
        await using var client = CreateClient(
            queue,
            new ActionTrackingTiming(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80)));
        client.StartView("Checkout");

        using var waiting = client.StartAction("TimedOut", "custom", needWait: true);
        await WaitUntilAsync(async () =>
        {
            var items = await queue.PeekAsync(10, CancellationToken.None);
            return items.Any(item => ContainsAction(item, "TimedOut"));
        });

        using var afterTimeout = client.StartAction("AfterTimeout", "click");
        Assert.True(afterTimeout.IsAccepted);
        await client.ShutdownAsync();

        var finalItems = await queue.PeekAsync(10, CancellationToken.None);
        var timedOut = Assert.Single(finalItems, item => ContainsAction(item, "TimedOut"));
        Assert.Contains("duration=", timedOut.Line, StringComparison.Ordinal);
        Assert.Contains(finalItems, item => ContainsAction(item, "AfterTimeout"));
    }

    [Fact]
    public async Task ViewChange_ClosesNeedWaitAction()
    {
        var queue = new MemoryRumQueue();
        await using var client = CreateClient(
            queue,
            new ActionTrackingTiming(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200)));
        client.StartView("FirstView");

        using var waiting = client.StartAction("WaitingForView", "custom", needWait: true);
        client.StartView("SecondView");
        using var afterViewChange = client.StartAction("AfterViewChange", "click");

        Assert.True(waiting.IsAccepted);
        Assert.True(afterViewChange.IsAccepted);
        await client.ShutdownAsync();

        var items = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Contains(items, item => ContainsAction(item, "WaitingForView"));
        Assert.Contains(items, item => ContainsAction(item, "AfterViewChange"));
    }

    private static GuanceClient CreateClient(MemoryRumQueue queue, ActionTrackingTiming timing) =>
        new(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                FlushInterval = TimeSpan.FromHours(1)
            },
            queue,
            new RetryRumTransport(),
            timing);

    private static bool ContainsAction(QueuedRumEvent item, string name) =>
        item.Line.StartsWith("action,", StringComparison.Ordinal) &&
        item.Line.Contains($"action_name={name}", StringComparison.Ordinal);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the Action lifecycle transition.");
    }

    private sealed class RetryRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "hold queue for assertions"));

        public void Dispose()
        {
        }
    }
}
