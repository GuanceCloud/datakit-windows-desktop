using Guance.Rum.Windows.Queue;

namespace Guance.Rum.Windows.Transport;

internal sealed class LogTransport : ILogTransport
{
    private readonly LineProtocolTransport transport;

    public LogTransport(RumConfig config)
    {
        transport = new LineProtocolTransport(config, RumConstants.LogWritePath);
    }

    public async Task<SendResult> SendAsync(
        IReadOnlyList<QueuedLogEvent> events,
        CancellationToken cancellationToken)
    {
        var body = string.Concat(events.Select(item => item.Line));
        return await transport.SendAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => transport.Dispose();
}
