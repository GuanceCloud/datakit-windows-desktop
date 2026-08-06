using Guance.Windows.Queue;

namespace Guance.Windows.Transport;

internal interface ILogTransport : IDisposable
{
    Task<SendResult> SendAsync(IReadOnlyList<QueuedLogEvent> events, CancellationToken cancellationToken);
}
