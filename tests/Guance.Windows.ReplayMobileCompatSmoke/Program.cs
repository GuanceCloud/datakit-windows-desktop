using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const string FakeSdkName = "df_android_rum_sdk";
    private const string FakeSdkVersion = "0.1.0-fake-mobile";
    private const string Service = "windows-replay-mobile-compat-smoke";
    private const string Version = "0.1.0-debug";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        var selfTest = args.Contains("--self-test", StringComparer.OrdinalIgnoreCase);
        var appId = selfTest
            ? "fake-mobile-replay-self-test"
            : Environment.GetEnvironmentVariable("GUANCE_RUM_APP_ID");

        if (string.IsNullOrWhiteSpace(appId))
        {
            Console.Error.WriteLine("Missing environment variable: GUANCE_RUM_APP_ID");
            return 2;
        }

        var fixture = FakeMobileReplayFixture.Create(appId);
        var replayRequest = BuildReplayRequest(fixture);
        ValidateFixture(fixture, replayRequest);

        if (selfTest)
        {
            Console.WriteLine("FAKE_MOBILE_REPLAY_SELF_TEST passed records=9 sequence=4,6,10,11,11,11,11,11,7");
            return 0;
        }

        var datawayUrl = Environment.GetEnvironmentVariable("GUANCE_RUM_DATAWAY_URL");
        var token = Environment.GetEnvironmentVariable("GUANCE_RUM_CLIENT_TOKEN");
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(datawayUrl))
        {
            missing.Add("GUANCE_RUM_DATAWAY_URL");
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            missing.Add("GUANCE_RUM_CLIENT_TOKEN");
        }
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("Missing environment variables: " + string.Join(", ", missing));
            return 2;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var replayResult = await SendReplayAsync(client, datawayUrl!, token!, replayRequest);
        if (!replayResult.Success)
        {
            Console.Error.WriteLine($"Replay upload failed status={replayResult.StatusCode} body={replayResult.SafeBody}");
            return 3;
        }

        var rumResult = await SendRumAsync(client, datawayUrl!, token!, fixture);
        if (!rumResult.Success)
        {
            Console.Error.WriteLine($"RUM upload failed status={rumResult.StatusCode} body={rumResult.SafeBody}");
            return 4;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            marker = "FAKE_MOBILE_REPLAY_RESULT",
            session_id = fixture.SessionId,
            view_id = fixture.ViewId,
            start_ms = fixture.StartMilliseconds,
            end_ms = fixture.EndMilliseconds,
            replay_status = replayResult.StatusCode,
            rum_status = rumResult.StatusCode,
            records = fixture.Records.Count
        }, JsonOptions));
        return 0;
    }

    private static ReplayRequest BuildReplayRequest(FakeMobileReplayFixture fixture)
    {
        var segmentPayload = new Dictionary<string, object?>
        {
            ["application"] = new Dictionary<string, object?> { ["id"] = fixture.AppId },
            ["session"] = new Dictionary<string, object?> { ["id"] = fixture.SessionId },
            ["view"] = new Dictionary<string, object?> { ["id"] = fixture.ViewId },
            ["start"] = fixture.StartMilliseconds,
            ["end"] = fixture.EndMilliseconds,
            ["records_count"] = fixture.Records.Count,
            ["index_in_view"] = 0,
            ["has_full_snapshot"] = true,
            ["source"] = "android",
            ["records"] = fixture.Records
        };
        var serializedSegment = JsonSerializer.SerializeToUtf8Bytes(segmentPayload, JsonOptions);
        var segment = new byte[serializedSegment.Length + 1];
        serializedSegment.CopyTo(segment, 0);
        segment[^1] = (byte)'\n';
        var compressed = CompressZlib(segment);
        var boundary = "guance-rum-replay-fake-" + Guid.NewGuid().ToString("N");
        using var output = new MemoryStream();

        WriteField(output, boundary, "records_count", fixture.Records.Count.ToString(CultureInfo.InvariantCulture));
        WriteField(output, boundary, "index_in_view", "0");
        WriteField(output, boundary, "source", "android");
        WriteField(output, boundary, "sdk_name", FakeSdkName);
        WriteField(output, boundary, "sdk_version", FakeSdkVersion);
        WriteField(output, boundary, "start", fixture.StartMilliseconds.ToString(CultureInfo.InvariantCulture));
        WriteField(output, boundary, "end", fixture.EndMilliseconds.ToString(CultureInfo.InvariantCulture));
        WriteField(output, boundary, "app_id", fixture.AppId);
        WriteField(output, boundary, "view_id", fixture.ViewId);
        WriteField(output, boundary, "session_id", fixture.SessionId);
        WriteField(output, boundary, "env", "local");
        WriteField(output, boundary, "service", Service);
        WriteField(output, boundary, "version", Version);
        WriteField(output, boundary, "raw_segment_size", segment.Length.ToString(CultureInfo.InvariantCulture));
        WriteField(output, boundary, "has_full_snapshot", "true");
        WriteFile(output, boundary, "segment", fixture.ViewId, compressed);
        WriteAscii(output, $"--{boundary}--\r\n");

        return new ReplayRequest($"multipart/form-data; boundary={boundary}", output.ToArray(), segment);
    }

    private static void ValidateFixture(FakeMobileReplayFixture fixture, ReplayRequest request)
    {
        var recordTypes = fixture.Records.Select(GetRecordType).ToArray();
        var metaIndex = Array.IndexOf(recordTypes, 4);
        var fullSnapshotIndex = Array.IndexOf(recordTypes, 10);
        Require(metaIndex == 0, "Meta record must be first.");
        Require(fullSnapshotIndex > metaIndex, "FullSnapshot must follow Meta.");
        Require(recordTypes[^1] == 7, "ViewEnd record must be last.");
        Require(recordTypes.Count(type => type == 10) == 1, "Fixture must contain exactly one FullSnapshot.");
        Require(fixture.Records.Count >= 2, "Replayer requires at least two events.");

        var extracted = ExtractReplaySegment(request.ContentType, request.Body);
        Require(extracted.SequenceEqual(request.RawSegment), "Multipart zlib segment did not round-trip.");
        using var json = JsonDocument.Parse(extracted);
        var root = json.RootElement;
        Require(root.GetProperty("source").GetString() == "android", "Segment source must be android for fake mobile flow.");
        Require(root.GetProperty("records_count").GetInt32() == fixture.Records.Count, "records_count does not match records.");
        Require(root.GetProperty("records")[0].GetProperty("type").GetInt32() == 4, "Serialized first record is not Meta.");
        var meta = root.GetProperty("records")[0].GetProperty("data");
        Require(meta.GetProperty("width").GetInt32() > 0 && meta.GetProperty("height").GetInt32() > 0,
            "Meta viewport must be non-zero.");

        var rumLines = BuildRumLines(fixture).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Require(rumLines.Length == 4, "Fixture must contain one View and three Action RUM events.");
        Require(rumLines[0].StartsWith("view,", StringComparison.Ordinal), "First RUM event must be a View.");
        Require(rumLines.Skip(1).All(line => line.StartsWith("action,", StringComparison.Ordinal)),
            "Remaining RUM events must be Actions.");
        Require(rumLines.All(line => line.Contains($"sdk_name={FakeSdkName}", StringComparison.Ordinal)),
            "Every fake RUM event must use the Android SDK identity.");
        Require(rumLines.All(line => line.Contains("session_has_replay=true", StringComparison.Ordinal)),
            "Every fake RUM event must advertise Replay availability.");
    }

    private static int GetRecordType(object record)
    {
        var json = JsonSerializer.SerializeToElement(record, JsonOptions);
        return json.GetProperty("type").GetInt32();
    }

    private static async Task<UploadResult> SendReplayAsync(
        HttpClient client,
        string datawayUrl,
        string token,
        ReplayRequest replayRequest)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildDatawayUri(datawayUrl, "v1/write/rum/replay", token));
        AddDeviceHeaders(request);
        request.Content = new ByteArrayContent(replayRequest.Body);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(replayRequest.ContentType);
        return await SendAsync(client, request);
    }

    private static async Task<UploadResult> SendRumAsync(
        HttpClient client,
        string datawayUrl,
        string token,
        FakeMobileReplayFixture fixture)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildDatawayUri(datawayUrl, "v1/write/rum", token));
        AddDeviceHeaders(request);
        request.Content = new StringContent(BuildRumLines(fixture), Encoding.UTF8, "text/plain");
        return await SendAsync(client, request);
    }

    private static async Task<UploadResult> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            using var response = await client.SendAsync(request);
            var status = (int)response.StatusCode;
            var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync();
            var safeBody = RedactAndTruncate(body, 512);
            return new UploadResult(status is >= 200 and < 300, status, safeBody);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new UploadResult(false, 0, RedactAndTruncate(exception.Message, 512));
        }
    }

    private static string BuildRumLines(FakeMobileReplayFixture fixture)
    {
        var common = new Dictionary<string, string>
        {
            ["app_id"] = fixture.AppId,
            ["service"] = Service,
            ["env"] = "local",
            ["version"] = Version,
            ["sdk_name"] = FakeSdkName,
            ["sdk_version"] = FakeSdkVersion,
            ["application_uuid"] = fixture.ApplicationId,
            ["os"] = "android",
            ["os_version"] = "14",
            ["os_version_major"] = "14",
            ["device"] = "mobile",
            ["model"] = "Fake Mobile Replay Harness",
            ["arch"] = "arm64",
            ["screen_size"] = "800*600",
            ["locale"] = "zh-CN",
            ["network_type"] = "wifi",
            ["session_id"] = fixture.SessionId,
            ["session_type"] = "user",
            ["is_signin"] = "F",
            ["userid"] = fixture.SessionId,
            ["view_id"] = fixture.ViewId,
            ["view_name"] = "Fake Mobile Replay Debug"
        };

        var lines = new StringBuilder();
        lines.Append(FormatLine("view", common,
            new Dictionary<string, object?>
            {
                ["time_spent"] = (fixture.EndMilliseconds - fixture.StartMilliseconds) * 1_000_000L,
                ["is_active"] = false,
                ["view_action_count"] = 3L,
                ["view_resource_count"] = 0L,
                ["view_error_count"] = 0L,
                ["view_long_task_count"] = 0L,
                ["view_update_time"] = fixture.EndMilliseconds * 1_000_000L,
                ["session_has_replay"] = true,
                ["session_sample_rate"] = 1.0,
                ["session_on_error_sample_rate"] = 0.0
            }, fixture.StartMilliseconds * 1_000_000L));

        AddActionLine(lines, common, fixture, "Open fake replay", "tap", 2_000, 120);
        AddActionLine(lines, common, fixture, "Update replay label", "tap", 5_000, 90);
        AddActionLine(lines, common, fixture, "Resize fake viewport", "tap", 8_000, 150);
        return lines.ToString();
    }

    private static void AddActionLine(
        StringBuilder lines,
        IReadOnlyDictionary<string, string> common,
        FakeMobileReplayFixture fixture,
        string name,
        string type,
        int offsetMilliseconds,
        int durationMilliseconds)
    {
        var tags = new Dictionary<string, string>(common)
        {
            ["action_id"] = Guid.NewGuid().ToString("N"),
            ["action_name"] = name,
            ["action_type"] = type
        };
        lines.Append(FormatLine("action", tags,
            new Dictionary<string, object?>
            {
                ["duration"] = durationMilliseconds * 1_000_000L,
                ["action_resource_count"] = 0L,
                ["action_error_count"] = 0L,
                ["action_long_task_count"] = 0L,
                ["session_has_replay"] = true,
                ["session_sample_rate"] = 1.0,
                ["session_on_error_sample_rate"] = 0.0
            }, (fixture.StartMilliseconds + offsetMilliseconds) * 1_000_000L));
    }

    private static string FormatLine(
        string measurement,
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, object?> fields,
        long timestampNanoseconds)
    {
        var builder = new StringBuilder(EscapeMeasurement(measurement));
        foreach (var tag in tags.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(tag.Value))
            {
                continue;
            }
            builder.Append(',').Append(EscapeKey(tag.Key)).Append('=').Append(EscapeTagValue(tag.Value));
        }
        builder.Append(' ');
        var first = true;
        foreach (var field in fields.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }
            first = false;
            builder.Append(EscapeKey(field.Key)).Append('=').Append(FormatField(field.Value));
        }
        builder.Append(' ').Append(timestampNanoseconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return builder.ToString();
    }

    private static string FormatField(object? value) => value switch
    {
        null => "\"\"",
        string text => $"\"{EscapeFieldString(text)}\"",
        bool boolean => boolean ? "true" : "false",
        byte or sbyte or short or ushort or int or uint or long or ulong =>
            Convert.ToString(value, CultureInfo.InvariantCulture) + "i",
        float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => $"\"{EscapeFieldString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}\""
    };

    private static Uri BuildDatawayUri(string baseUrl, string path, string token)
    {
        var normalized = baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        var builder = new UriBuilder(normalized)
        {
            Query = "token=" + Uri.EscapeDataString(token) + "&to_headless=true"
        };
        return builder.Uri;
    }

    private static void AddDeviceHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Datakit-Device-Time",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Datakit-Trace", Guid.NewGuid().ToString("N"));
    }

    private static byte[] CompressZlib(byte[] value)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(value, 0, value.Length);
        }
        return output.ToArray();
    }

    private static byte[] ExtractReplaySegment(string contentType, byte[] multipartBody)
    {
        var boundary = contentType[(contentType.IndexOf("boundary=", StringComparison.Ordinal) + "boundary=".Length)..];
        var marker = Encoding.ASCII.GetBytes("Content-Type: application/octet-stream\r\n\r\n");
        var start = IndexOf(multipartBody, marker, 0);
        Require(start >= 0, "Multipart segment marker was not found.");
        start += marker.Length;
        var endMarker = Encoding.ASCII.GetBytes($"\r\n--{boundary}");
        var end = IndexOf(multipartBody, endMarker, start);
        Require(end > start, "Multipart segment end marker was not found.");
        using var compressed = new MemoryStream(multipartBody, start, end - start, writable: false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static int IndexOf(byte[] source, byte[] value, int start)
    {
        for (var i = start; i <= source.Length - value.Length; i++)
        {
            var matches = true;
            for (var j = 0; j < value.Length; j++)
            {
                if (source[i + j] == value[j])
                {
                    continue;
                }
                matches = false;
                break;
            }
            if (matches)
            {
                return i;
            }
        }
        return -1;
    }

    private static void WriteField(Stream output, string boundary, string name, string value)
    {
        WriteAscii(output, $"--{boundary}\r\n");
        WriteAscii(output, $"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
        WriteUtf8(output, value);
        WriteAscii(output, "\r\n");
    }

    private static void WriteFile(Stream output, string boundary, string name, string fileName, byte[] data)
    {
        WriteAscii(output, $"--{boundary}\r\n");
        WriteAscii(output, $"Content-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\n");
        WriteAscii(output, "Content-Type: application/octet-stream\r\n\r\n");
        output.Write(data, 0, data.Length);
        WriteAscii(output, "\r\n");
    }

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));

    private static void WriteUtf8(Stream output, string value) =>
        output.Write(Encoding.UTF8.GetBytes(value));

    private static string EscapeMeasurement(string value) => value.Replace(",", "\\,").Replace(" ", "\\ ");
    private static string EscapeKey(string value) => value.Replace(",", "\\,").Replace(" ", "\\ ").Replace("=", "\\=");
    private static string EscapeTagValue(string value) => value.Replace(",", "\\,").Replace(" ", "\\ ").Replace("=", "\\=");
    private static string EscapeFieldString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string RedactAndTruncate(string value, int maxLength)
    {
        var redacted = Regex.Replace(value, "(?i)(token=)[^&\\s]+", "$1redacted");
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "...";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ReplayRequest(string ContentType, byte[] Body, byte[] RawSegment);
    private sealed record UploadResult(bool Success, int StatusCode, string SafeBody);

    private sealed record FakeMobileReplayFixture(
        string AppId,
        string ApplicationId,
        string SessionId,
        string ViewId,
        long StartMilliseconds,
        long EndMilliseconds,
        IReadOnlyList<object> Records)
    {
        public static FakeMobileReplayFixture Create(string appId)
        {
            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 15_000;
            var end = start + 12_000;
            var sessionId = Guid.NewGuid().ToString("D");
            var viewId = Guid.NewGuid().ToString("D");
            var applicationId = Guid.NewGuid().ToString("D");
            var records = new List<object>
            {
                Record(4, start, new Dictionary<string, object?>
                {
                    ["width"] = 800,
                    ["height"] = 600,
                    ["href"] = "fake-mobile://windows-replay-debug"
                }),
                Record(6, start + 1, new Dictionary<string, object?> { ["has_focus"] = true }),
                Record(10, start + 10, new Dictionary<string, object?>
                {
                    ["wireframes"] = BuildWireframes()
                }),
                Record(11, start + 2_000, Pointer("down", 1, 180, 190)),
                Record(11, start + 2_080, Pointer("up", 1, 180, 190)),
                Record(11, start + 5_000, new Dictionary<string, object?>
                {
                    ["source"] = 0,
                    ["adds"] = Array.Empty<object>(),
                    ["removes"] = Array.Empty<object>(),
                    ["updates"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = 5,
                            ["type"] = "text",
                            ["text"] = "Step 2: mutation rendered"
                        }
                    }
                }),
                Record(11, start + 8_000, new Dictionary<string, object?>
                {
                    ["source"] = 4,
                    ["width"] = 900,
                    ["height"] = 650
                }),
                Record(11, start + 9_000, new Dictionary<string, object?>
                {
                    ["source"] = 2,
                    ["positions"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = 0,
                            ["x"] = 510,
                            ["y"] = 360,
                            ["timestamp"] = start + 9_000
                        }
                    }
                }),
                Record(7, end, new Dictionary<string, object?>())
            };
            return new FakeMobileReplayFixture(appId, applicationId, sessionId, viewId, start, end, records);
        }

        private static Dictionary<string, object?> Record(int type, long timestamp, object data) => new()
        {
            ["type"] = type,
            ["timestamp"] = timestamp,
            ["data"] = data
        };

        private static Dictionary<string, object?> Pointer(string eventType, int pointerId, int x, int y) => new()
        {
            ["source"] = 9,
            ["pointerType"] = "touch",
            ["pointerId"] = pointerId,
            ["x"] = x,
            ["y"] = y,
            ["pointerEventType"] = eventType
        };

        private static object[] BuildWireframes() =>
        [
            Shape(1, 0, 0, 800, 600, "#F4F7FFFF", 0),
            Shape(2, 28, 24, 744, 552, "#FFFFFFFF", 18),
            Shape(3, 28, 24, 744, 92, "#2563EBFF", 18),
            Text(4, 56, 48, 650, 48, "Fake Mobile Replay", 28, "#FFFFFFFF"),
            Text(5, 64, 152, 610, 42, "Step 1: full snapshot rendered", 20, "#111827FF"),
            Shape(6, 64, 222, 300, 118, "#DBEAFEFF", 12),
            Text(7, 86, 252, 250, 58, "Tap target", 20, "#1E3A8AFF"),
            Shape(8, 400, 222, 300, 118, "#DCFCE7FF", 12),
            Text(9, 422, 252, 250, 58, "Viewport resize at 8s", 18, "#14532DFF"),
            Text(10, 64, 400, 620, 70, "If this text is visible and changes at 5s, the full mobile Replay path is working.", 16, "#374151FF")
        ];

        private static Dictionary<string, object?> Shape(
            int id,
            int x,
            int y,
            int width,
            int height,
            string color,
            int cornerRadius) => new()
        {
            ["id"] = id,
            ["type"] = "shape",
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["shapeStyle"] = new Dictionary<string, object?>
            {
                ["backgroundColor"] = color,
                ["opacity"] = 1,
                ["cornerRadius"] = cornerRadius
            }
        };

        private static Dictionary<string, object?> Text(
            int id,
            int x,
            int y,
            int width,
            int height,
            string text,
            int size,
            string color) => new()
        {
            ["id"] = id,
            ["type"] = "text",
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["text"] = text,
            ["textStyle"] = new Dictionary<string, object?>
            {
                ["family"] = "Segoe UI, sans-serif",
                ["size"] = size,
                ["color"] = color
            },
            ["textPosition"] = new Dictionary<string, object?>
            {
                ["padding"] = new Dictionary<string, object?>
                {
                    ["top"] = 0,
                    ["right"] = 0,
                    ["bottom"] = 0,
                    ["left"] = 0
                },
                ["alignment"] = new Dictionary<string, object?>
                {
                    ["horizontal"] = "left",
                    ["vertical"] = "center"
                }
            }
        };
    }
}
