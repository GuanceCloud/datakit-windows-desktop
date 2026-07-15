namespace Guance.Rum.Windows;

public sealed class RumResourceTiming
{
    public string Source { get; init; } = "manual";
    public string Precision { get; init; } = "phase";
    public string? Phase { get; init; }
    public TimeSpan? Dns { get; init; }
    public TimeSpan? Tcp { get; init; }
    public TimeSpan? Ssl { get; init; }
    public TimeSpan? Ttfb { get; init; }
    public TimeSpan? TotalDuration { get; init; }
    public bool TtfbEstimated { get; init; }

    public static RumResourceTiming FromTotalElapsed(TimeSpan elapsed, string source)
    {
        return new RumResourceTiming
        {
            Source = source,
            Precision = "total_elapsed_fallback",
            Phase = "duration_only",
            TotalDuration = elapsed,
            Ttfb = elapsed,
            TtfbEstimated = true
        };
    }

    public static RumResourceTiming FromPhases(
        TimeSpan? dns = null,
        TimeSpan? tcp = null,
        TimeSpan? ssl = null,
        TimeSpan? ttfb = null,
        TimeSpan? totalDuration = null,
        string source = "manual")
    {
        return new RumResourceTiming
        {
            Source = source,
            Precision = "phase",
            Phase = BuildPhase(dns, tcp, ssl, ttfb),
            Dns = dns,
            Tcp = tcp,
            Ssl = ssl,
            Ttfb = ttfb,
            TotalDuration = totalDuration,
            TtfbEstimated = false
        };
    }

    internal IReadOnlyDictionary<string, object?> ToProperties()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RumConstants.ResourceTimingSource] = Source,
            [RumConstants.ResourceTimingPrecision] = Precision,
            [RumConstants.ResourceTimingPhase] = Phase ?? BuildPhase(Dns, Tcp, Ssl, Ttfb),
            [RumConstants.ResourceTtfbEstimated] = TtfbEstimated
        };

        AddDuration(result, RumConstants.ResourceDns, Dns);
        AddDuration(result, RumConstants.ResourceTcp, Tcp);
        AddDuration(result, RumConstants.ResourceSsl, Ssl);
        AddDuration(result, RumConstants.ResourceTtfb, Ttfb);
        AddDuration(result, RumConstants.ResourceTimingDuration, TotalDuration);
        return result;
    }

    private static void AddDuration(IDictionary<string, object?> result, string key, TimeSpan? value)
    {
        if (value is not null)
        {
            result[key] = ToNanoseconds(value.Value);
        }
    }

    private static long ToNanoseconds(TimeSpan value)
    {
        return Clock.DurationNanoseconds(value);
    }

    private static string BuildPhase(TimeSpan? dns, TimeSpan? tcp, TimeSpan? ssl, TimeSpan? ttfb)
    {
        var parts = new List<string>(4);
        if (dns is not null)
        {
            parts.Add("dns");
        }

        if (tcp is not null)
        {
            parts.Add("tcp");
        }

        if (ssl is not null)
        {
            parts.Add("ssl");
        }

        if (ttfb is not null)
        {
            parts.Add("ttfb");
        }

        return parts.Count == 0 ? "unspecified" : string.Join('_', parts);
    }
}
