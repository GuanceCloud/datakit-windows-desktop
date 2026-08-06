using Guance.Windows.Queue;

namespace Guance.Windows.Transport;

internal interface IDatawayTransport : IDisposable
{
    Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken);
}
