namespace Guance.Windows.Queue;

internal interface ILogQueue : IAsyncDisposable
{
    Task<BatchAppendResult> EnqueueAsync(string line, CancellationToken cancellationToken);
    Task<bool> SealAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken) => Task.FromResult(false);
    Task<QueueBatch<QueuedLogEvent>?> AcquireAsync(CancellationToken cancellationToken) =>
        Task.FromResult<QueueBatch<QueuedLogEvent>?>(null);
    Task CompleteAsync(string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
    Task AbandonAsync(string leaseId, CancellationToken cancellationToken) => Task.CompletedTask;
}
