namespace Guance.Windows;

/// <summary>
/// Distributed-tracing propagation format used for automatically instrumented HTTP requests.
/// </summary>
public enum TraceType
{
    DdTrace,
    ZipkinMultiHeader,
    ZipkinSingleHeader,
    TraceParent,
    SkyWalking,
    Jaeger
}
