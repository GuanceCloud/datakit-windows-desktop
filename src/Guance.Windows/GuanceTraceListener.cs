using System.Diagnostics;
using System.Text;

namespace Guance.Windows;

/// <summary>
/// Forwards <see cref="Trace"/> and <see cref="TraceSource"/> output to Guance Logging.
/// </summary>
public sealed class GuanceTraceListener : TraceListener
{
    [ThreadStatic]
    private static bool isForwarding;

    private readonly GuanceClient client;
    private readonly ThreadLocal<StringBuilder> buffers = new(
        () => new StringBuilder(),
        trackAllValues: true);

    /// <summary>Creates a listener that forwards trace output through the supplied client.</summary>
    /// <param name="client">The client that accepts forwarded logs.</param>
    public GuanceTraceListener(GuanceClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public override void Write(string? message)
    {
        if (message is not null)
        {
            var buffer = buffers.Value!;
            lock (buffer)
            {
                buffer.Append(message);
            }
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? message)
    {
        var buffer = buffers.Value!;
        lock (buffer)
        {
            buffer.Append(message);
            ForwardBuffer(buffer);
        }
    }

    /// <inheritdoc />
    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        Forward(message ?? string.Empty, ToLogStatus(eventType), CreateProperties(source, eventType, id));
    }

    /// <inheritdoc />
    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args)
    {
        var message = args is { Length: > 0 }
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, format ?? string.Empty, args)
            : format ?? string.Empty;
        Forward(message, ToLogStatus(eventType), CreateProperties(source, eventType, id));
    }

    /// <inheritdoc />
    public override void Fail(string? message, string? detailMessage)
    {
        var content = string.IsNullOrWhiteSpace(detailMessage)
            ? message ?? string.Empty
            : $"{message}: {detailMessage}";
        Forward(content, LogStatus.Critical, null);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        foreach (var buffer in buffers.Values)
        {
            lock (buffer)
            {
                ForwardBuffer(buffer);
            }
        }

        base.Flush();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
            buffers.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ForwardBuffer(StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var message = buffer.ToString();
        buffer.Clear();
        Forward(message, LogStatus.Info, null);
    }

    private void Forward(
        string message,
        LogStatus status,
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (isForwarding)
        {
            return;
        }

        try
        {
            isForwarding = true;
            client.AddLog(message, status, properties);
        }
        finally
        {
            isForwarding = false;
        }
    }

    private static IReadOnlyDictionary<string, object?> CreateProperties(
        string source,
        TraceEventType eventType,
        int id)
    {
        return new Dictionary<string, object?>
        {
            ["logger_name"] = source,
            ["trace_event_type"] = eventType.ToString(),
            ["event_id"] = id
        };
    }

    private static LogStatus ToLogStatus(TraceEventType eventType)
    {
        return eventType switch
        {
            TraceEventType.Critical => LogStatus.Critical,
            TraceEventType.Error => LogStatus.Error,
            TraceEventType.Warning => LogStatus.Warning,
            TraceEventType.Verbose => LogStatus.Debug,
            _ => LogStatus.Info
        };
    }
}
