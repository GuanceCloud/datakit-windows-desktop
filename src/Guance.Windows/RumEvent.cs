namespace Guance.Windows;

internal sealed class RumEvent : ILineProtocolPoint
{
    public RumEvent(string measurement, long timestampNanoseconds)
    {
        Measurement = measurement;
        TimestampNanoseconds = timestampNanoseconds;
    }

    public string Measurement { get; }
    public long TimestampNanoseconds { get; }
    public Dictionary<string, object?> Tags { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);

    public RumEvent WithTag(string key, object? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
        {
            Tags[key] = value;
        }

        return this;
    }

    public RumEvent WithField(string key, object? value)
    {
        Fields[key] = value;
        return this;
    }
}
