namespace Guance.Windows.Queue;

internal sealed class MemoryRumQueue : IRumQueue
{
    private readonly object gate = new();
    private readonly Queue<QueuedRumEvent> queue = new();
    private long nextId;
    private QueueBatch<QueuedRumEvent>? activeLease;

    public Task<BatchAppendResult> EnqueueAsync(string line, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            queue.Enqueue(new QueuedRumEvent(++nextId, line, DateTimeOffset.UtcNow));
        }

        return Task.FromResult(BatchAppendResult.Ready);
    }

    public Task<bool> SealAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<QueueBatch<QueuedRumEvent>?> AcquireAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            activeLease ??= queue.Count == 0
                ? null
                : new QueueBatch<QueuedRumEvent>(
                    Guid.NewGuid().ToString("N"),
                    queue.ToArray(),
                    queue.Sum(item => (long)item.Line.Length),
                    DateTimeOffset.UtcNow);
            return Task.FromResult(activeLease);
        }
    }

    // Test and diagnostic convenience: production upload code uses leases.
    internal Task<IReadOnlyList<QueuedRumEvent>> PeekAsync(int count, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<QueuedRumEvent>>(queue.Take(count).ToArray());
        }
    }

    public Task CompleteAsync(string leaseId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (activeLease?.LeaseId == leaseId)
            {
                for (var index = 0; index < activeLease.Items.Count; index++) queue.Dequeue();
                activeLease = null;
            }
        }

        return Task.CompletedTask;
    }

    public Task AbandonAsync(string leaseId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (activeLease?.LeaseId == leaseId) activeLease = null;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
