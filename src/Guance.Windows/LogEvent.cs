namespace Guance.Windows;

internal sealed class LogEvent(long timestampNanoseconds) : ILineProtocolPoint
{
    public string Measurement => RumConstants.WindowsLogSource;
    public long TimestampNanoseconds { get; } = timestampNanoseconds;
    public Dictionary<string, object?> Tags { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);

    public LogEvent WithTag(string key, object? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
        {
            Tags[key] = value;
        }

        return this;
    }

    public LogEvent WithField(string key, object? value)
    {
        Fields[key] = value;
        return this;
    }
}
