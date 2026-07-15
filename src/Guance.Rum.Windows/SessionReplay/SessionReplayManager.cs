using System.Text.Json;

namespace Guance.Rum.Windows.SessionReplay;

internal sealed class SessionReplayManager : IAsyncDisposable
{
    private static readonly TimeSpan ErrorBufferWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IncrementalCoalesceWindow = TimeSpan.FromMilliseconds(200);
    private readonly RumConfig config;
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

    public SessionReplayManager(RumConfig config, ISessionReplayQueue queue, SessionReplayPrivacyOverrides privacyOverrides)
    {
        this.config = config;
        this.queue = queue;
        this.privacyOverrides = privacyOverrides;
        recordingEnabled = config.SessionReplay.Enabled;
    }

    public SessionReplayPrivacyOverrides PrivacyOverrides => privacyOverrides;

    public void Start()
    {
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
        var record = new Dictionary<string, object?>
        {
            ["type"] = 2,
            ["timestamp"] = timestamp,
            ["data"] = new Dictionary<string, object?>
            {
                ["node"] = BuildDocumentNode(root),
                ["initialOffset"] = new Dictionary<string, object?> { ["left"] = 0, ["top"] = 0 }
            }
        };

        AddRecord(context, record, timestamp, hasFullSnapshot: true, creationReason: "full_snapshot");
    }

    public void CaptureIncrementalEvent(SessionReplayContext context, string eventType, string? target, double x, double y, string? value = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var data = new Dictionary<string, object?>
        {
            ["source"] = eventType,
            ["target"] = target,
            ["x"] = x,
            ["y"] = y
        };
        if (value is not null)
        {
            data["value"] = value;
        }

        var record = new Dictionary<string, object?>
        {
            ["type"] = 3,
            ["timestamp"] = timestamp,
            ["data"] = data
        };

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
            ["type"] = 3,
            ["timestamp"] = timestamp,
            ["data"] = new Dictionary<string, object?>
            {
                ["source"] = "resize",
                ["target"] = target,
                ["width"] = width,
                ["height"] = height
            }
        };

        AddRecord(context, record, timestamp, hasFullSnapshot: false, creationReason: "incremental", coalesceKey: $"resize:{target}");
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
                    shouldFlush = AddPendingNoLock(context, record, timestampMilliseconds, hasFullSnapshot, creationReason, coalesceKey);
                }
            }
            else if (sessionErrorSampled)
            {
                errorBuffer.Enqueue(new BufferedReplayRecord(context, record, timestampMilliseconds, hasFullSnapshot, creationReason, coalesceKey));
                TrimErrorBufferNoLock(timestampMilliseconds);
            }
        }

        if (shouldFlushFirst)
        {
            _ = FlushPendingRecordsAsync(CancellationToken.None)
                .ContinueWith(_ => AddRecord(context, record, timestampMilliseconds, hasFullSnapshot, creationReason, coalesceKey),
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

    private static Dictionary<string, object?> BuildDocumentNode(SessionReplayNode root)
    {
        var body = new SessionReplayNode("body", 0, 0, root.Width, root.Height);
        body.Children.Add(root);
        var html = new SessionReplayNode("html", 0, 0, root.Width, root.Height);
        html.Children.Add(body);
        var id = 1;
        return BuildNode(html, ref id);
    }

    private static Dictionary<string, object?> BuildNode(SessionReplayNode node, ref int id)
    {
        var attributes = new Dictionary<string, string>(node.Attributes, StringComparer.Ordinal)
        {
            ["style"] = BuildStyle(node)
        };

        if (node.Hidden)
        {
            attributes["data-guance-hidden"] = "true";
        }

        var result = new Dictionary<string, object?>
        {
            ["type"] = 2,
            ["id"] = id++,
            ["tagName"] = node.TagName,
            ["attributes"] = attributes,
            ["childNodes"] = new List<object>()
        };

        var children = (List<object>)result["childNodes"]!;
        if (node.Hidden)
        {
            children.Add(new Dictionary<string, object?> { ["type"] = 3, ["id"] = id++, ["textContent"] = "Hidden" });
            return result;
        }

        if (!string.IsNullOrEmpty(node.Text))
        {
            children.Add(new Dictionary<string, object?> { ["type"] = 3, ["id"] = id++, ["textContent"] = node.Text });
        }

        foreach (var child in node.Children)
        {
            children.Add(BuildNode(child, ref id));
        }

        return result;
    }

    private static string BuildStyle(SessionReplayNode node)
    {
        return FormattableString.Invariant($"position:absolute;left:{node.X:0.##}px;top:{node.Y:0.##}px;width:{node.Width:0.##}px;height:{node.Height:0.##}px;box-sizing:border-box;overflow:hidden;");
    }

    private sealed record TimestampedReplayRecord(object Record, long TimestampMilliseconds, string? CoalesceKey, int EstimatedBytes);

    private sealed record BufferedReplayRecord(
        SessionReplayContext Context,
        object Record,
        long TimestampMilliseconds,
        bool HasFullSnapshot,
        string CreationReason,
        string? CoalesceKey);
}
