namespace Guance.Windows;

/// <summary>Represents one custom log submitted as part of a batch.</summary>
/// <param name="Content">Log message.</param>
/// <param name="Status">Predefined or application-defined status.</param>
/// <param name="Properties">Optional event properties.</param>
public sealed record LogEntry(
    string Content,
    string Status,
    IReadOnlyDictionary<string, object?>? Properties = null)
{
    /// <summary>Creates a log entry using a predefined <see cref="LogStatus" />.</summary>
    public LogEntry(
        string content,
        LogStatus status,
        IReadOnlyDictionary<string, object?>? properties = null)
        : this(content, LogStatusNames.ToProtocolValue(status), properties)
    {
    }
}
