namespace Guance.Rum.Windows;

/// <summary>
/// Distributed-tracing propagation format used for automatically instrumented HTTP requests.
/// </summary>
public enum RumTraceType
{
    DdTrace,
    ZipkinMultiHeader,
    ZipkinSingleHeader,
    TraceParent,
    SkyWalking,
    Jaeger
}
