namespace Guance.Rum.Windows;

public sealed class RumLogConfig
{
    public bool EnableCustomLog { get; init; }
    public bool EnableLinkRumData { get; init; }
    public bool EnableTraceCapture { get; init; }
    public double SampleRate { get; init; } = 1.0;
    public IReadOnlyCollection<RumLogStatus>? LevelFilters { get; init; }
    public IReadOnlyDictionary<string, object?> GlobalContext { get; init; } =
        new Dictionary<string, object?>();
    public int MaxQueueItems { get; init; } = 5_000;
    public long MaxQueueBytes { get; init; } = 32L * 1024 * 1024;
    public RumLogDiscardStrategy DiscardStrategy { get; init; } = RumLogDiscardStrategy.DiscardNew;
}
