using Guance.Windows.Queue;

namespace Guance.Windows.Transport;

internal sealed class DatawayTransport : IDatawayTransport
{
    private readonly LineProtocolTransport transport;

    public DatawayTransport(GuanceConfig config)
    {
        transport = new LineProtocolTransport(config, RumConstants.RumWritePath);
    }

    public async Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken)
    {
        var body = string.Concat(events.Select(item => item.Line));
        return await transport.SendAsync(body, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        transport.Dispose();
    }
}
