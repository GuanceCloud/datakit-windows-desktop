using Guance.Rum.Windows.Transport;

namespace Guance.Rum.Windows.SessionReplay;

internal interface ISessionReplayTransport : IDisposable
{
    Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken);
}
