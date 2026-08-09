namespace Guance.Windows;

/// <summary>Specifies which log to discard when the in-memory queue is full.</summary>
public enum LogDiscardStrategy
{
    /// <summary>Rejects the newly submitted log.</summary>
    DiscardNew,
    /// <summary>Removes the oldest queued log before accepting the new log.</summary>
    DiscardOldest
}
