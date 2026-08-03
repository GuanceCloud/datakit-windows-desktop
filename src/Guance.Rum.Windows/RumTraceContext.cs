using System.Collections.ObjectModel;

namespace Guance.Rum.Windows;

/// <summary>
/// Trace headers and identifiers returned by a custom trace-context provider.
/// </summary>
public sealed class RumTraceContext
{
    public RumTraceContext(
        IReadOnlyDictionary<string, string> headers,
        string? traceId,
        string? spanId)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Trace header names must not be empty.", nameof(headers));
            }

            if (value is null)
            {
                throw new ArgumentException("Trace header values must not be null.", nameof(headers));
            }

            copy[name] = value;
        }

        Headers = new ReadOnlyDictionary<string, string>(copy);
        TraceId = traceId;
        SpanId = spanId;
    }

    public IReadOnlyDictionary<string, string> Headers { get; }
    public string? TraceId { get; }
    public string? SpanId { get; }
}
