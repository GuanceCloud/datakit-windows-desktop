namespace Guance.Rum.Windows;

public sealed record RumLogEntry(
    string Content,
    string Status,
    IReadOnlyDictionary<string, object?>? Properties = null)
{
    public RumLogEntry(
        string content,
        RumLogStatus status,
        IReadOnlyDictionary<string, object?>? properties = null)
        : this(content, RumLogStatusNames.ToProtocolValue(status), properties)
    {
    }
}
