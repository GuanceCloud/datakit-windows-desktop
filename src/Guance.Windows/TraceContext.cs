using System.Collections.ObjectModel;

namespace Guance.Windows;

/// <summary>
/// Trace headers and identifiers returned by a custom trace-context provider.
/// </summary>
public sealed class TraceContext
{
    /// <summary>Creates a custom trace context.</summary>
    /// <param name="headers">Headers to add to the outgoing request.</param>
    /// <param name="traceId">The trace identifier associated with the RUM resource.</param>
    /// <param name="spanId">The span identifier associated with the RUM resource.</param>
    public TraceContext(
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

    /// <summary>Gets the case-insensitive request headers to inject.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
    /// <summary>Gets the trace identifier used for RUM correlation.</summary>
    public string? TraceId { get; }
    /// <summary>Gets the span identifier used for RUM correlation.</summary>
    public string? SpanId { get; }
}
