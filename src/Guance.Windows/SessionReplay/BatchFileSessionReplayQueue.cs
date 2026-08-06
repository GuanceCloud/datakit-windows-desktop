using Guance.Windows.Queue;

namespace Guance.Windows.SessionReplay;

internal sealed class BatchFileSessionReplayQueue : ISessionReplayQueue
{
    private readonly BatchFileStore store;

    public BatchFileSessionReplayQueue(GuanceConfig config)
    {
        store = new BatchFileStore(
            config,
            BatchStreamKind.SessionReplay,
            "application/octet-stream",
            lineDelimited: false,
            CacheAdmissionPolicy.DiscardOldest);
    }

    public Task<BatchAppendResult> EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken) =>
        store.WriteBatchAsync(contentType, body, recordCount: 1, cancellationToken);

    public Task<bool> SealAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken) => Task.FromResult(false);

    public async Task<QueueBatch<QueuedSessionReplaySegment>?> AcquireAsync(CancellationToken cancellationToken)
    {
        var lease = await store.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null) return null;
        var segment = new QueuedSessionReplaySegment(
            1,
            lease.ContentType,
            lease.Payload,
            lease.CreatedAt,
            lease.Payload.LongLength);
        return new QueueBatch<QueuedSessionReplaySegment>(
            lease.LeaseId,
            new[] { segment },
            lease.Payload.LongLength,
            lease.CreatedAt);
    }

    public Task CompleteAsync(string leaseId, CancellationToken cancellationToken) =>
        store.CompleteAsync(leaseId, cancellationToken);

    public Task AbandonAsync(string leaseId, CancellationToken cancellationToken) =>
        store.AbandonAsync(leaseId, cancellationToken);

    public ValueTask DisposeAsync() => store.DisposeAsync();
}
