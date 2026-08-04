using Guance.Rum.Windows.Queue;

namespace Guance.Rum.Windows.Transport;

internal interface ILogTransport : IDisposable
{
    Task<SendResult> SendAsync(IReadOnlyList<QueuedLogEvent> events, CancellationToken cancellationToken);
}
