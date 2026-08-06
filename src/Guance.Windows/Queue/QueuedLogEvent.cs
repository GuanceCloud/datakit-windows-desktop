namespace Guance.Windows.Queue;

internal sealed record QueuedLogEvent(long Id, string Line, DateTimeOffset CreatedAt);
