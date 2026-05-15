namespace Guance.Rum.Windows.Queue;

internal sealed record QueuedRumEvent(long Id, string Line, DateTimeOffset CreatedAt);
