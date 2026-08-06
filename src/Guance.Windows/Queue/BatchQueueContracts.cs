namespace Guance.Windows.Queue;

internal enum BatchStreamKind
{
    Rum = 1,
    Log = 2,
    SessionReplay = 3
}

internal enum CacheAdmissionPolicy
{
    DiscardOldest,
    DiscardNew
}

internal readonly record struct BatchAppendResult(bool Accepted, bool BatchReady)
{
    public static BatchAppendResult Rejected => new(false, false);
    public static BatchAppendResult Buffered => new(true, false);
    public static BatchAppendResult Ready => new(true, true);
}

internal sealed record QueueBatch<T>(
    string LeaseId,
    IReadOnlyList<T> Items,
    long PayloadBytes,
    DateTimeOffset CreatedAt);

internal sealed record StoredBatchLease(
    string LeaseId,
    BatchStreamKind Kind,
    string ContentType,
    byte[] Payload,
    int RecordCount,
    DateTimeOffset CreatedAt);

internal readonly record struct CacheUsageSnapshot(long AllocatedBytes, int FileCount);
