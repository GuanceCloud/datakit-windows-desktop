namespace Guance.Rum.Windows.SessionReplay;

internal interface ISessionReplayQueue : IAsyncDisposable
{
    Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken);
    Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken);
    Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
    Task TrimAsync(CancellationToken cancellationToken);
}
