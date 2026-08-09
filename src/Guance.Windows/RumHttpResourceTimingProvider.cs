using System.Net.Http;

namespace Guance.Windows;

/// <summary>Provides precise network phase timing for an automatically tracked HTTP resource.</summary>
/// <param name="request">The outgoing HTTP request.</param>
/// <param name="response">The HTTP response, or null when the request failed.</param>
/// <param name="elapsed">The total measured request duration.</param>
/// <param name="exception">The request exception, or null when the request completed.</param>
/// <returns>Timing details, or null to use the SDK total-duration fallback.</returns>
public delegate RumResourceTiming? RumHttpResourceTimingProvider(
    HttpRequestMessage request,
    HttpResponseMessage? response,
    TimeSpan elapsed,
    Exception? exception);
