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

    /// <summary>Gets whether this call was accepted as the current Action.</summary>
    public bool IsAccepted => ActionId.Length != 0;

    internal static RumActionScope Rejected(GuanceClient client) => new(client, string.Empty);

    /// <summary>Stops a need-wait Action the first time the scope is disposed. Normal Actions ignore disposal.</summary>
    public void Dispose()
    {
        if (ActionId.Length != 0 && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            client.StopAction(ActionId);
        }
    }
}
