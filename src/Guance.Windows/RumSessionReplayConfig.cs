namespace Guance.Windows;

/// <summary>Configures experimental Windows Session Replay recording, limits, and privacy.</summary>
public sealed class RumSessionReplayConfig
{
    /// <summary>Enables Session Replay. It is disabled by default.</summary>
    public bool Enabled { get; init; }
    /// <summary>Gets the replay sampling rate for normally sampled sessions.</summary>
    public double SampleRate { get; init; } = 1.0;
    /// <summary>Gets the additional replay sampling rate for error sessions.</summary>
    public double OnErrorSampleRate { get; init; }
    /// <summary>Gets the default text and input privacy mode.</summary>
    public SessionReplayTextAndInputPrivacy TextAndInputPrivacy { get; init; } = SessionReplayTextAndInputPrivacy.MaskAll;
    /// <summary>Gets the default pointer and touch privacy mode.</summary>
    public SessionReplayTouchPrivacy TouchPrivacy { get; init; } = SessionReplayTouchPrivacy.Show;
    /// <summary>Gets the default image privacy mode.</summary>
    public SessionReplayImagePrivacy ImagePrivacy { get; init; } = SessionReplayImagePrivacy.MaskAll;
    /// <summary>Gets the maximum number of records in one replay segment.</summary>
    public int SegmentRecordLimit { get; init; } = 500;
    /// <summary>Gets the maximum uncompressed bytes in one replay segment.</summary>
    public int SegmentBytesLimit { get; init; } = 1024 * 1024;
    /// <summary>Gets the maximum interval before an active replay segment is flushed.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Gets the maximum number of UI nodes captured in one snapshot.</summary>
    public int MaxNodeCount { get; init; } = 2_000;
    /// <summary>Gets the maximum UI-tree depth captured in one snapshot.</summary>
    public int MaxTreeDepth { get; init; } = 64;
    /// <summary>Gets the maximum captured text length per UI node.</summary>
    public int MaxTextLength { get; init; } = 256;
    /// <summary>Gets the maximum captured attribute value length per UI node.</summary>
    public int MaxAttributeValueLength { get; init; } = 512;
    /// <summary>
    /// Compatibility kill switch for image capture. When false, it overrides <see cref="ImagePrivacy" /> and masks every image.
    /// </summary>
    public bool CaptureImages { get; init; } = true;
    /// <summary>Gets the maximum encoded bytes captured for one image.</summary>
    public int MaxImageBytes { get; init; } = 512 * 1024;
    /// <summary>Gets the maximum width or height used when encoding a captured image.</summary>
    public int MaxImageDimension { get; init; } = 1024;
    /// <summary>Gets the area threshold used by <see cref="SessionReplayImagePrivacy.MaskLargeOnly" />.</summary>
    public double LargeImagePrivacyThreshold { get; init; } = 100;
    /// <summary>Gets type-name fragments treated as custom-rendered or opaque content.</summary>
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
    /// <summary>Gets regular expressions used to identify sensitive text.</summary>
    public IReadOnlyCollection<string> SensitiveTextPatterns { get; init; } = new[]
    {
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        @"\b(?:\d[ -]*?){13,19}\b",
        @"\b(?:\+?\d[\d -]{7,}\d)\b"
    };
}

/// <summary>Specifies how Session Replay records text and input values.</summary>
public enum SessionReplayTextAndInputPrivacy
{
    /// <summary>Records text and input values without masking.</summary>
    Allow,
    /// <summary>Masks values from controls recognized as sensitive.</summary>
    MaskSensitiveInputs,
    /// <summary>Masks every input value while allowing non-input text.</summary>
    MaskAllInputs,
    /// <summary>Masks all captured text and input values.</summary>
    MaskAll
}

/// <summary>Specifies whether Session Replay records pointer and touch coordinates.</summary>
public enum SessionReplayTouchPrivacy
{
    /// <summary>Records pointer and touch interactions.</summary>
    Show,
    /// <summary>Suppresses pointer and touch interaction details.</summary>
    Hide
}

/// <summary>Specifies how Session Replay captures images.</summary>
public enum SessionReplayImagePrivacy
{
    /// <summary>Masks every image.</summary>
    MaskAll,
    /// <summary>Masks images whose rendered area exceeds the configured threshold.</summary>
    MaskLargeOnly,
    /// <summary>Allows image capture subject to size limits.</summary>
    MaskNone
}
