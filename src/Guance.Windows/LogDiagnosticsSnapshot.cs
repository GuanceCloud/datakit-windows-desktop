namespace Guance.Windows;

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
