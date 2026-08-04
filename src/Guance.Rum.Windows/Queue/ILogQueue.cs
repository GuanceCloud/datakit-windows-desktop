namespace Guance.Rum.Windows.Queue;

internal interface ILogQueue : IAsyncDisposable
{
    Task<bool> EnqueueAsync(string line, CancellationToken cancellationToken);
    Task<IReadOnlyList<QueuedLogEvent>> PeekAsync(int count, CancellationToken cancellationToken);
    Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
}
