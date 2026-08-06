namespace Guance.Windows;

public sealed class RumSessionReplayConfig
{
    public bool Enabled { get; init; }
    public double SampleRate { get; init; } = 1.0;
    public double OnErrorSampleRate { get; init; }
    public SessionReplayTextAndInputPrivacy TextAndInputPrivacy { get; init; } = SessionReplayTextAndInputPrivacy.MaskAll;
    public SessionReplayTouchPrivacy TouchPrivacy { get; init; } = SessionReplayTouchPrivacy.Show;
    public SessionReplayImagePrivacy ImagePrivacy { get; init; } = SessionReplayImagePrivacy.MaskAll;
    public int SegmentRecordLimit { get; init; } = 500;
    public int SegmentBytesLimit { get; init; } = 1024 * 1024;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxNodeCount { get; init; } = 2_000;
    public int MaxTreeDepth { get; init; } = 64;
    public int MaxTextLength { get; init; } = 256;
    public int MaxAttributeValueLength { get; init; } = 512;
    /// <summary>
    /// Compatibility kill switch for image capture. When false, it overrides <see cref="ImagePrivacy" /> and masks every image.
    /// </summary>
    public bool CaptureImages { get; init; } = true;
    public int MaxImageBytes { get; init; } = 512 * 1024;
    public int MaxImageDimension { get; init; } = 1024;
    public double LargeImagePrivacyThreshold { get; init; } = 100;
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

public enum SessionReplayImagePrivacy
{
    MaskAll,
    MaskLargeOnly,
    MaskNone
}
