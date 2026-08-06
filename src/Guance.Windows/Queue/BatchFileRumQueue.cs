using System.Text;

namespace Guance.Windows.Queue;

internal sealed class BatchFileRumQueue : IRumQueue
{
    private readonly BatchFileStore store;

    public BatchFileRumQueue(GuanceConfig config)
    {
        store = new BatchFileStore(
            config,
            BatchStreamKind.Rum,
            "text/plain; charset=utf-8",
            lineDelimited: true,
            CacheAdmissionPolicy.DiscardOldest);
    }

    public Task<BatchAppendResult> EnqueueAsync(string line, CancellationToken cancellationToken) =>
        store.AppendLineAsync(line, cancellationToken);

    public Task<bool> SealAsync(CancellationToken cancellationToken) => store.SealAsync(cancellationToken);

    public Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken) =>
        store.SealIfOlderAsync(maximumAge, cancellationToken);

    public async Task<QueueBatch<QueuedRumEvent>?> AcquireAsync(CancellationToken cancellationToken)
    {
        var lease = await store.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null) return null;
        return new QueueBatch<QueuedRumEvent>(
            lease.LeaseId,
            DecodeLines(lease.Payload, lease.CreatedAt, static (id, line, createdAt) => new QueuedRumEvent(id, line, createdAt)),
            lease.Payload.LongLength,
            lease.CreatedAt);
    }

    public Task CompleteAsync(string leaseId, CancellationToken cancellationToken) =>
        store.CompleteAsync(leaseId, cancellationToken);

    public Task AbandonAsync(string leaseId, CancellationToken cancellationToken) =>
        store.AbandonAsync(leaseId, cancellationToken);

    public CacheUsageSnapshot GetUsageSnapshot() => store.GetUsageSnapshot();

    public ValueTask DisposeAsync() => store.DisposeAsync();

    internal static IReadOnlyList<T> DecodeLines<T>(
        byte[] payload,
        DateTimeOffset createdAt,
        Func<long, string, DateTimeOffset, T> factory)
    {
        var text = Encoding.UTF8.GetString(payload);
        var lines = text.Split('\n');
        var result = new List<T>(Math.Max(0, lines.Length - 1));
        long id = 0;
        for (var index = 0; index < lines.Length - 1; index++)
        {
            result.Add(factory(++id, lines[index] + "\n", createdAt));
        }
        return result;
    }
}
