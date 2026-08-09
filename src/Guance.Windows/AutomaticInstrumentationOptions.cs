namespace Guance.Windows;

/// <summary>Controls which automatic Windows instrumentation modules the SDK enables.</summary>
public sealed class AutomaticInstrumentationOptions
{
    /// <summary>Enables automatic WPF window and control instrumentation.</summary>
    public bool EnableWpf { get; init; } = true;
    /// <summary>Enables automatic Windows Forms window and control instrumentation.</summary>
    public bool EnableWinForms { get; init; } = true;
    /// <summary>Enables WinUI 3 control instrumentation after a window is attached.</summary>
    public bool EnableWinUI { get; init; } = true;
    /// <summary>Enables discovery and instrumentation of supported WebView2 controls.</summary>
    public bool EnableWebView { get; init; } = true;
    /// <summary>Enables automatic <see cref="System.Net.Http.HttpClient" /> resource instrumentation.</summary>
    public bool EnableHttpClient { get; init; } = true;
    /// <summary>Enables collection of unhandled application and UI framework exceptions.</summary>
    public bool EnableUnhandledException { get; init; } = true;
    /// <summary>Enables UI-thread block detection and Long Task reporting.</summary>
    public bool EnableUiThreadBlock { get; init; } = true;
    /// <summary>Enables application cold- and hot-launch action tracking.</summary>
    public bool EnableAppLaunch { get; init; } = true;
    /// <summary>Gets the minimum UI-thread block duration reported as a Long Task.</summary>
    public TimeSpan UiThreadBlockThreshold { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Gets the interval between UI-thread responsiveness probes.</summary>
    public TimeSpan UiThreadProbeInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Gets the cooldown used to coalesce repeated reports for one continuous block.</summary>
    public TimeSpan UiThreadLongTaskCooldown { get; init; } = TimeSpan.FromSeconds(5);
}
