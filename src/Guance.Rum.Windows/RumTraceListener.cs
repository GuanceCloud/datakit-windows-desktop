using System.Diagnostics;
using System.Text;

namespace Guance.Rum.Windows;

/// <summary>
/// Forwards <see cref="Trace"/> and <see cref="TraceSource"/> output to Guance Logging.
/// </summary>
public sealed class RumTraceListener : TraceListener
{
    [ThreadStatic]
    private static bool isForwarding;

    private readonly RumClient client;
    private readonly ThreadLocal<StringBuilder> buffers = new(
        () => new StringBuilder(),
        trackAllValues: true);

    public RumTraceListener(RumClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

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

    public override void WriteLine(string? message)
    {
        var buffer = buffers.Value!;
        lock (buffer)
        {
            buffer.Append(message);
            ForwardBuffer(buffer);
        }
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        Forward(message ?? string.Empty, ToLogStatus(eventType), CreateProperties(source, eventType, id));
    }

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

    public override void Fail(string? message, string? detailMessage)
    {
        var content = string.IsNullOrWhiteSpace(detailMessage)
            ? message ?? string.Empty
            : $"{message}: {detailMessage}";
        Forward(content, RumLogStatus.Critical, null);
    }

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
        Forward(message, RumLogStatus.Info, null);
    }

    private void Forward(
        string message,
        RumLogStatus status,
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

    private static RumLogStatus ToLogStatus(TraceEventType eventType)
    {
        return eventType switch
        {
            TraceEventType.Critical => RumLogStatus.Critical,
            TraceEventType.Error => RumLogStatus.Error,
            TraceEventType.Warning => RumLogStatus.Warning,
            TraceEventType.Verbose => RumLogStatus.Debug,
            _ => RumLogStatus.Info
        };
    }
}
