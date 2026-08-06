using Guance.Windows.SessionReplay;

namespace Guance.Windows.Queue;

internal sealed class DisabledLogQueue : ILogQueue
{
    public Task<BatchAppendResult> EnqueueAsync(string line, CancellationToken cancellationToken) =>
        Task.FromResult(BatchAppendResult.Rejected);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class DisabledSessionReplayQueue : ISessionReplayQueue
{
    public Task<BatchAppendResult> EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken) =>
        Task.FromResult(BatchAppendResult.Rejected);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
