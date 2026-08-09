namespace Guance.Windows;

/// <summary>Represents an immutable snapshot of RUM, Replay, queue, and upload state.</summary>
/// <param name="Timestamp">Time at which the snapshot was captured.</param>
/// <param name="SessionId">Current RUM session identifier.</param>
/// <param name="SessionSampled">Whether normal RUM sampling selected the session.</param>
/// <param name="SessionErrorSampled">Whether error-session sampling selected the session.</param>
/// <param name="HasActiveView">Whether a View is active.</param>
/// <param name="ActiveActionCount">Number of active scoped Actions.</param>
/// <param name="ActiveResourceCount">Number of active Resources.</param>
/// <param name="RumEventsEnqueued">Accepted RUM event count.</param>
/// <param name="RumEventsDroppedBySampling">RUM events rejected by sampling.</param>
/// <param name="RumUploadSuccessCount">Successful RUM upload count.</param>
/// <param name="RumUploadRetryCount">Retried RUM upload count.</param>
/// <param name="RumUploadTerminalFailureCount">Terminal RUM upload failure count.</param>
/// <param name="ReplayUploadSuccessCount">Successful Replay upload count.</param>
/// <param name="ReplayUploadRetryCount">Retried Replay upload count.</param>
/// <param name="ReplayUploadTerminalFailureCount">Terminal Replay upload failure count.</param>
/// <param name="QueueErrorCount">Persistent queue error count.</param>
/// <param name="LastRumUploadStatusCode">Last RUM intake HTTP status code.</param>
/// <param name="LastReplayUploadStatusCode">Last Replay intake HTTP status code.</param>
/// <param name="LastRumUploadError">Last RUM upload error, if any.</param>
/// <param name="LastReplayUploadError">Last Replay upload error, if any.</param>
/// <param name="LastQueueError">Last persistent queue error, if any.</param>
public sealed record RumDiagnosticsSnapshot(
    DateTimeOffset Timestamp,
    string SessionId,
    bool SessionSampled,
    bool SessionErrorSampled,
    bool HasActiveView,
    int ActiveActionCount,
    int ActiveResourceCount,
    long RumEventsEnqueued,
    long RumEventsDroppedBySampling,
    long RumUploadSuccessCount,
    long RumUploadRetryCount,
    long RumUploadTerminalFailureCount,
    long ReplayUploadSuccessCount,
    long ReplayUploadRetryCount,
    long ReplayUploadTerminalFailureCount,
    long QueueErrorCount,
    long LastRumUploadStatusCode,
    long LastReplayUploadStatusCode,
    string? LastRumUploadError,
    string? LastReplayUploadError,
    string? LastQueueError);
