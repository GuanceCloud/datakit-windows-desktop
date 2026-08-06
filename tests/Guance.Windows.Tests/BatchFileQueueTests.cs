using System.IO;
using Guance.Windows.Queue;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class BatchFileQueueTests
{
    [Fact]
    public async Task RumQueue_LeasesWholeBatchesInFifoOrder()
    {
        await using var queue = new BatchFileRumQueue(CreateConfig(maxFiles: 4, maxBatchItems: 1));

        Assert.True((await queue.EnqueueAsync("line-1\n", CancellationToken.None)).Accepted);
        Assert.True((await queue.EnqueueAsync("line-2\n", CancellationToken.None)).Accepted);

        var first = Assert.IsType<QueueBatch<QueuedRumEvent>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("line-1\n", Assert.Single(first.Items).Line);
        await queue.AbandonAsync(first.LeaseId, CancellationToken.None);

        var retried = Assert.IsType<QueueBatch<QueuedRumEvent>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("line-1\n", Assert.Single(retried.Items).Line);
        await queue.CompleteAsync(retried.LeaseId, CancellationToken.None);

        var second = Assert.IsType<QueueBatch<QueuedRumEvent>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("line-2\n", Assert.Single(second.Items).Line);
    }

    [Fact]
    public async Task RumQueue_DiscardOldestKeepsCacheWithinFileLimit()
    {
        await using var queue = new BatchFileRumQueue(CreateConfig(maxFiles: 2, maxBatchItems: 1));

        await queue.EnqueueAsync("line-1\n", CancellationToken.None);
        await queue.EnqueueAsync("line-2\n", CancellationToken.None);
        await queue.EnqueueAsync("line-3\n", CancellationToken.None);

        var first = Assert.IsType<QueueBatch<QueuedRumEvent>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("line-2\n", Assert.Single(first.Items).Line);
        await queue.CompleteAsync(first.LeaseId, CancellationToken.None);
        var second = Assert.IsType<QueueBatch<QueuedRumEvent>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("line-3\n", Assert.Single(second.Items).Line);
    }

    [Fact]
    public async Task LogQueue_DiscardNewPreservesExistingBatches()
    {
        var config = CreateConfig(maxFiles: 2, maxBatchItems: 1, LogDiscardStrategy.DiscardNew);
        await using var queue = new BatchFileLogQueue(config);

        Assert.True((await queue.EnqueueAsync("first\n", CancellationToken.None)).Accepted);
        Assert.True((await queue.EnqueueAsync("second\n", CancellationToken.None)).Accepted);
        Assert.False((await queue.EnqueueAsync("third\n", CancellationToken.None)).Accepted);

        var first = Assert.IsType<QueueBatch<QueuedLogEvent>>(await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("first\n", Assert.Single(first.Items).Line);
    }

    [Fact]
    public async Task RumQueue_RecoversAnInterruptedSendingLeaseOnStartup()
    {
        var config = CreateConfig(maxFiles: 4, maxBatchItems: 1);
        await using (var firstQueue = new BatchFileRumQueue(config))
        {
            await firstQueue.EnqueueAsync("retry-me\n", CancellationToken.None);
            Assert.NotNull(await firstQueue.AcquireAsync(CancellationToken.None));
        }

        await using var recoveredQueue = new BatchFileRumQueue(config);
        var recovered = Assert.IsType<QueueBatch<QueuedRumEvent>>(
            await recoveredQueue.AcquireAsync(CancellationToken.None));
        Assert.Equal("retry-me\n", Assert.Single(recovered.Items).Line);
    }

    [Fact]
    public async Task RumQueue_RepairsCompleteLinesFromAnInterruptedActiveFile()
    {
        var config = CreateConfig(maxFiles: 4, maxBatchItems: 10);
        var activeDirectory = Path.Combine(config.CacheDirectory!, "v1", "rum", "active");
        Directory.CreateDirectory(activeDirectory);
        var path = Path.Combine(activeDirectory, "interrupted.tmp");
        var payload = System.Text.Encoding.UTF8.GetBytes("complete\npartial");
        var header = BatchFileFormat.CreateHeader(
            BatchStreamKind.Rum,
            DateTimeOffset.UtcNow,
            0,
            "text/plain; charset=utf-8",
            0,
            0);
        File.WriteAllBytes(path, header.Concat(payload).ToArray());

        await using var queue = new BatchFileRumQueue(config);
        var recovered = Assert.IsType<QueueBatch<QueuedRumEvent>>(
            await queue.AcquireAsync(CancellationToken.None));
        Assert.Equal("complete\n", Assert.Single(recovered.Items).Line);
    }

    [Fact]
    public async Task RumQueue_DiscardsABatchWithAChecksumMismatch()
    {
        var config = CreateConfig(maxFiles: 4, maxBatchItems: 1);
        await using var queue = new BatchFileRumQueue(config);
        await queue.EnqueueAsync("corrupt-me\n", CancellationToken.None);
        var batchPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(config.CacheDirectory!, "v1", "rum", "ready"),
            "*.batch"));
        var bytes = File.ReadAllBytes(batchPath);
        bytes[^1] ^= 0x7f;
        File.WriteAllBytes(batchPath, bytes);

        Assert.Null(await queue.AcquireAsync(CancellationToken.None));
        Assert.False(File.Exists(batchPath));
    }

    [Fact]
    public async Task SharedQuota_CountsFilesAcrossTelemetryStreams()
    {
        var config = CreateConfig(maxFiles: 2, maxBatchItems: 1);
        await using var rumQueue = new BatchFileRumQueue(config);
        await using var logQueue = new BatchFileLogQueue(config);

        Assert.True((await rumQueue.EnqueueAsync("rum-1\n", CancellationToken.None)).Accepted);
        Assert.True((await logQueue.EnqueueAsync("log-1\n", CancellationToken.None)).Accepted);
        Assert.True((await rumQueue.EnqueueAsync("rum-2\n", CancellationToken.None)).Accepted);

        var usage = rumQueue.GetUsageSnapshot();
        Assert.Equal(2, usage.FileCount);
        Assert.True(usage.AllocatedBytes <= config.Cache.MaxDiskBytes);
    }

    private static GuanceConfig CreateConfig(
        int maxFiles,
        int maxBatchItems,
        LogDiscardStrategy discardStrategy = LogDiscardStrategy.DiscardOldest)
    {
        return new GuanceConfig
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "guance-batch-queue-tests", Guid.NewGuid().ToString("N")),
            Cache = new CacheOptions
            {
                MaxDiskBytes = 4L * 1024 * 1024,
                MaxFiles = maxFiles,
                MaxBatchItems = maxBatchItems,
                MaxBatchBytes = 1_024
            },
            Logging = new LogConfig { DiscardStrategy = discardStrategy }
        };
    }
}
