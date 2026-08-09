namespace Guance.Windows;

/// <summary>Represents an active RUM action that stops when the scope is disposed.</summary>
public sealed class RumActionScope : IDisposable
{
    private readonly GuanceClient client;
    private int disposed;

    internal RumActionScope(GuanceClient client, string actionId)
    {
        this.client = client;
        ActionId = actionId;
    }

    /// <summary>Gets the unique identifier of the active action.</summary>
    public string ActionId { get; }

    /// <summary>Stops the action the first time the scope is disposed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            client.StopAction(ActionId);
        }
    }
}
