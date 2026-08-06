namespace Guance.Windows;

public sealed record LogEntry(
    string Content,
    string Status,
    IReadOnlyDictionary<string, object?>? Properties = null)
{
    public LogEntry(
        string content,
        LogStatus status,
        IReadOnlyDictionary<string, object?>? properties = null)
        : this(content, LogStatusNames.ToProtocolValue(status), properties)
    {
    }
}
