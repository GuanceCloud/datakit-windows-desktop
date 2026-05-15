namespace Guance.Rum.Windows.Queue;

internal sealed class MemoryRumQueue : IRumQueue
{
    private readonly object gate = new();
    private readonly Queue<QueuedRumEvent> queue = new();
    private long nextId;

    public Task EnqueueAsync(string line, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            queue.Enqueue(new QueuedRumEvent(++nextId, line, DateTimeOffset.UtcNow));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueuedRumEvent>> PeekAsync(int count, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<QueuedRumEvent>>(queue.Take(count).ToArray());
        }
    }

    public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var delete = ids.ToHashSet();
            var keep = queue.Where(item => !delete.Contains(item.Id)).ToArray();
            queue.Clear();
            foreach (var item in keep)
            {
                queue.Enqueue(item);
            }
        }

        return Task.CompletedTask;
    }

    public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
