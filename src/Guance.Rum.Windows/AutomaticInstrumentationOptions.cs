namespace Guance.Rum.Windows;

public sealed class AutomaticInstrumentationOptions
{
    public bool EnableWpf { get; init; } = true;
    public bool EnableWinForms { get; init; } = true;
    public bool EnableWinUI { get; init; } = true;
    public bool EnableWebView { get; init; } = true;
    public bool EnableHttpClient { get; init; } = true;
    public bool EnableUnhandledException { get; init; } = true;
    public bool EnableUiThreadBlock { get; init; } = true;
    public TimeSpan UiThreadBlockThreshold { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan UiThreadProbeInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan UiThreadLongTaskCooldown { get; init; } = TimeSpan.FromSeconds(5);
}
