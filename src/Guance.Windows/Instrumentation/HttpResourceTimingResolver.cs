using System.Net.Http;

namespace Guance.Windows;

internal static class HttpResourceTimingResolver
{
    public static RumResourceTiming Resolve(
        GuanceConfig config,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        TimeSpan elapsed,
        Exception? exception,
        string fallbackSource)
    {
        var provider = config.HttpResourceTimingProvider;
        if (provider is not null)
        {
            try
            {
                var timing = provider(request, response, elapsed, exception);
                if (timing is not null)
                {
                    return timing;
                }
            }
            catch
            {
                // Timing enrichment must never break user traffic or RUM resource completion.
            }
        }

        return RumResourceTiming.FromTotalElapsed(elapsed, fallbackSource);
    }
}
