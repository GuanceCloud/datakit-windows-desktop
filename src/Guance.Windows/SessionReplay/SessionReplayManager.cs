using System.IO;
using System.Text;
using System.Text.Json;

namespace Guance.Windows.SessionReplay;

internal sealed class SessionReplayManager : IAsyncDisposable
{
    private static readonly TimeSpan ErrorBufferWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IncrementalCoalesceWindow = TimeSpan.FromMilliseconds(200);
    private readonly GuanceConfig config;
    private readonly ISessionReplayQueue queue;
    private readonly SessionReplayPrivacyOverrides privacyOverrides;
    private readonly Random random = new();
    private readonly object gate = new();
    private readonly Dictionary<string, int> viewIndexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> coalescedPendingIndexes = new(StringComparer.Ordinal);
    private readonly Queue<BufferedReplayRecord> errorBuffer = new();
    private readonly List<TimestampedReplayRecord> pending = new();
    private bool recordingEnabled;
    private bool sessionSampled;
    private bool sessionErrorSampled;
    private bool forceRecordForError;
    private bool pendingHasFullSnapshot;
    private string pendingCreationReason = "incremental";
    private SessionReplayContext? pendingContext;
    private string? sampledSessionId;
    private int pendingEstimatedBytes;

    public SessionReplayManager(GuanceConfig config, ISessionReplayQueue queue, SessionReplayPrivacyOverrides privacyOverrides)
    {
        this.config = config;
        this.queue = queue;
        this.privacyOverrides = privacyOverrides;
        recordingEnabled = config.SessionReplay.Enabled;
    }

    public SessionReplayPrivacyOverrides PrivacyOverrides => privacyOverrides;

    public bool IsRecordingEnabled
    {
        get
        {
            lock (gate)
            {
                return recordingEnabled;
            }
        }
    }

    public bool HasReplay(string sessionId)
    {
        lock (gate)
        {
            EnsureSessionSampling(sessionId);
            return recordingEnabled && (sessionSampled || forceRecordForError);
        }
    }

    public void Start()
    {
        if (!config.SessionReplay.Enabled)
        {
            return;
        }

        lock (gate)
        {
            recordingEnabled = true;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            recordingEnabled = false;
        }

        _ = FlushPendingRecordsAsync(CancellationToken.None);
    }

    public void CaptureFullSnapshot(SessionReplayContext context, SessionReplayNode root)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var wireframes = BuildWireframes(root);
        LogFullSnapshotMapping(root, wireframes);
        var metaTimestamp = Math.Max(0, timestamp - 1);
        var metaRecord = new Dictionary<string, object?>
        {
            ["type"] = 4,
            ["timestamp"] = metaTimestamp,
            ["data"] = new Dictionary<string, object?>
            {
                ["width"] = ToReplayInt(root.Width),
                ["height"] = ToReplayInt(root.Height),
                ["href"] = string.Empty
            }
        };
        var fullSnapshotRecord = new Dictionary<string, object?>
        {
            ["type"] = 10,
            ["timestamp"] = timestamp,
            ["data"] = new Dictionary<string, object?>
            {
                ["wireframes"] = wireframes
            }
        };

        AddRecords(
            context,
            new[]
            {
                new PendingReplayRecord(metaRecord, metaTimestamp, HasFullSnapshot: false, CreationReason: "meta", CoalesceKey: null),
                new PendingReplayRecord(fullSnapshotRecord, timestamp, HasFullSnapshot: true, CreationReason: "full_snapshot", CoalesceKey: null)
            });
    }

    public void CaptureIncrementalEvent(SessionReplayContext context, string eventType, string? target, double x, double y, string? value = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new Dictionary<string, object?>
        {
            ["type"] = 11,
            ["timestamp"] = timestamp,
            ["data"] = BuildIncrementalData(eventType, target, x, y, timestamp, value)
        };
        LogIncrementalEvent(eventType, target, x, y, value, record);

        var coalesceKey = eventType is "input" or "selection" or "toggle"
            ? $"{eventType}:{target}"
            : null;
        AddRecord(context, record, timestamp, hasFullSnapshot: false, creationReason: "incremental", coalesceKey);
    }

    public void CaptureResizeEvent(SessionReplayContext context, string? target, double width, double height)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new Dictionary<string, object?>
        {
            ["type"] = 11,
            ["timestamp"] = timestamp,
            ["data"] = new Dictionary<string, object?>
            {
                ["source"] = 4,
                ["target"] = target,
                ["width"] = ToReplayInt(width),
                ["height"] = ToReplayInt(height)
            }
        };
        LogIncrementalEvent("resize", target, width, height, null, record);

        AddRecord(context, record, timestamp, hasFullSnapshot: false, creationReason: "incremental", coalesceKey: $"resize:{target}");
    }

    public void CaptureWebViewRecord(SessionReplayContext context, string slotId, JsonElement record)
    {
        if (string.IsNullOrWhiteSpace(slotId) ||
            record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("timestamp", out var timestampProperty) ||
            !timestampProperty.TryGetInt64(out var timestamp) ||
            timestamp <= 0 ||
            !record.TryGetProperty("type", out var typeProperty) ||
            !typeProperty.TryGetInt32(out var type))
        {
            return;
        }

        var enrichedRecord = AddWebViewSlotId(record, slotId);
        AddRecord(
            context,
            enrichedRecord,
            timestamp,
            hasFullSnapshot: type == 2,
            creationReason: type == 2 ? "webview_full_snapshot" : "webview_incremental");
    }

    public void CaptureViewEnd(SessionReplayContext context)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new Dictionary<string, object?>
        {
            ["type"] = 7,
            ["timestamp"] = timestamp,
            ["data"] = new Dictionary<string, object?>()
        };

        AddRecord(context, record, timestamp, hasFullSnapshot: false, creationReason: "view_end");
    }

    public void NotifyError(SessionReplayContext context)
    {
        lock (gate)
        {
            EnsureSessionSampling(context.SessionId);
            if (!sessionSampled && sessionErrorSampled)
            {
                forceRecordForError = true;
                while (errorBuffer.Count > 0)
                {
                    var buffered = errorBuffer.Dequeue();
                    AddPendingNoLock(buffered.Context, buffered.Record, buffered.TimestampMilliseconds, buffered.HasFullSnapshot, buffered.CreationReason, buffered.CoalesceKey);
                }
                if (!pendingHasFullSnapshot)
                {
                    pendingCreationReason = "error_replay";
                }
            }
        }

        _ = FlushPendingRecordsAsync(CancellationToken.None);
    }

    public async Task FlushPendingRecordsAsync(CancellationToken cancellationToken)
    {
        List<TimestampedReplayRecord> records;
        SessionReplayContext? context;
        bool hasFullSnapshot;
        string creationReason;
        int indexInView;
        long start;
        long end;

        lock (gate)
        {
            if (pending.Count == 0 || pendingContext is null)
            {
                return;
            }

            records = pending.ToList();
            context = pendingContext;
            hasFullSnapshot = pendingHasFullSnapshot;
            creationReason = pendingCreationReason;
            start = records.Min(item => item.TimestampMilliseconds);
            end = records.Max(item => item.TimestampMilliseconds);
            var viewKey = string.IsNullOrWhiteSpace(context.ViewId) ? context.SessionId : context.ViewId!;
            viewIndexes.TryGetValue(viewKey, out indexInView);
            viewIndexes[viewKey] = indexInView + 1;

            pending.Clear();
            coalescedPendingIndexes.Clear();
            pendingContext = null;
            pendingHasFullSnapshot = false;
            pendingCreationReason = "incremental";
            pendingEstimatedBytes = 0;
        }

        var builder = new SessionReplaySegmentBuilder(
            context,
            records.Select(item => item.Record).ToArray(),
            hasFullSnapshot,
            creationReason,
            indexInView,
            start,
            end);
        var (contentType, body) = builder.Build();
        await queue.EnqueueAsync(contentType, body, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushPendingRecordsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void AddRecord(SessionReplayContext context, object record, long timestampMilliseconds, bool hasFullSnapshot, string creationReason, string? coalesceKey = null)
    {
        AddRecords(
            context,
            new[] { new PendingReplayRecord(record, timestampMilliseconds, hasFullSnapshot, creationReason, coalesceKey) });
    }

    private void AddRecords(SessionReplayContext context, IReadOnlyList<PendingReplayRecord> records)
    {
        var shouldFlush = false;
        var shouldFlushFirst = false;
        lock (gate)
        {
            EnsureSessionSampling(context.SessionId);
            if (!recordingEnabled)
            {
                return;
            }

            if (sessionSampled || forceRecordForError)
            {
                if (pending.Count > 0 && pendingContext is not null && !SameReplayContext(pendingContext, context))
                {
                    shouldFlushFirst = true;
                }
                else
                {
                    foreach (var record in records)
                    {
                        shouldFlush |= AddPendingNoLock(
                            context,
                            record.Record,
                            record.TimestampMilliseconds,
                            record.HasFullSnapshot,
                            record.CreationReason,
                            record.CoalesceKey);
                    }
                }
            }
            else if (sessionErrorSampled)
            {
                foreach (var record in records)
                {
                    errorBuffer.Enqueue(new BufferedReplayRecord(
                        context,
                        record.Record,
                        record.TimestampMilliseconds,
                        record.HasFullSnapshot,
                        record.CreationReason,
                        record.CoalesceKey));
                }
                TrimErrorBufferNoLock(records[^1].TimestampMilliseconds);
            }
        }

        if (shouldFlushFirst)
        {
            _ = FlushPendingRecordsAsync(CancellationToken.None)
                .ContinueWith(_ => AddRecords(context, records),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            return;
        }

        if (shouldFlush)
        {
            _ = FlushPendingRecordsAsync(CancellationToken.None);
        }
    }

    private bool AddPendingNoLock(SessionReplayContext context, object record, long timestampMilliseconds, bool hasFullSnapshot, string creationReason, string? coalesceKey = null)
    {
        if (string.Equals(creationReason, "view_end", StringComparison.Ordinal) &&
            pendingContext is not null &&
            SameReplayContext(pendingContext, context) &&
            pending.Count > 0)
        {
            timestampMilliseconds = Math.Max(timestampMilliseconds, pending.Max(item => item.TimestampMilliseconds) + 1);
            if (record is IDictionary<string, object?> fields)
            {
                fields["timestamp"] = timestampMilliseconds;
            }
        }

        pendingContext = context;
        var coalesced = false;
        if (!string.IsNullOrWhiteSpace(coalesceKey) &&
            coalescedPendingIndexes.TryGetValue(coalesceKey, out var index) &&
            index >= 0 &&
            index < pending.Count)
        {
            if (IsWithinCoalesceWindow(timestampMilliseconds, pending[index].TimestampMilliseconds))
            {
                var estimatedBytes = EstimateRecordBytes(record);
                pendingEstimatedBytes = Math.Max(0, pendingEstimatedBytes - pending[index].EstimatedBytes + estimatedBytes);
                pending[index] = new TimestampedReplayRecord(record, timestampMilliseconds, coalesceKey, estimatedBytes);
                coalesced = true;
            }
        }

        if (!coalesced)
        {
            if (!string.IsNullOrWhiteSpace(coalesceKey))
            {
                coalescedPendingIndexes[coalesceKey] = pending.Count;
            }

            var estimatedBytes = EstimateRecordBytes(record);
            pendingEstimatedBytes += estimatedBytes + (pending.Count == 0 ? 2 : 1);
            pending.Add(new TimestampedReplayRecord(record, timestampMilliseconds, coalesceKey, estimatedBytes));
        }

        pendingHasFullSnapshot |= hasFullSnapshot;
        if (hasFullSnapshot)
        {
            pendingCreationReason = creationReason;
        }

        return pending.Count >= config.SessionReplay.SegmentRecordLimit || pendingEstimatedBytes >= config.SessionReplay.SegmentBytesLimit;
    }

    internal static bool IsWithinCoalesceWindow(long timestampMilliseconds, long previousTimestampMilliseconds)
    {
        if (timestampMilliseconds < previousTimestampMilliseconds)
        {
            return false;
        }

        var elapsedMilliseconds = (ulong)timestampMilliseconds - (ulong)previousTimestampMilliseconds;
        return elapsedMilliseconds <= (ulong)IncrementalCoalesceWindow.TotalMilliseconds;
    }

    private void EnsureSessionSampling(string sessionId)
    {
        if (string.Equals(sampledSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        sampledSessionId = sessionId;
        sessionSampled = Hit(config.SessionReplay.SampleRate);
        sessionErrorSampled = !sessionSampled && Hit(config.SessionReplay.OnErrorSampleRate);
        forceRecordForError = false;
        pending.Clear();
        coalescedPendingIndexes.Clear();
        errorBuffer.Clear();
        pendingEstimatedBytes = 0;
    }

    private bool Hit(double rate)
    {
        if (rate <= 0)
        {
            return false;
        }

        if (rate >= 1)
        {
            return true;
        }

        return random.NextDouble() <= rate;
    }

    private static bool SameReplayContext(SessionReplayContext left, SessionReplayContext right)
    {
        return string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
               string.Equals(left.ViewId, right.ViewId, StringComparison.Ordinal);
    }

    private static int EstimateRecordBytes(object record)
    {
        return JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
    }

    private void TrimErrorBufferNoLock(long nowMilliseconds)
    {
        var cutoff = nowMilliseconds - (long)ErrorBufferWindow.TotalMilliseconds;
        while (errorBuffer.Count > 0 && errorBuffer.Peek().TimestampMilliseconds < cutoff)
        {
            errorBuffer.Dequeue();
        }
    }

    private static List<object> BuildWireframes(SessionReplayNode root)
    {
        var wireframes = new List<object>();
        var reservedWebViewIds = new HashSet<long>();
        ReserveWebViewIds(root, reservedWebViewIds);
        long nextId = 1;
        AddWireframe(root, wireframes, reservedWebViewIds, ref nextId);
        return wireframes;
    }

    private static void ReserveWebViewIds(SessionReplayNode node, HashSet<long> reservedWebViewIds)
    {
        if (TryGetWebViewWireframeId(node, out var webViewId))
        {
            reservedWebViewIds.Add(webViewId);
        }

        if (node.Hidden)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            ReserveWebViewIds(child, reservedWebViewIds);
        }
    }

    private static void AddWireframe(
        SessionReplayNode node,
        List<object> wireframes,
        HashSet<long> reservedWebViewIds,
        ref long nextId)
    {
        wireframes.Add(BuildWireframe(node, reservedWebViewIds, ref nextId));
        if (node.Hidden)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AddWireframe(child, wireframes, reservedWebViewIds, ref nextId);
        }
    }

    private static Dictionary<string, object?> BuildWireframe(
        SessionReplayNode node,
        HashSet<long> reservedWebViewIds,
        ref long nextId)
    {
        var wireframeId = TryGetWebViewWireframeId(node, out var webViewId)
            ? webViewId
            : AllocateWireframeId(reservedWebViewIds, ref nextId);
        var result = new Dictionary<string, object?>
        {
            ["id"] = wireframeId,
            ["x"] = ToReplayInt(node.X),
            ["y"] = ToReplayInt(node.Y),
            ["width"] = ToReplayInt(node.Width),
            ["height"] = ToReplayInt(node.Height)
        };

        if (node.Hidden)
        {
            result["type"] = "placeholder";
            result["label"] = "Hidden";
            return result;
        }

        if (!string.IsNullOrWhiteSpace(node.WebViewSlotId))
        {
            result["type"] = "webview";
            result["slotId"] = node.WebViewSlotId;
            result["isVisible"] = node.WebViewIsVisible;
        }
        else if (string.Equals(node.TagName, "img", StringComparison.Ordinal))
        {
            result["type"] = "image";
            result["mimeType"] = string.IsNullOrWhiteSpace(node.ImageMimeType) ? "png" : node.ImageMimeType;
            result["isEmpty"] = node.ImageIsEmpty || string.IsNullOrWhiteSpace(node.ImageBase64);
            if (!string.IsNullOrWhiteSpace(node.ImageBase64))
            {
                result["base64"] = node.ImageBase64;
            }
        }
        else if (!string.IsNullOrEmpty(node.Text))
        {
            result["type"] = "text";
            result["text"] = node.Text;
            result["textStyle"] = new Dictionary<string, object?>
            {
                ["family"] = string.IsNullOrWhiteSpace(node.FontFamily) ? "Segoe UI" : node.FontFamily,
                ["size"] = node.FontSize is > 0 ? ToReplayInt(node.FontSize.Value) : 12,
                ["color"] = string.IsNullOrWhiteSpace(node.TextColor) ? "#000000FF" : node.TextColor
            };
            result["textPosition"] = new Dictionary<string, object?>
            {
                ["padding"] = new Dictionary<string, object?>
                {
                    ["top"] = ToReplayInt(node.PaddingTop),
                    ["bottom"] = ToReplayInt(node.PaddingBottom),
                    ["left"] = ToReplayInt(node.PaddingLeft),
                    ["right"] = ToReplayInt(node.PaddingRight)
                },
                ["alignment"] = new Dictionary<string, object?>
                {
                    ["horizontal"] = node.TextHorizontalAlignment,
                    ["vertical"] = node.TextVerticalAlignment
                }
            };
        }
        else
        {
            result["type"] = "shape";
        }

        result["shapeStyle"] = new Dictionary<string, object?>
        {
            ["backgroundColor"] = string.IsNullOrWhiteSpace(node.BackgroundColor) ? "#FFFFFF00" : node.BackgroundColor,
            ["opacity"] = Math.Clamp(node.Opacity, 0, 1),
            ["cornerRadius"] = ToReplayInt(node.CornerRadius)
        };

        if (!string.IsNullOrWhiteSpace(node.BorderColor) && node.BorderWidth > 0)
        {
            result["border"] = new Dictionary<string, object?>
            {
                ["color"] = node.BorderColor,
                ["width"] = Math.Max(1, ToReplayInt(node.BorderWidth))
            };
        }

        return result;
    }

    private static long AllocateWireframeId(HashSet<long> reservedWebViewIds, ref long nextId)
    {
        while (reservedWebViewIds.Contains(nextId))
        {
            nextId++;
        }

        return nextId++;
    }

    private static bool TryGetWebViewWireframeId(SessionReplayNode node, out long webViewId)
    {
        webViewId = 0;
        return !string.IsNullOrWhiteSpace(node.WebViewSlotId) &&
               long.TryParse(node.WebViewSlotId, out webViewId) &&
               webViewId > 0;
    }

    private static JsonElement AddWebViewSlotId(JsonElement record, string slotId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in record.EnumerateObject())
            {
                if (!string.Equals(property.Name, "slotId", StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteString("slotId", slotId);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static Dictionary<string, object?> BuildIncrementalData(string eventType, string? target, double x, double y, long timestampMilliseconds, string? value)
    {
        if (eventType == "click")
        {
            return new Dictionary<string, object?>
            {
                ["source"] = 2,
                ["target"] = target,
                ["positions"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = 0,
                        ["x"] = ToReplayInt(x),
                        ["y"] = ToReplayInt(y),
                        ["timestamp"] = timestampMilliseconds
                    }
                }
            };
        }

        var data = new Dictionary<string, object?>
        {
            ["source"] = 0,
            ["adds"] = Array.Empty<object>(),
            ["removes"] = Array.Empty<object>(),
            ["updates"] = Array.Empty<object>(),
            ["event_type"] = eventType,
            ["target"] = target,
            ["x"] = ToReplayInt(x),
            ["y"] = ToReplayInt(y)
        };
        if (value is not null)
        {
            data["value"] = value;
        }

        return data;
    }

    private static int ToReplayInt(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Max(0, (int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private void LogFullSnapshotMapping(SessionReplayNode root, IReadOnlyList<object> wireframes)
    {
        if (!SdkDiagnostics.IsEnabled)
        {
            return;
        }

        var message = $"[Guance.RUM.SessionReplay] full snapshot mapped root tag={root.TagName} text={Truncate(root.Text)} rect=({root.X:0.##},{root.Y:0.##},{root.Width:0.##},{root.Height:0.##}) wireframes={wireframes.Count}";
        SdkDiagnostics.WriteLine(message);
        LogNodeMapping(root, depth: 0);
    }

    private void LogNodeMapping(SessionReplayNode node, int depth)
    {
        if (!SdkDiagnostics.IsEnabled || depth > 8)
        {
            return;
        }

        var wireframeType = node.Hidden
            ? "placeholder"
            : string.IsNullOrEmpty(node.Text) ? "shape" : "text";
        var message = $"[Guance.RUM.SessionReplay] node->wireframe depth={depth} tag={node.TagName} type={wireframeType} text={Truncate(node.Text)} hidden={node.Hidden} rect=({node.X:0.##},{node.Y:0.##},{node.Width:0.##},{node.Height:0.##}) attrs={node.Attributes.Count} children={node.Children.Count}";
        SdkDiagnostics.WriteLine(message);

        foreach (var child in node.Children)
        {
            LogNodeMapping(child, depth + 1);
        }
    }

    private void LogIncrementalEvent(string eventType, string? target, double x, double y, string? value, object record)
    {
        if (!SdkDiagnostics.IsEnabled)
        {
            return;
        }

        var recordJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var message = $"[Guance.RUM.SessionReplay] incremental event={eventType} target={target ?? string.Empty} x={x:0.##} y={y:0.##} value={Truncate(value)} record={recordJson}";
        SdkDiagnostics.WriteLine(message);
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 80 ? value : value[..80] + "...";
    }

    private sealed record TimestampedReplayRecord(object Record, long TimestampMilliseconds, string? CoalesceKey, int EstimatedBytes);

    private sealed record PendingReplayRecord(
        object Record,
        long TimestampMilliseconds,
        bool HasFullSnapshot,
        string CreationReason,
        string? CoalesceKey);

    private sealed record BufferedReplayRecord(
        SessionReplayContext Context,
        object Record,
        long TimestampMilliseconds,
        bool HasFullSnapshot,
        string CreationReason,
        string? CoalesceKey);
}
