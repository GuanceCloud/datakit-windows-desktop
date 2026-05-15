using System.IO;
using System.Text;
using System.Text.Json;

namespace Guance.Rum.Windows.SessionReplay;

internal sealed class SessionReplaySegmentBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SessionReplayContext context;
    private readonly IReadOnlyList<object> records;
    private readonly bool hasFullSnapshot;
    private readonly string creationReason;
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
        this.creationReason = creationReason;
        this.indexInView = indexInView;
        this.startMilliseconds = startMilliseconds;
        this.endMilliseconds = endMilliseconds;
    }

    public (string ContentType, byte[] Body) Build()
    {
        var segment = JsonSerializer.SerializeToUtf8Bytes(records, JsonOptions);
        var boundary = "guance-rum-replay-" + Guid.NewGuid().ToString("N");
        using var output = new MemoryStream();

        WriteField(output, boundary, "records_count", records.Count.ToString());
        WriteField(output, boundary, "index_in_view", indexInView.ToString());
        WriteField(output, boundary, "source", "windows");
        WriteField(output, boundary, "sdk_name", context.SdkName);
        WriteField(output, boundary, "sdk_version", context.SdkVersion);
        WriteField(output, boundary, "start", startMilliseconds.ToString());
        WriteField(output, boundary, "end", endMilliseconds.ToString());
        WriteField(output, boundary, "app_id", context.AppId);
        WriteField(output, boundary, "view_id", context.ViewId ?? string.Empty);
        WriteField(output, boundary, "creation_reason", creationReason);
        WriteField(output, boundary, "session_id", context.SessionId);
        WriteField(output, boundary, "env", context.Env);
        WriteField(output, boundary, "service", context.Service);
        WriteField(output, boundary, "version", context.Version);
        WriteField(output, boundary, "raw_segment_size", segment.Length.ToString());
        WriteField(output, boundary, "has_full_snapshot", hasFullSnapshot ? "true" : "false");
        WriteFile(output, boundary, "segment", "segment", segment);
        WriteAscii(output, $"--{boundary}--\r\n");

        return ($"multipart/form-data; boundary={boundary}", output.ToArray());
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
        WriteAscii(output, "Content-Type: application/json\r\n\r\n");
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
