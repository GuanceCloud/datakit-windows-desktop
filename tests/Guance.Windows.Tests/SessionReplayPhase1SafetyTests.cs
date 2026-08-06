using Guance.Windows.Queue;
using Guance.Windows.SessionReplay;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class SessionReplayPhase1SafetyTests
{
    [Fact]
    public async Task ManualStartDoesNotOverrideDisabledConfiguration()
    {
        await using var manager = new SessionReplayManager(
            new GuanceConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                SessionReplay = new RumSessionReplayConfig { Enabled = false }
            },
            new RejectingReplayQueue(),
            new SessionReplayPrivacyOverrides());

        manager.Start();

        Assert.False(manager.IsRecordingEnabled);
        Assert.False(manager.HasReplay("session"));
    }

    private sealed class RejectingReplayQueue : ISessionReplayQueue
    {
        public Task<BatchAppendResult> EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Phase 1 must not enqueue Session Replay data.");

        public Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueuedSessionReplaySegment>>(Array.Empty<QueuedSessionReplaySegment>());

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task TrimAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
