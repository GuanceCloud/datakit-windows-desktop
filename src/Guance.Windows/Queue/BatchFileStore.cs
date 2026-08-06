using System.IO;
using System.Text;

namespace Guance.Windows.Queue;

internal sealed class BatchFileStore : IAsyncDisposable
{
    private readonly BatchStreamKind kind;
    private readonly string contentType;
    private readonly bool lineDelimited;
    private readonly CacheOptions options;
    private readonly CacheAdmissionPolicy admissionPolicy;
    private readonly long maximumPayloadBytes;
    private readonly CacheQuotaManager quota;
    private readonly string activeDirectory;
    private readonly string readyDirectory;
    private readonly string sendingDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? activeStream;
    private string? activePath;
    private DateTimeOffset activeCreatedAt;
    private int activeRecordCount;
    private long activePayloadBytes;
    private BatchCrc32 activeChecksum;
    private bool disposed;

    public BatchFileStore(
        GuanceConfig config,
        BatchStreamKind kind,
        string contentType,
        bool lineDelimited,
        CacheAdmissionPolicy admissionPolicy)
    {
        this.kind = kind;
        this.contentType = contentType;
        this.lineDelimited = lineDelimited;
        this.admissionPolicy = admissionPolicy;
        options = config.Cache;
        maximumPayloadBytes = lineDelimited
            ? options.MaxBatchBytes
            : checked(config.SessionReplay.SegmentBytesLimit + CacheOptions.ReplayEnvelopeAllowanceBytes);

        var cacheRoot = Path.Combine(CachePath.GetRoot(config), "v1");
        var streamName = kind switch
        {
            BatchStreamKind.Rum => "rum",
            BatchStreamKind.Log => "logs",
            BatchStreamKind.SessionReplay => "replay",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var streamRoot = Path.Combine(cacheRoot, streamName);
        activeDirectory = Path.Combine(streamRoot, "active");
        readyDirectory = Path.Combine(streamRoot, "ready");
        sendingDirectory = Path.Combine(streamRoot, "sending");
        Directory.CreateDirectory(activeDirectory);
        Directory.CreateDirectory(readyDirectory);
        Directory.CreateDirectory(sendingDirectory);
        quota = CacheQuotaManager.GetOrCreate(cacheRoot, options);
        Recover();
        quota.Trim();
    }

    public async Task<BatchAppendResult> AppendLineAsync(string line, CancellationToken cancellationToken)
    {
        if (!lineDelimited)
        {
            throw new InvalidOperationException("This batch store does not accept line records.");
        }

        if (string.IsNullOrEmpty(line))
        {
            return BatchAppendResult.Rejected;
        }

        if (!line.EndsWith("\n", StringComparison.Ordinal))
        {
            line += "\n";
        }

        var payload = Encoding.UTF8.GetBytes(line);
        if (payload.LongLength > options.MaxBatchBytes)
        {
            return BatchAppendResult.Rejected;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var batchReady = false;
            if (activeRecordCount > 0 &&
                (activeRecordCount >= options.MaxBatchItems || activePayloadBytes + payload.LongLength > options.MaxBatchBytes))
            {
                batchReady = await SealNoLockAsync(cancellationToken).ConfigureAwait(false);
            }

            if (activeStream is null)
            {
                if (!await CreateActiveNoLockAsync(payload, cancellationToken).ConfigureAwait(false))
                {
                    return BatchAppendResult.Rejected;
                }
            }
            else
            {
                var previousLength = BatchFileFormat.HeaderSize + activePayloadBytes;
                var projectedLength = previousLength + payload.LongLength;
                if (!quota.TryReserve(activePath!, projectedLength, kind, admissionPolicy))
                {
                    return BatchAppendResult.Rejected;
                }

                try
                {
                    activeStream.Position = previousLength;
                    await activeStream.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        activeStream.SetLength(previousLength);
                        activeStream.Position = previousLength;
                    }
                    catch
                    {
                        // Preserve the original write failure. Startup recovery
                        // will repair or discard the interrupted active file.
                    }
                    quota.Rollback(activePath!, previousLength);
                    throw;
                }
            }

            activeRecordCount++;
            activePayloadBytes += payload.LongLength;
            activeChecksum.Append(payload);

            if (activeRecordCount >= options.MaxBatchItems || activePayloadBytes >= options.MaxBatchBytes)
            {
                batchReady |= await SealNoLockAsync(cancellationToken).ConfigureAwait(false);
            }

            return batchReady ? BatchAppendResult.Ready : BatchAppendResult.Buffered;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BatchAppendResult> WriteBatchAsync(
        string batchContentType,
        byte[] payload,
        int recordCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (lineDelimited || payload.Length == 0 || recordCount <= 0 ||
            payload.LongLength > maximumPayloadBytes)
        {
            return BatchAppendResult.Rejected;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var createdAt = DateTimeOffset.UtcNow;
            var tempPath = Path.Combine(activeDirectory, CreateFileName(createdAt, ".tmp"));
            var finalPath = Path.Combine(readyDirectory, Path.GetFileNameWithoutExtension(tempPath) + ".batch");
            var totalLength = BatchFileFormat.HeaderSize + payload.LongLength;
            if (!quota.TryReserve(tempPath, totalLength, kind, admissionPolicy))
            {
                return BatchAppendResult.Rejected;
            }

            try
            {
                var header = BatchFileFormat.CreateHeader(
                    kind,
                    createdAt,
                    recordCount,
                    batchContentType,
                    payload.LongLength,
                    BatchCrc32.Compute(payload));
                await using (var stream = CreateFile(tempPath))
                {
                    await stream.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, finalPath);
                quota.Move(tempPath, finalPath);
                return BatchAppendResult.Ready;
            }
            catch
            {
                TryDelete(tempPath);
                quota.Release(tempPath);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> SealAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await SealNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> SealIfOlderAsync(TimeSpan maximumAge, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return activeStream is not null && DateTimeOffset.UtcNow - activeCreatedAt >= maximumAge &&
                   await SealNoLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoredBatchLease?> AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            foreach (var sourcePath in Directory.EnumerateFiles(readyDirectory, "*.batch")
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExpired(sourcePath))
                {
                    DeleteAndRelease(sourcePath);
                    continue;
                }

                var leasedPath = Path.Combine(sendingDirectory, Path.GetFileName(sourcePath));
                try
                {
                    File.Move(sourcePath, leasedPath);
                    quota.Move(sourcePath, leasedPath);
                }
                catch (IOException)
                {
                    continue;
                }

                try
                {
                    var batch = ReadBatch(leasedPath);
                    return new StoredBatchLease(
                        leasedPath,
                        batch.Header.Kind,
                        batch.Header.ContentType,
                        batch.Payload,
                        batch.Header.RecordCount,
                        batch.Header.CreatedAt);
                }
                catch (InvalidDataException)
                {
                    DeleteAndRelease(leasedPath);
                }
                catch (IOException)
                {
                    await AbandonNoLockAsync(leasedPath).ConfigureAwait(false);
                    return null;
                }
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CompleteAsync(string leaseId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateLeasePath(leaseId);
            DeleteAndRelease(leaseId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AbandonAsync(string leaseId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateLeasePath(leaseId);
            await AbandonNoLockAsync(leaseId).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public CacheUsageSnapshot GetUsageSnapshot() => quota.GetSnapshot();

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            await SealNoLockAsync(CancellationToken.None).ConfigureAwait(false);
            disposed = true;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task<bool> CreateActiveNoLockAsync(byte[] firstPayload, CancellationToken cancellationToken)
    {
        activeCreatedAt = DateTimeOffset.UtcNow;
        activePath = Path.Combine(activeDirectory, CreateFileName(activeCreatedAt, ".tmp"));
        var projectedLength = BatchFileFormat.HeaderSize + firstPayload.LongLength;
        if (!quota.TryReserve(activePath, projectedLength, kind, admissionPolicy))
        {
            ResetActiveNoLock();
            return false;
        }

        try
        {
            activeStream = CreateFile(activePath);
            activeChecksum = BatchCrc32.Create();
            var header = BatchFileFormat.CreateHeader(kind, activeCreatedAt, 0, contentType, 0, 0);
            await activeStream.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            await activeStream.WriteAsync(firstPayload.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            activeStream?.Dispose();
            TryDelete(activePath);
            quota.Release(activePath);
            ResetActiveNoLock();
            throw;
        }
    }

    private async Task<bool> SealNoLockAsync(CancellationToken cancellationToken)
    {
        if (activeStream is null || activePath is null || activeRecordCount <= 0)
        {
            return false;
        }

        var sourcePath = activePath;
        var finalPath = Path.Combine(readyDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".batch");
        try
        {
            var header = BatchFileFormat.CreateHeader(
                kind,
                activeCreatedAt,
                activeRecordCount,
                contentType,
                activePayloadBytes,
                activeChecksum.GetCurrentHash());
            activeStream.Position = 0;
            await activeStream.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            await activeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            activeStream.Flush(flushToDisk: true);
            activeStream.Dispose();
            activeStream = null;
            File.Move(sourcePath, finalPath);
            quota.Move(sourcePath, finalPath);
            return true;
        }
        finally
        {
            if (activeStream is not null)
            {
                activeStream.Dispose();
            }
            ResetActiveNoLock();
        }
    }

    private Task AbandonNoLockAsync(string leaseId)
    {
        if (!File.Exists(leaseId))
        {
            quota.Release(leaseId);
            return Task.CompletedTask;
        }

        var destination = Path.Combine(readyDirectory, Path.GetFileName(leaseId));
        if (File.Exists(destination))
        {
            destination = Path.Combine(readyDirectory, CreateFileName(DateTimeOffset.UtcNow, ".batch"));
        }

        File.Move(leaseId, destination);
        quota.Move(leaseId, destination);
        return Task.CompletedTask;
    }

    private void Recover()
    {
        RecoverSendingFiles();
        RecoverActiveFiles();
        RemoveExpiredFiles();
    }

    private void RecoverSendingFiles()
    {
        foreach (var path in Directory.EnumerateFiles(sendingDirectory, "*.batch"))
        {
            var destination = Path.Combine(readyDirectory, Path.GetFileName(path));
            if (File.Exists(destination))
            {
                destination = Path.Combine(readyDirectory, CreateFileName(DateTimeOffset.UtcNow, ".batch"));
            }

            try
            {
                File.Move(path, destination);
                quota.Move(path, destination);
            }
            catch (IOException)
            {
                // Leave the file for a future startup recovery attempt.
            }
        }
    }

    private void RecoverActiveFiles()
    {
        foreach (var path in Directory.EnumerateFiles(activeDirectory, "*.tmp"))
        {
            try
            {
                if (TryPromoteCompletedBatch(path))
                {
                    continue;
                }

                if (lineDelimited && TryRecoverLineBatch(path))
                {
                    continue;
                }

                DeleteAndRelease(path);
            }
            catch (IOException)
            {
                // The active file may belong to another live writer.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat inaccessible files as occupied cache until a later startup.
            }
        }
    }

    private bool TryPromoteCompletedBatch(string path)
    {
        try
        {
            _ = ReadBatch(path);
            PromoteRecovered(path);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private bool TryRecoverLineBatch(string path)
    {
        if (new FileInfo(path).Length > BatchFileFormat.HeaderSize + maximumPayloadBytes)
        {
            return false;
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length <= BatchFileFormat.HeaderSize)
        {
            return false;
        }

        var payload = bytes.AsSpan(BatchFileFormat.HeaderSize);
        var finalPayloadLength = payload.LastIndexOf((byte)'\n') + 1;
        if (finalPayloadLength <= 0)
        {
            return false;
        }

        var completePayload = payload[..finalPayloadLength];
        var recordCount = 0;
        foreach (var current in completePayload)
        {
            if (current == (byte)'\n') recordCount++;
        }

        var createdAt = File.GetCreationTimeUtc(path);
        if (createdAt == DateTime.MinValue)
        {
            createdAt = DateTime.UtcNow;
        }

        var header = BatchFileFormat.CreateHeader(
            kind,
            new DateTimeOffset(createdAt, TimeSpan.Zero),
            recordCount,
            contentType,
            finalPayloadLength,
            BatchCrc32.Compute(completePayload));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(BatchFileFormat.HeaderSize + finalPayloadLength);
            stream.Position = 0;
            stream.Write(header);
            stream.Flush(flushToDisk: true);
        }
        quota.TryReserve(path, BatchFileFormat.HeaderSize + finalPayloadLength, kind, admissionPolicy);
        PromoteRecovered(path);
        return true;
    }

    private void PromoteRecovered(string path)
    {
        var destination = Path.Combine(readyDirectory, Path.GetFileNameWithoutExtension(path) + ".batch");
        if (File.Exists(destination))
        {
            destination = Path.Combine(readyDirectory, CreateFileName(DateTimeOffset.UtcNow, ".batch"));
        }
        File.Move(path, destination);
        quota.Move(path, destination);
    }

    private void RemoveExpiredFiles()
    {
        foreach (var directory in new[] { readyDirectory, sendingDirectory })
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.batch"))
            {
                if (IsExpired(path))
                {
                    DeleteAndRelease(path);
                }
            }
        }
    }

    private bool IsExpired(string path) =>
        DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > options.MaxAge;

    private (BatchHeader Header, byte[] Payload) ReadBatch(string path)
    {
        var fileLength = new FileInfo(path).Length;
        if (fileLength <= BatchFileFormat.HeaderSize ||
            fileLength > BatchFileFormat.HeaderSize + maximumPayloadBytes)
        {
            throw new InvalidDataException("Cache batch length is outside the configured batch limit.");
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length <= BatchFileFormat.HeaderSize)
        {
            throw new InvalidDataException("Cache batch is truncated.");
        }

        var header = BatchFileFormat.ReadHeader(bytes.AsSpan(0, BatchFileFormat.HeaderSize));
        if (header.Kind != kind || header.PayloadLength != bytes.LongLength - BatchFileFormat.HeaderSize ||
            header.PayloadLength > maximumPayloadBytes)
        {
            throw new InvalidDataException("Cache batch metadata does not match its payload.");
        }

        var payload = bytes.AsSpan(BatchFileFormat.HeaderSize).ToArray();
        if (BatchCrc32.Compute(payload) != header.Checksum)
        {
            throw new InvalidDataException("Cache batch checksum failed.");
        }

        return (header, payload);
    }

    private static FileStream CreateFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string CreateFileName(DateTimeOffset createdAt, string extension) =>
        $"{createdAt.ToUnixTimeMilliseconds():D13}-{Guid.NewGuid():N}{extension}";

    private void DeleteAndRelease(string path)
    {
        TryDelete(path);
        if (!File.Exists(path))
        {
            quota.Release(path);
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ValidateLeasePath(string leaseId)
    {
        var leasePath = Path.GetFullPath(leaseId);
        var expectedRoot = Path.GetFullPath(sendingDirectory) + Path.DirectorySeparatorChar;
        if (!leasePath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The cache lease does not belong to this queue.");
        }
    }

    private void ResetActiveNoLock()
    {
        activeStream = null;
        activePath = null;
        activeCreatedAt = default;
        activeRecordCount = 0;
        activePayloadBytes = 0;
        activeChecksum = default;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(BatchFileStore));
        }
    }
}
