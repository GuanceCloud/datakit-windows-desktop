using Guance.Rum.Windows.Queue;

namespace Guance.Rum.Windows.Transport;

internal interface IDatawayTransport : IDisposable
{
    Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken);
}
