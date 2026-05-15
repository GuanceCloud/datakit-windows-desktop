namespace Guance.Rum.Windows;

public sealed class RumSessionReplayConfig
{
    public bool Enabled { get; init; }
    public double SampleRate { get; init; } = 1.0;
    public double OnErrorSampleRate { get; init; }
    public SessionReplayTextAndInputPrivacy TextAndInputPrivacy { get; init; } = SessionReplayTextAndInputPrivacy.MaskSensitiveInputs;
    public SessionReplayTouchPrivacy TouchPrivacy { get; init; } = SessionReplayTouchPrivacy.Show;
    public int SegmentRecordLimit { get; init; } = 500;
    public int SegmentBytesLimit { get; init; } = 1024 * 1024;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxQueueItems { get; init; } = 10_000;
    public long MaxQueueBytes { get; init; } = 128L * 1024 * 1024;
    public int MaxNodeCount { get; init; } = 2_000;
    public int MaxTreeDepth { get; init; } = 64;
    public int MaxTextLength { get; init; } = 256;
    public int MaxAttributeValueLength { get; init; } = 512;
    public IReadOnlyCollection<string> CustomRenderedTypeNameMarkers { get; init; } = new[]
    {
        "DirectX",
        "D3D",
        "SwapChain",
        "OpenGL",
        "GLControl",
        "Vulkan",
        "Skia",
        "SKElement",
        "SKGL",
        "HwndHost",
        "WindowsFormsHost",
        "ElementHost",
        "WebView",
        "MediaElement"
    };
    public IReadOnlyCollection<string> SensitiveTextPatterns { get; init; } = new[]
    {
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        @"\b(?:\d[ -]*?){13,19}\b",
        @"\b(?:\+?\d[\d -]{7,}\d)\b"
    };
}

public enum SessionReplayTextAndInputPrivacy
{
    Allow,
    MaskSensitiveInputs,
    MaskAllInputs,
    MaskAll
}

public enum SessionReplayTouchPrivacy
{
    Show,
    Hide
}
