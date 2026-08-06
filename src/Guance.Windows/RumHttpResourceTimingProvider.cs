using System.Net.Http;

namespace Guance.Windows;

public delegate RumResourceTiming? RumHttpResourceTimingProvider(
    HttpRequestMessage request,
    HttpResponseMessage? response,
    TimeSpan elapsed,
    Exception? exception);
