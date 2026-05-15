namespace Guance.Rum.Windows.Queue;

internal interface IRumQueue : IAsyncDisposable
{
    Task EnqueueAsync(string line, CancellationToken cancellationToken);
    Task<IReadOnlyList<QueuedRumEvent>> PeekAsync(int count, CancellationToken cancellationToken);
    Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
    Task TrimAsync(CancellationToken cancellationToken);
}
