namespace Guance.Rum.Windows;

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
