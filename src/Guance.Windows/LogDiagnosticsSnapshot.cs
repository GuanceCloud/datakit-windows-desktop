namespace Guance.Windows;

/// <summary>Represents an immutable snapshot of log queue and upload counters.</summary>
/// <param name="Timestamp">Time at which the snapshot was captured.</param>
/// <param name="LogsEnqueued">Accepted log count.</param>
/// <param name="LogsDroppedByConfiguration">Logs rejected because custom logging is disabled.</param>
/// <param name="LogsDroppedBySampling">Logs rejected by sampling.</param>
/// <param name="LogsDroppedByLevel">Logs rejected by the level filter.</param>
/// <param name="LogsDroppedByCapacity">Logs rejected or evicted because the queue was full.</param>
/// <param name="UploadSuccessCount">Successful log upload count.</param>
/// <param name="UploadRetryCount">Retried log upload count.</param>
/// <param name="UploadTerminalFailureCount">Terminal log upload failure count.</param>
/// <param name="LastUploadStatusCode">Last Logging intake HTTP status code.</param>
/// <param name="LastUploadError">Last log upload error, if any.</param>
public sealed record LogDiagnosticsSnapshot(
    DateTimeOffset Timestamp,
    long LogsEnqueued,
    long LogsDroppedByConfiguration,
    long LogsDroppedBySampling,
    long LogsDroppedByLevel,
    long LogsDroppedByCapacity,
    long UploadSuccessCount,
    long UploadRetryCount,
    long UploadTerminalFailureCount,
    long LastUploadStatusCode,
    string? LastUploadError);
