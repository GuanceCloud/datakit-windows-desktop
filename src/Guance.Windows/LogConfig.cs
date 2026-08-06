namespace Guance.Windows;

public sealed class LogConfig
{
    public bool EnableCustomLog { get; init; }
    public bool EnableLinkRumData { get; init; }
    public bool EnableTraceCapture { get; init; }
    public double SampleRate { get; init; } = 1.0;
    public IReadOnlyCollection<LogStatus>? LevelFilters { get; init; }
    public IReadOnlyDictionary<string, object?> GlobalContext { get; init; } =
        new Dictionary<string, object?>();
    public LogDiscardStrategy DiscardStrategy { get; init; } = LogDiscardStrategy.DiscardNew;
}
