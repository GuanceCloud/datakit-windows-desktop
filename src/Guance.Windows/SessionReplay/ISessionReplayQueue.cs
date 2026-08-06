using Guance.Windows.Queue;

namespace Guance.Windows.SessionReplay;

internal interface ISessionReplayQueue : IAsyncDisposable
{
    Task<BatchAppendResult> EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken);
    Task<bool> SealAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken) => Task.FromResult(false);
    Task<QueueBatch<QueuedSessionReplaySegment>?> AcquireAsync(CancellationToken cancellationToken) =>
        Task.FromResult<QueueBatch<QueuedSessionReplaySegment>?>(null);
    Task CompleteAsync(string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
    Task AbandonAsync(string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
}
