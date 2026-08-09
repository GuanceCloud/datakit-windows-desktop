namespace Guance.Windows;

/// <summary>
/// Controls the SDK's shared on-disk cache.
/// </summary>
public sealed class CacheOptions
{
    internal const long ReplayEnvelopeAllowanceBytes = 64L * 1024;

    /// <summary>
    /// Hard upper bound for all RUM, log, and Session Replay cache files combined.
    /// </summary>
    public long MaxDiskBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>
    /// Target utilization after an eviction pass. This avoids evicting on every append.
    /// </summary>
    public double LowWatermarkRatio { get; init; } = 0.9;

    /// <summary>
    /// Maximum number of active, ready, and in-flight batch files.
    /// </summary>
    public int MaxFiles { get; init; } = 1_024;

    /// <summary>
    /// Maximum age of an upload batch before it is discarded.
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum number of line-protocol records in one RUM or log upload batch.
    /// </summary>
    public int MaxBatchItems { get; init; } = 50;

    /// <summary>
    /// Maximum uncompressed payload bytes in one RUM or log upload batch.
    /// </summary>
    public long MaxBatchBytes { get; init; } = 512L * 1024;

    /// <summary>
    /// Soft cache shares. Streams may borrow unused space; eviction prefers streams
    /// currently above their share.
    /// </summary>
    public double RumShare { get; init; } = 0.25;

    /// <summary>Soft share of the cache reserved for log batches.</summary>
    public double LogShare { get; init; } = 0.15;

    /// <summary>Soft share of the cache reserved for Session Replay segments.</summary>
    public double SessionReplayShare { get; init; } = 0.60;
}
