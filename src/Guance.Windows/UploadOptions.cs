namespace Guance.Windows;

/// <summary>
/// Controls aggregate upload traffic across RUM, logging, and Session Replay.
/// </summary>
public sealed class UploadOptions
{
    /// <summary>
    /// Maximum request-body bytes sent per second across all telemetry streams.
    /// Set to zero for no byte-rate limit.
    /// </summary>
    public long MaxBytesPerSecond { get; init; } = 256L * 1024;

    /// <summary>
    /// Maximum byte burst available to the shared token bucket.
    /// </summary>
    public long BurstBytes { get; init; } = 2L * 1024 * 1024;

    /// <summary>
    /// Maximum requests per second across all telemetry streams.
    /// Set to zero for no request-rate limit.
    /// </summary>
    public double MaxRequestsPerSecond { get; init; } = 2;

    /// <summary>
    /// Maximum number of HTTP uploads in flight. The first implementation serializes
    /// cache leases and therefore supports one concurrent request.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 1;

    /// <summary>
    /// Maximum number of batches sent by one scheduled drain cycle.
    /// </summary>
    public int MaxBatchesPerCycle { get; init; } = 4;

    /// <summary>Gets the relative scheduling weight for RUM uploads.</summary>
    public int RumWeight { get; init; } = 4;
    /// <summary>Gets the relative scheduling weight for log uploads.</summary>
    public int LogWeight { get; init; } = 2;
    /// <summary>Gets the relative scheduling weight for Session Replay uploads.</summary>
    public int SessionReplayWeight { get; init; } = 1;
}
