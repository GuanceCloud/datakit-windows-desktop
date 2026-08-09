namespace Guance.Windows;

/// <summary>
/// Distributed-tracing propagation format used for automatically instrumented HTTP requests.
/// </summary>
public enum TraceType
{
    /// <summary>Datadog multi-header propagation.</summary>
    DdTrace,
    /// <summary>Zipkin B3 multi-header propagation.</summary>
    ZipkinMultiHeader,
    /// <summary>Zipkin B3 single-header propagation.</summary>
    ZipkinSingleHeader,
    /// <summary>W3C traceparent propagation.</summary>
    TraceParent,
    /// <summary>Apache SkyWalking propagation.</summary>
    SkyWalking,
    /// <summary>Jaeger propagation.</summary>
    Jaeger
}
