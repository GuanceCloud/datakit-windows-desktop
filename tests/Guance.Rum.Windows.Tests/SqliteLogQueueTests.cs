using Guance.Rum.Windows.Queue;
using System.IO;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class SqliteLogQueueTests
{
    [Fact]
    public async Task Queue_DiscardNew_PreservesExistingFifoItems()
    {
        await using var queue = CreateQueue(RumLogDiscardStrategy.DiscardNew);

        Assert.True(await queue.EnqueueAsync("first\n", CancellationToken.None));
        Assert.True(await queue.EnqueueAsync("second\n", CancellationToken.None));
        Assert.False(await queue.EnqueueAsync("third\n", CancellationToken.None));

        var items = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal(new[] { "first\n", "second\n" }, items.Select(item => item.Line));
    }

    [Fact]
    public async Task Queue_DiscardOldest_MakesRoomForNewestItem()
    {
        await using var queue = CreateQueue(RumLogDiscardStrategy.DiscardOldest);

        Assert.True(await queue.EnqueueAsync("first\n", CancellationToken.None));
        Assert.True(await queue.EnqueueAsync("second\n", CancellationToken.None));
        Assert.True(await queue.EnqueueAsync("third\n", CancellationToken.None));

        var items = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal(new[] { "second\n", "third\n" }, items.Select(item => item.Line));
    }

    private static SqliteLogQueue CreateQueue(RumLogDiscardStrategy strategy)
    {
        return new SqliteLogQueue(new RumConfig
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "guance-rum-log-queue-tests", Guid.NewGuid().ToString("N")),
            Logging = new RumLogConfig
            {
                MaxQueueItems = 2,
                MaxQueueBytes = 1_024,
                DiscardStrategy = strategy
            }
        });
    }
}
