using System.Net.Http;

namespace Guance.Rum.Windows;

/// <summary>
/// Configures HTTP trace-header propagation and optional RUM resource correlation.
/// </summary>
public sealed class RumTraceConfig
{
    /// <summary>Enables automatic trace-header injection. Disabled by default.</summary>
    public bool EnableAutoTrace { get; init; }

    /// <summary>Adds the generated trace and span identifiers to the matching RUM resource.</summary>
    public bool EnableLinkRumData { get; init; }

    /// <summary>Fraction of requests whose propagated sampling flag is enabled.</summary>
    public double SampleRate { get; init; } = 1.0;

    /// <summary>Propagation format. DDTrace matches the Android SDK default.</summary>
    public RumTraceType TraceType { get; init; } = RumTraceType.DdTrace;

    /// <summary>
    /// Optional allow-list callback. Return true only for destinations that may receive trace headers.
    /// </summary>
    public Func<Uri, bool>? ShouldTrace { get; init; }

    /// <summary>
    /// Optional custom provider. When set, it replaces the built-in header and identifier generation.
    /// </summary>
    public Func<HttpRequestMessage, RumTraceContext?>? ContextProvider { get; init; }
}
