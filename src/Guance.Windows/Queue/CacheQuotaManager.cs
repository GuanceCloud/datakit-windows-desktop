using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace Guance.Windows.Queue;

internal sealed class CacheQuotaManager
{
    private static readonly ConcurrentDictionary<string, CacheQuotaManager> Managers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object gate = new();
    private readonly string cacheRoot;
    private readonly CacheOptions options;
    private readonly long allocationUnit;
    private readonly Dictionary<string, long> allocations = new(StringComparer.OrdinalIgnoreCase);
    private long allocatedBytes;

    private CacheQuotaManager(string cacheRoot, CacheOptions options)
    {
        this.cacheRoot = cacheRoot;
        this.options = options;
        Directory.CreateDirectory(cacheRoot);
        allocationUnit = GetAllocationUnit(cacheRoot);
        ReconcileNoLock();
    }

    public static CacheQuotaManager GetOrCreate(string cacheRoot, CacheOptions options)
    {
        var normalized = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Managers.GetOrAdd(normalized, path => new CacheQuotaManager(path, options));
    }

    public bool TryReserve(
        string path,
        long projectedLength,
        BatchStreamKind requestingStream,
        CacheAdmissionPolicy policy)
    {
        lock (gate)
        {
            var normalized = Path.GetFullPath(path);
            allocations.TryGetValue(normalized, out var previous);
            var projected = EstimateAllocation(projectedLength);
            var delta = projected - previous;
            var newFile = previous == 0;
            if (delta <= 0)
            {
                allocations[normalized] = projected;
                allocatedBytes += delta;
                return true;
            }

            if ((allocatedBytes + delta > options.MaxDiskBytes ||
                 (newFile && allocations.Count + 1 > options.MaxFiles)) &&
                policy == CacheAdmissionPolicy.DiscardOldest)
            {
                EvictNoLock(delta, newFile, requestingStream);
            }

            if (allocatedBytes + delta > options.MaxDiskBytes ||
                (newFile && allocations.Count + 1 > options.MaxFiles))
            {
                return false;
            }

            allocations[normalized] = projected;
            allocatedBytes += delta;
            return true;
        }
    }

    public void Rollback(string path, long actualLength)
    {
        lock (gate)
        {
            var normalized = Path.GetFullPath(path);
            allocations.TryGetValue(normalized, out var previous);
            if (actualLength <= 0)
            {
                allocations.Remove(normalized);
                allocatedBytes -= previous;
            }
            else
            {
                var actual = EstimateAllocation(actualLength);
                allocations[normalized] = actual;
                allocatedBytes += actual - previous;
            }
        }
    }

    public void Move(string source, string destination)
    {
        lock (gate)
        {
            var sourcePath = Path.GetFullPath(source);
            var destinationPath = Path.GetFullPath(destination);
            if (allocations.Remove(sourcePath, out var allocation))
            {
                allocations[destinationPath] = allocation;
            }
            else if (File.Exists(destinationPath))
            {
                var measured = EstimateAllocation(new FileInfo(destinationPath).Length);
                allocations[destinationPath] = measured;
                allocatedBytes += measured;
            }
        }
    }

    public void Release(string path)
    {
        lock (gate)
        {
            var normalized = Path.GetFullPath(path);
            if (allocations.Remove(normalized, out var allocation))
            {
                allocatedBytes -= allocation;
            }
        }
    }

    public CacheUsageSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new CacheUsageSnapshot(Math.Max(0, allocatedBytes), allocations.Count);
        }
    }

    public void Trim()
    {
        lock (gate)
        {
            if (allocatedBytes > options.MaxDiskBytes || allocations.Count > options.MaxFiles)
            {
                EvictNoLock(0, requiresFile: false, BatchStreamKind.Rum);
            }
        }
    }

    private void EvictNoLock(long requiredBytes, bool requiresFile, BatchStreamKind requestingStream)
    {
        var lowWatermark = (long)(options.MaxDiskBytes * options.LowWatermarkRatio);
        var targetBytes = Math.Max(0, lowWatermark - requiredBytes);
        var targetFiles = requiresFile ? options.MaxFiles - 1 : options.MaxFiles;
        var streamBytes = allocations
            .GroupBy(item => GetStream(item.Key))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Value));

        var candidates = allocations
            .Where(item => IsReadyBatch(item.Key))
            .Select(item => new
            {
                Path = item.Key,
                Bytes = item.Value,
                Stream = GetStream(item.Key),
                CreatedAt = File.GetLastWriteTimeUtc(item.Key)
            })
            .OrderByDescending(item => IsAboveSoftShare(item.Stream, streamBytes))
            .ThenByDescending(item => item.Stream == requestingStream)
            .ThenBy(item => item.CreatedAt)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (allocatedBytes <= targetBytes && allocations.Count <= targetFiles)
            {
                break;
            }

            try
            {
                File.Delete(candidate.Path);
                if (!File.Exists(candidate.Path) && allocations.Remove(candidate.Path))
                {
                    allocatedBytes -= candidate.Bytes;
                }
            }
            catch (IOException)
            {
                // A concurrent lease may have moved the file. The caller will reject
                // the append if enough capacity still cannot be established.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat an undeletable file as occupied capacity.
            }
        }
    }

    private bool IsAboveSoftShare(BatchStreamKind stream, IReadOnlyDictionary<BatchStreamKind, long> streamBytes)
    {
        streamBytes.TryGetValue(stream, out var used);
        var share = stream switch
        {
            BatchStreamKind.Rum => options.RumShare,
            BatchStreamKind.Log => options.LogShare,
            BatchStreamKind.SessionReplay => options.SessionReplayShare,
            _ => 1
        };
        return used > options.MaxDiskBytes * share;
    }

    private void ReconcileNoLock()
    {
        allocations.Clear();
        allocatedBytes = 0;
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(".batch", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var allocation = EstimateAllocation(new FileInfo(path).Length);
                allocations[Path.GetFullPath(path)] = allocation;
                allocatedBytes += allocation;
            }
            catch (IOException)
            {
                // A later append/lease will reconcile the affected path.
            }
        }
    }

    private long EstimateAllocation(long length)
    {
        if (length <= 0)
        {
            return 0;
        }

        return checked(((length + allocationUnit - 1) / allocationUnit) * allocationUnit);
    }

    private static bool IsReadyBatch(string path) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "ready", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".batch", StringComparison.OrdinalIgnoreCase);

    private static BatchStreamKind GetStream(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = parts.Length - 1; index >= 0; index--)
        {
            if (parts[index].Equals("rum", StringComparison.OrdinalIgnoreCase)) return BatchStreamKind.Rum;
            if (parts[index].Equals("logs", StringComparison.OrdinalIgnoreCase)) return BatchStreamKind.Log;
            if (parts[index].Equals("replay", StringComparison.OrdinalIgnoreCase)) return BatchStreamKind.SessionReplay;
        }

        return BatchStreamKind.Rum;
    }

    private static long GetAllocationUnit(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 4_096;
        }

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) &&
               GetDiskFreeSpace(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _)
            ? Math.Max(1, (long)sectorsPerCluster * bytesPerSector)
            : 4_096;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpace(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);
}
