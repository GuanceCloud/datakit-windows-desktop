namespace Guance.Windows;

/// <summary>
/// Allocation-aware usage of the cache shared by all telemetry streams.
/// </summary>
public sealed record CacheDiagnosticsSnapshot(
    DateTimeOffset Timestamp,
    long AllocatedBytes,
    long MaxDiskBytes,
    int FileCount,
    int MaxFiles)
{
    public double UsageRatio => MaxDiskBytes <= 0
        ? 0
        : (double)AllocatedBytes / MaxDiskBytes;
}
