using System.IO;
using Guance.Rum.Windows.Queue;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class SqliteRumQueueTests
{
    [Fact]
    public async Task Queue_PeeksInFifoOrderAndDeletesById()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "guance-rum-tests", Guid.NewGuid().ToString("N"));
        await using var queue = new SqliteRumQueue(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            CacheDirectory = tempDir
        });

        await queue.EnqueueAsync("line-1\n", CancellationToken.None);
        await queue.EnqueueAsync("line-2\n", CancellationToken.None);

        var firstBatch = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal(new[] { "line-1\n", "line-2\n" }, firstBatch.Select(item => item.Line).ToArray());

        await queue.DeleteAsync(new[] { firstBatch[0].Id }, CancellationToken.None);
        var secondBatch = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal("line-2\n", Assert.Single(secondBatch).Line);
    }

    [Fact]
    public async Task Queue_TrimDropsOldestWhenItemLimitIsExceeded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "guance-rum-tests", Guid.NewGuid().ToString("N"));
        await using var queue = new SqliteRumQueue(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            CacheDirectory = tempDir,
            MaxQueueItems = 2
        });

        await queue.EnqueueAsync("line-1\n", CancellationToken.None);
        await queue.EnqueueAsync("line-2\n", CancellationToken.None);
        await queue.EnqueueAsync("line-3\n", CancellationToken.None);
        await queue.TrimAsync(CancellationToken.None);

        var batch = await queue.PeekAsync(10, CancellationToken.None);
        Assert.Equal(new[] { "line-2\n", "line-3\n" }, batch.Select(item => item.Line).ToArray());
    }
}
