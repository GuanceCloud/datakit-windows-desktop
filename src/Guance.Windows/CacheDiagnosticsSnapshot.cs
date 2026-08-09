namespace Guance.Windows;

/// <summary>
/// Allocation-aware usage of the cache shared by all telemetry streams.
/// </summary>
/// <param name="Timestamp">Time at which the snapshot was captured.</param>
/// <param name="AllocatedBytes">Allocation-aware bytes used by telemetry files.</param>
/// <param name="MaxDiskBytes">Configured shared cache byte limit.</param>
/// <param name="FileCount">Current telemetry file count.</param>
/// <param name="MaxFiles">Configured shared cache file-count limit.</param>
public sealed record CacheDiagnosticsSnapshot(
    DateTimeOffset Timestamp,
    long AllocatedBytes,
    long MaxDiskBytes,
    int FileCount,
    int MaxFiles)
{
    /// <summary>Gets the fraction of the configured disk limit currently allocated.</summary>
    public double UsageRatio => MaxDiskBytes <= 0
        ? 0
        : (double)AllocatedBytes / MaxDiskBytes;
}
