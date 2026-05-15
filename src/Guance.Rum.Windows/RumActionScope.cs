namespace Guance.Rum.Windows;

public sealed class RumActionScope : IDisposable
{
    private readonly RumClient client;
    private int disposed;

    internal RumActionScope(RumClient client, string actionId)
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
