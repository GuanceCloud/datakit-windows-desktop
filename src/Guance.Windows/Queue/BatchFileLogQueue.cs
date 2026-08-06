namespace Guance.Windows.Queue;

internal sealed class BatchFileLogQueue : ILogQueue
{
    private readonly BatchFileStore store;

    public BatchFileLogQueue(GuanceConfig config)
    {
        store = new BatchFileStore(
            config,
            BatchStreamKind.Log,
            "text/plain; charset=utf-8",
            lineDelimited: true,
            config.Logging.DiscardStrategy == LogDiscardStrategy.DiscardNew
                ? CacheAdmissionPolicy.DiscardNew
                : CacheAdmissionPolicy.DiscardOldest);
    }

    public Task<BatchAppendResult> EnqueueAsync(string line, CancellationToken cancellationToken) =>
        store.AppendLineAsync(line, cancellationToken);

    public Task<bool> SealAsync(CancellationToken cancellationToken) => store.SealAsync(cancellationToken);

    public Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken) =>
        store.SealIfOlderAsync(maximumAge, cancellationToken);

    public async Task<QueueBatch<QueuedLogEvent>?> AcquireAsync(CancellationToken cancellationToken)
    {
        var lease = await store.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null) return null;
        return new QueueBatch<QueuedLogEvent>(
            lease.LeaseId,
            BatchFileRumQueue.DecodeLines(
                lease.Payload,
                lease.CreatedAt,
                static (id, line, createdAt) => new QueuedLogEvent(id, line, createdAt)),
            lease.Payload.LongLength,
            lease.CreatedAt);
    }

    public Task CompleteAsync(string leaseId, CancellationToken cancellationToken) =>
        store.CompleteAsync(leaseId, cancellationToken);

    public Task AbandonAsync(string leaseId, CancellationToken cancellationToken) =>
        store.AbandonAsync(leaseId, cancellationToken);

    public ValueTask DisposeAsync() => store.DisposeAsync();
}
