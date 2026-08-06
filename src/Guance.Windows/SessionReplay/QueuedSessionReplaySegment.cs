namespace Guance.Windows.SessionReplay;

internal sealed record QueuedSessionReplaySegment(
    long Id,
    string ContentType,
    byte[] Body,
    DateTimeOffset CreatedAt,
    long Size);
