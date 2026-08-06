namespace Guance.Windows;

public sealed class RumActionScope : IDisposable
{
    private readonly GuanceClient client;
    private int disposed;

    internal RumActionScope(GuanceClient client, string actionId)
    {
        this.client = client;
        ActionId = actionId;
    }

    public string ActionId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            client.StopAction(ActionId);
        }
    }
}
