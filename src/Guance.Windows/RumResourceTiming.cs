namespace Guance.Windows;

/// <summary>Describes measured or fallback timing information for a RUM resource.</summary>
public sealed class RumResourceTiming
{
    /// <summary>Gets a label identifying the timing provider.</summary>
    public string Source { get; init; } = "manual";
    /// <summary>Gets the timing precision label, such as phase or total_elapsed_fallback.</summary>
    public string Precision { get; init; } = "phase";
    /// <summary>Gets a description of the available timing phases.</summary>
    public string? Phase { get; init; }
    /// <summary>Gets DNS lookup duration when measured.</summary>
    public TimeSpan? Dns { get; init; }
    /// <summary>Gets TCP connection duration when measured.</summary>
    public TimeSpan? Tcp { get; init; }
    /// <summary>Gets TLS handshake duration when measured.</summary>
    public TimeSpan? Ssl { get; init; }
    /// <summary>Gets time to first byte when measured or estimated.</summary>
    public TimeSpan? Ttfb { get; init; }
    /// <summary>Gets the total resource duration when measured.</summary>
    public TimeSpan? TotalDuration { get; init; }
    /// <summary>Indicates whether time to first byte is an estimate.</summary>
    public bool TtfbEstimated { get; init; }

    /// <summary>Creates fallback timing when only total elapsed time is available.</summary>
    /// <param name="elapsed">The measured total elapsed time.</param>
    /// <param name="source">A label identifying the timing source.</param>
    /// <returns>A duration-only timing value with estimated TTFB.</returns>
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

    /// <summary>Creates timing from independently measured network phases.</summary>
    /// <param name="dns">DNS lookup duration.</param>
    /// <param name="tcp">TCP connection duration.</param>
    /// <param name="ssl">TLS handshake duration.</param>
    /// <param name="ttfb">Time to first byte.</param>
    /// <param name="totalDuration">Total resource duration.</param>
    /// <param name="source">A label identifying the timing source.</param>
    /// <returns>A phase-precision timing value.</returns>
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
