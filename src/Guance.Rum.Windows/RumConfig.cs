using System.Net.Http;

namespace Guance.Rum.Windows;

public sealed class RumConfig
{
    private static readonly HashSet<string> SupportedEnvironments = new(StringComparer.OrdinalIgnoreCase)
    {
        "prod",
        "gray",
        "pre",
        "common",
        "local"
    };

    public string? DatawayUrl { get; init; }
    public string? DatakitUrl { get; init; }
    public string? ClientToken { get; init; }
    public string RumAppId { get; init; } = string.Empty;
    public string ServiceName { get; init; } = "df_rum_windows";
    public string Env { get; init; } = "prod";
    public string Version { get; init; } = "1.0.0";
    public bool Debug { get; init; }
    public double SampleRate { get; init; } = 1.0;
    public double SessionErrorSampleRate { get; init; }
    public RumTraceConfig Trace { get; init; } = new();
    public RumPrivacyConfig Privacy { get; init; } = new();
    public RumSessionReplayConfig SessionReplay { get; init; } = new();
    public int BatchSize { get; init; } = 50;
    public int MaxQueueItems { get; init; } = 100_000;
    public long MaxQueueBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public bool CompressIntakeRequests { get; init; }
    public string? CacheDirectory { get; init; }
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; init; }
    public RumHttpResourceTimingProvider? HttpResourceTimingProvider { get; init; }
    public Action<RumDiagnosticEvent>? DiagnosticListener { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RumAppId))
        {
            throw new InvalidOperationException("RumAppId is required.");
        }

        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException("ServiceName is required.");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new InvalidOperationException("Version is required.");
        }

        if (string.IsNullOrWhiteSpace(Env) || !SupportedEnvironments.Contains(Env))
        {
            throw new InvalidOperationException("Env must be one of: prod, gray, pre, common, local.");
        }

        var hasDataway = !string.IsNullOrWhiteSpace(DatawayUrl);
        var hasDatakit = !string.IsNullOrWhiteSpace(DatakitUrl);
        if (!hasDataway && !hasDatakit)
        {
            throw new InvalidOperationException("Either DatawayUrl or DatakitUrl is required.");
        }

        if (hasDataway && string.IsNullOrWhiteSpace(ClientToken))
        {
            throw new InvalidOperationException("ClientToken is required when DatawayUrl is configured.");
        }

        if (SampleRate is < 0 or > 1)
        {
            throw new InvalidOperationException("SampleRate must be between 0 and 1.");
        }

        if (SessionErrorSampleRate is < 0 or > 1)
        {
            throw new InvalidOperationException("SessionErrorSampleRate must be between 0 and 1.");
        }

        if (Trace is null)
        {
            throw new InvalidOperationException("Trace must not be null.");
        }

        if (Trace.SampleRate is < 0 or > 1)
        {
            throw new InvalidOperationException("Trace.SampleRate must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(Privacy.RedactedValue))
        {
            throw new InvalidOperationException("Privacy.RedactedValue must not be empty.");
        }

        if (SessionReplay.SampleRate is < 0 or > 1)
        {
            throw new InvalidOperationException("SessionReplay.SampleRate must be between 0 and 1.");
        }

        if (SessionReplay.OnErrorSampleRate is < 0 or > 1)
        {
            throw new InvalidOperationException("SessionReplay.OnErrorSampleRate must be between 0 and 1.");
        }

        if (SessionReplay.SegmentRecordLimit <= 0)
        {
            throw new InvalidOperationException("SessionReplay.SegmentRecordLimit must be greater than 0.");
        }

        if (SessionReplay.SegmentBytesLimit <= 0)
        {
            throw new InvalidOperationException("SessionReplay.SegmentBytesLimit must be greater than 0.");
        }

        if (SessionReplay.FlushInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SessionReplay.FlushInterval must be greater than zero.");
        }

        if (SessionReplay.MaxQueueItems <= 0)
        {
            throw new InvalidOperationException("SessionReplay.MaxQueueItems must be greater than 0.");
        }

        if (SessionReplay.MaxQueueBytes <= 0)
        {
            throw new InvalidOperationException("SessionReplay.MaxQueueBytes must be greater than 0.");
        }

        if (SessionReplay.MaxNodeCount <= 0)
        {
            throw new InvalidOperationException("SessionReplay.MaxNodeCount must be greater than 0.");
        }

        if (SessionReplay.MaxTreeDepth <= 0)
        {
            throw new InvalidOperationException("SessionReplay.MaxTreeDepth must be greater than 0.");
        }

        if (SessionReplay.MaxTextLength <= 0)
        {
            throw new InvalidOperationException("SessionReplay.MaxTextLength must be greater than 0.");
        }

        if (SessionReplay.MaxAttributeValueLength <= 0)
        {
            throw new InvalidOperationException("SessionReplay.MaxAttributeValueLength must be greater than 0.");
        }

        if (SessionReplay.LargeImagePrivacyThreshold <= 0)
        {
            throw new InvalidOperationException("SessionReplay.LargeImagePrivacyThreshold must be greater than 0.");
        }

        if (SessionReplay.CustomRenderedTypeNameMarkers is null)
        {
            throw new InvalidOperationException("SessionReplay.CustomRenderedTypeNameMarkers must not be null.");
        }

        foreach (var marker in SessionReplay.CustomRenderedTypeNameMarkers)
        {
            if (string.IsNullOrWhiteSpace(marker))
            {
                throw new InvalidOperationException("SessionReplay.CustomRenderedTypeNameMarkers must not contain empty values.");
            }
        }

        if (SessionReplay.SensitiveTextPatterns is null)
        {
            throw new InvalidOperationException("SessionReplay.SensitiveTextPatterns must not be null.");
        }

        foreach (var pattern in SessionReplay.SensitiveTextPatterns)
        {
            try
            {
                _ = System.Text.RegularExpressions.Regex.IsMatch(string.Empty, pattern);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid SessionReplay sensitive text pattern: {pattern}", ex);
            }
        }
    }
}
