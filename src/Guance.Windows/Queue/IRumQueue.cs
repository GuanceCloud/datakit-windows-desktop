namespace Guance.Windows.Queue;

internal interface IRumQueue : IAsyncDisposable
{
    Task<BatchAppendResult> EnqueueAsync(string line, CancellationToken cancellationToken);
    Task<bool> SealAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken) => Task.FromResult(false);
    Task<QueueBatch<QueuedRumEvent>?> AcquireAsync(CancellationToken cancellationToken) =>
        Task.FromResult<QueueBatch<QueuedRumEvent>?>(null);
    Task CompleteAsync(string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
    Task AbandonAsync(string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
    CacheUsageSnapshot GetUsageSnapshot() => default;
}
