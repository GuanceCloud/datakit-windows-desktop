using Guance.Windows.Transport;

namespace Guance.Windows.SessionReplay;

internal interface ISessionReplayTransport : IDisposable
{
    Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken);
}
