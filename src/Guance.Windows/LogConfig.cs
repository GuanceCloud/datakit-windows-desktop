namespace Guance.Windows;

/// <summary>Configures custom log collection and RUM correlation.</summary>
public sealed class LogConfig
{
    /// <summary>Enables custom log collection.</summary>
    public bool EnableCustomLog { get; init; }
    /// <summary>Includes the active RUM session, view, and action context on logs.</summary>
    public bool EnableLinkRumData { get; init; }
    /// <summary>Forwards <see cref="System.Diagnostics.Trace" /> output to Guance Logging.</summary>
    public bool EnableTraceCapture { get; init; }
    /// <summary>Gets the log sampling rate in the inclusive range 0 through 1.</summary>
    public double SampleRate { get; init; } = 1.0;
    /// <summary>Gets an optional allow-list of accepted log levels.</summary>
    public IReadOnlyCollection<LogStatus>? LevelFilters { get; init; }
    /// <summary>Gets tags attached to every accepted log.</summary>
    public IReadOnlyDictionary<string, object?> GlobalContext { get; init; } =
        new Dictionary<string, object?>();
    /// <summary>Gets the strategy used when the in-memory log queue reaches capacity.</summary>
    public LogDiscardStrategy DiscardStrategy { get; init; } = LogDiscardStrategy.DiscardNew;
}
