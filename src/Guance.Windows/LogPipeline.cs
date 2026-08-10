using System.Text;
using Guance.Windows.Queue;
using Guance.Windows.Transport;

namespace Guance.Windows;

internal sealed class LogPipeline : IAsyncDisposable
{
    private const int MaxContentBytes = 30 * 1024;
    private readonly GuanceConfig config;
    private readonly ILogQueue queue;
    private readonly ILogTransport transport;
    private readonly Func<string, string, IReadOnlyDictionary<string, object?>?, LogEvent> createEvent;
    private readonly Action<RumDiagnosticEvent> reportDiagnostic;
    private readonly CancellationToken shutdownToken;
    private readonly Action notifyUploadReady;
    private readonly UploadRateLimiter uploadRateLimiter;
    private readonly object queueWriteGate = new();
    private readonly HashSet<Task> pendingQueueWrites = new();
    private readonly RetryBackoff retryBackoff;
    private long logsEnqueued;
    private long logsDroppedByConfiguration;
    private long logsDroppedBySampling;
    private long logsDroppedByLevel;
    private long logsDroppedByCapacity;
    private long uploadSuccessCount;
    private long uploadRetryCount;
    private long uploadTerminalFailureCount;
    private long lastUploadStatusCode;
    private string? lastUploadError;

    public LogPipeline(
        GuanceConfig config,
        ILogQueue queue,
        ILogTransport transport,
        Func<string, string, IReadOnlyDictionary<string, object?>?, LogEvent> createEvent,
        Action<RumDiagnosticEvent> reportDiagnostic,
        CancellationToken shutdownToken,
        UploadRateLimiter uploadRateLimiter,
        Action notifyUploadReady)
    {
        this.config = config;
        this.queue = queue;
        this.transport = transport;
        this.createEvent = createEvent;
        this.reportDiagnostic = reportDiagnostic;
        this.shutdownToken = shutdownToken;
        this.uploadRateLimiter = uploadRateLimiter;
        this.notifyUploadReady = notifyUploadReady;
        retryBackoff = new RetryBackoff(config.FlushInterval);
    }

    public void AddLog(
        string content,
        string status,
        IReadOnlyDictionary<string, object?>? properties)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Log status is required.", nameof(status));
        }

        if (!config.Logging.EnableCustomLog)
        {
            Interlocked.Increment(ref logsDroppedByConfiguration);
            Report(RumDiagnosticLevel.Debug, "log_configuration", "Dropped custom log because logging is disabled.");
            return;
        }

        var normalizedStatus = status.Trim().ToLowerInvariant();
        if (!ShouldCollectLevel(normalizedStatus))
        {
            Interlocked.Increment(ref logsDroppedByLevel);
            Report(RumDiagnosticLevel.Debug, "log_level", $"Dropped {normalizedStatus} log by level filter.");
            return;
        }

        if (config.Logging.SampleRate <= 0 ||
            (config.Logging.SampleRate < 1 && Random.Shared.NextDouble() >= config.Logging.SampleRate))
        {
            Interlocked.Increment(ref logsDroppedBySampling);
            Report(RumDiagnosticLevel.Debug, "log_sampling", $"Dropped {normalizedStatus} log by sampling.");
            return;
        }

        var logEvent = createEvent(TruncateUtf8(content, MaxContentBytes), normalizedStatus, properties);
        TelemetryModifierPipeline.Apply(logEvent, config);
        var line = LineProtocolFormatter.Format(logEvent);
        try
        {
            TrackQueueWrite(queue.EnqueueAsync(line, shutdownToken));
        }
        catch (Exception ex)
        {
            Report(RumDiagnosticLevel.Error, "log_queue", ex.Message, exception: ex);
        }
    }

    public LogDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        return new LogDiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            Interlocked.Read(ref logsEnqueued),
            Interlocked.Read(ref logsDroppedByConfiguration),
            Interlocked.Read(ref logsDroppedBySampling),
            Interlocked.Read(ref logsDroppedByLevel),
            Interlocked.Read(ref logsDroppedByCapacity),
            Interlocked.Read(ref uploadSuccessCount),
            Interlocked.Read(ref uploadRetryCount),
            Interlocked.Read(ref uploadTerminalFailureCount),
            Interlocked.Read(ref lastUploadStatusCode),
            Volatile.Read(ref lastUploadError));
    }

    public async Task SealAsync(bool force, CancellationToken cancellationToken)
    {
        if (!config.Logging.EnableCustomLog) return;
        await WaitForPendingQueueWritesAsync(cancellationToken).ConfigureAwait(false);
        if (force)
        {
            await queue.SealAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await queue.SealIfOlderAsync(config.FlushInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        transport.Dispose();
        await queue.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<UploadAttemptStatus> TryUploadOneAsync(CancellationToken cancellationToken)
    {
        if (!config.Logging.EnableCustomLog)
        {
            return UploadAttemptStatus.Empty;
        }

        await WaitForPendingQueueWritesAsync(cancellationToken).ConfigureAwait(false);
        if (!retryBackoff.CanAttemptNow())
        {
            return UploadAttemptStatus.RetryLater;
        }

        var batch = await queue.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            retryBackoff.Reset();
            return UploadAttemptStatus.Empty;
        }

        try
        {
            await uploadRateLimiter.WaitAsync(
                config.CompressIntakeRequests
                    ? UploadSizeEstimator.EstimateDeflateUpperBound(batch.PayloadBytes)
                    : batch.PayloadBytes,
                cancellationToken).ConfigureAwait(false);
            var result = await transport.SendAsync(batch.Items, cancellationToken).ConfigureAwait(false);
            RecordUploadResult(result);

            if (result.DeleteFromQueue)
            {
                retryBackoff.Reset();
                await queue.CompleteAsync(batch.LeaseId, cancellationToken).ConfigureAwait(false);
                return UploadAttemptStatus.Consumed;
            }

            await queue.AbandonAsync(batch.LeaseId, cancellationToken).ConfigureAwait(false);
            if (result.RetryLater) retryBackoff.ScheduleRetry(result.RetryAfter);
            return UploadAttemptStatus.RetryLater;
        }
        catch
        {
            await queue.AbandonAsync(batch.LeaseId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void TrackQueueWrite(Task<BatchAppendResult> enqueueTask)
    {
        lock (queueWriteGate)
        {
            pendingQueueWrites.Add(enqueueTask);
        }

        _ = enqueueTask.ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                var exception = task.Exception?.GetBaseException() ?? new InvalidOperationException("Log queue enqueue failed.");
                Report(RumDiagnosticLevel.Error, "log_queue", exception.Message, exception: exception);
            }
            else if (task.IsCanceled)
            {
                var exception = new OperationCanceledException("Log queue enqueue was canceled.");
                Report(RumDiagnosticLevel.Error, "log_queue", exception.Message, exception: exception);
            }
            else if (task.Result.Accepted)
            {
                Interlocked.Increment(ref logsEnqueued);
            }
            else
            {
                Interlocked.Increment(ref logsDroppedByCapacity);
                Report(RumDiagnosticLevel.Warning, "log_queue", "Dropped log because the logging queue is full.");
            }

            lock (queueWriteGate)
            {
                pendingQueueWrites.Remove(task);
            }

            if (task.Status == TaskStatus.RanToCompletion && task.Result.BatchReady)
            {
                notifyUploadReady();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForPendingQueueWritesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] pending;
            lock (queueWriteGate)
            {
                pending = pendingQueueWrites.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Enqueue failures are reported by TrackQueueWrite.
            }
        }
    }

    private void RecordUploadResult(SendResult result)
    {
        Interlocked.Exchange(ref lastUploadStatusCode, result.StatusCode ?? 0);
        if (result.RetryLater)
        {
            Volatile.Write(ref lastUploadError, result.ErrorMessage);
            Interlocked.Increment(ref uploadRetryCount);
            Report(RumDiagnosticLevel.Warning, "log_upload", result.ErrorMessage ?? "Log upload will retry.", result.StatusCode);
        }
        else if (result.StatusCode is >= 200 and < 300)
        {
            Volatile.Write(ref lastUploadError, null);
            Interlocked.Increment(ref uploadSuccessCount);
            Report(RumDiagnosticLevel.Debug, "log_upload", "Log upload succeeded.", result.StatusCode);
        }
        else if (result.DeleteFromQueue)
        {
            Volatile.Write(ref lastUploadError, result.ErrorMessage);
            Interlocked.Increment(ref uploadTerminalFailureCount);
            Report(RumDiagnosticLevel.Error, "log_upload", result.ErrorMessage ?? "Log upload terminal failure.", result.StatusCode);
        }
    }

    private bool ShouldCollectLevel(string status)
    {
        var filters = config.Logging.LevelFilters;
        return filters is null || filters.Count == 0 || filters.Any(item =>
            string.Equals(LogStatusNames.ToProtocolValue(item), status, StringComparison.OrdinalIgnoreCase));
    }

    private void Report(
        RumDiagnosticLevel level,
        string source,
        string message,
        int? statusCode = null,
        Exception? exception = null)
    {
        reportDiagnostic(new RumDiagnosticEvent(
            DateTimeOffset.UtcNow,
            level,
            source,
            message,
            statusCode,
            exception));
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= maxBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0 && char.IsHighSurrogate(value[low - 1]))
        {
            low--;
        }

        return value[..low];
    }
}
