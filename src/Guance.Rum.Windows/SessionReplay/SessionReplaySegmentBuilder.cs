using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Guance.Rum.Windows.SessionReplay;

internal sealed class SessionReplaySegmentBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object DebugPayloadGate = new();
    private static readonly Dictionary<string, string> DebugPayloadsByContentType = new(StringComparer.Ordinal);
    private readonly SessionReplayContext context;
    private readonly IReadOnlyList<object> records;
    private readonly bool hasFullSnapshot;
    private readonly int indexInView;
    private readonly long startMilliseconds;
    private readonly long endMilliseconds;

    public SessionReplaySegmentBuilder(
        SessionReplayContext context,
        IReadOnlyList<object> records,
        bool hasFullSnapshot,
        string creationReason,
        int indexInView,
        long startMilliseconds,
        long endMilliseconds)
    {
        this.context = context;
        this.records = records;
        this.hasFullSnapshot = hasFullSnapshot;
        this.indexInView = indexInView;
        this.startMilliseconds = startMilliseconds;
        this.endMilliseconds = endMilliseconds;
    }

    public (string ContentType, byte[] Body) Build()
    {
        var segmentPayload = BuildWindowsSegment();
        var serializedSegment = JsonSerializer.SerializeToUtf8Bytes(segmentPayload, JsonOptions);
        var segment = new byte[serializedSegment.Length + 1];
        serializedSegment.CopyTo(segment, 0);
        segment[^1] = (byte)'\n';
        var compressedSegment = CompressSegment(segment);
        var boundary = "guance-rum-replay-" + Guid.NewGuid().ToString("N");
        var contentType = $"multipart/form-data; boundary={boundary}";
        using var output = new MemoryStream();

        WriteField(output, boundary, "records_count", records.Count.ToString());
        WriteField(output, boundary, "index_in_view", indexInView.ToString());
        WriteField(output, boundary, "source", RumConstants.WindowsSource);
        WriteField(output, boundary, "sdk_name", context.SdkName);
        WriteField(output, boundary, "sdk_version", context.SdkVersion);
        WriteField(output, boundary, "start", startMilliseconds.ToString());
        WriteField(output, boundary, "end", endMilliseconds.ToString());
        WriteField(output, boundary, "app_id", context.AppId);
        WriteField(output, boundary, "view_id", context.ViewId ?? string.Empty);
        WriteField(output, boundary, "session_id", context.SessionId);
        WriteField(output, boundary, "env", context.Env);
        WriteField(output, boundary, "service", context.Service);
        WriteField(output, boundary, "version", context.Version);
        WriteField(output, boundary, "raw_segment_size", segment.Length.ToString());
        WriteField(output, boundary, "has_full_snapshot", hasFullSnapshot ? "true" : "false");
        WriteFile(output, boundary, "segment", string.IsNullOrWhiteSpace(context.ViewId) ? "segment" : context.ViewId!, compressedSegment);
        WriteAscii(output, $"--{boundary}--\r\n");

        var body = output.ToArray();
        RememberDebugPayload(contentType, body, Encoding.UTF8.GetString(segment));
        return (contentType, body);
    }

    public static string? TryGetDebugPayload(string contentType, byte[] body)
    {
        lock (DebugPayloadGate)
        {
            if (DebugPayloadsByContentType.TryGetValue(contentType, out var payload))
            {
                return payload;
            }
        }

        return null;
    }

    private Dictionary<string, object?> BuildWindowsSegment()
    {
        return new Dictionary<string, object?>
        {
            ["application"] = new Dictionary<string, object?> { ["id"] = NormalizeReplayId(context.AppId) },
            ["session"] = new Dictionary<string, object?> { ["id"] = NormalizeReplayId(context.SessionId) },
            ["view"] = new Dictionary<string, object?> { ["id"] = NormalizeReplayId(context.ViewId ?? string.Empty) },
            ["start"] = startMilliseconds,
            ["end"] = endMilliseconds,
            ["records_count"] = records.Count,
            ["index_in_view"] = indexInView,
            ["has_full_snapshot"] = hasFullSnapshot,
            ["source"] = RumConstants.WindowsSource,
            ["records"] = records
        };
    }

    private static byte[] CompressSegment(byte[] segment)
    {
        using var output = new MemoryStream(segment.Length);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(segment, 0, segment.Length);
        }

        return output.ToArray();
    }

    private static void RememberDebugPayload(string contentType, byte[] body, string payload)
    {
        lock (DebugPayloadGate)
        {
            if (DebugPayloadsByContentType.Count > 16)
            {
                DebugPayloadsByContentType.Clear();
            }

            DebugPayloadsByContentType[contentType] = payload;
        }
    }

    private static string NormalizeReplayId(string value)
    {
        return Guid.TryParse(value, out var guid) ? guid.ToString("D") : value;
    }

    private static void WriteField(Stream output, string boundary, string name, string value)
    {
        WriteAscii(output, $"--{boundary}\r\n");
        WriteAscii(output, $"Content-Disposition: form-data; name=\"{EscapeName(name)}\"\r\n\r\n");
        WriteUtf8(output, value);
        WriteAscii(output, "\r\n");
    }

    private static void WriteFile(Stream output, string boundary, string name, string fileName, byte[] data)
    {
        WriteAscii(output, $"--{boundary}\r\n");
        WriteAscii(output, $"Content-Disposition: form-data; name=\"{EscapeName(name)}\"; filename=\"{EscapeName(fileName)}\"\r\n");
        WriteAscii(output, "Content-Type: application/octet-stream\r\n\r\n");
        output.Write(data, 0, data.Length);
        WriteAscii(output, "\r\n");
    }

    private static string EscapeName(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static void WriteAscii(Stream output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUtf8(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        output.Write(bytes, 0, bytes.Length);
    }
}
