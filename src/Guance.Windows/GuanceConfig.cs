using System.Net.Http;

namespace Guance.Windows;

public sealed class GuanceConfig
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
    public TraceConfig Trace { get; init; } = new();
    public LogConfig Logging { get; init; } = new();
    public RumPrivacyConfig Privacy { get; init; } = new();
    public RumSessionReplayConfig SessionReplay { get; init; } = new();
    public CacheOptions Cache { get; init; } = new();
    public UploadOptions Upload { get; init; } = new();
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

        if (Logging is null)
        {
            throw new InvalidOperationException("Logging must not be null.");
        }

        if (Logging.SampleRate is < 0 or > 1)
        {
            throw new InvalidOperationException("Logging.SampleRate must be between 0 and 1.");
        }

        if (Logging.EnableTraceCapture && !Logging.EnableCustomLog)
        {
            throw new InvalidOperationException("Logging.EnableCustomLog must be enabled when Logging.EnableTraceCapture is enabled.");
        }

        if (Logging.GlobalContext is null)
        {
            throw new InvalidOperationException("Logging.GlobalContext must not be null.");
        }

        if (Logging.LevelFilters is not null)
        {
            foreach (var level in Logging.LevelFilters)
            {
                if (!Enum.IsDefined(typeof(LogStatus), level))
                {
                    throw new InvalidOperationException($"Logging.LevelFilters contains an unsupported level: {level}.");
                }
            }
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

        if (Cache is null)
        {
            throw new InvalidOperationException("Cache must not be null.");
        }

        if (Cache.MaxDiskBytes <= 0)
        {
            throw new InvalidOperationException("Cache.MaxDiskBytes must be greater than 0.");
        }

        if (Cache.LowWatermarkRatio is <= 0 or >= 1)
        {
            throw new InvalidOperationException("Cache.LowWatermarkRatio must be between 0 and 1.");
        }

        if (Cache.MaxFiles <= 0 || Cache.MaxBatchItems <= 0 || Cache.MaxBatchBytes <= 0)
        {
            throw new InvalidOperationException("Cache file and batch limits must be greater than 0.");
        }

        if (Cache.MaxAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Cache.MaxAge must be greater than zero.");
        }

        if (Cache.RumShare <= 0 || Cache.LogShare <= 0 || Cache.SessionReplayShare <= 0 ||
            Math.Abs(Cache.RumShare + Cache.LogShare + Cache.SessionReplayShare - 1) > 0.0001)
        {
            throw new InvalidOperationException("Cache stream shares must be greater than 0 and add up to 1.");
        }

        var maximumReplayBody = checked(
            SessionReplay.SegmentBytesLimit + CacheOptions.ReplayEnvelopeAllowanceBytes);
        var largestCacheFile = Math.Max(Cache.MaxBatchBytes, maximumReplayBody) +
                               Queue.BatchFileFormat.HeaderSize;
        if (Cache.MaxDiskBytes < largestCacheFile)
        {
            throw new InvalidOperationException("Cache.MaxDiskBytes must fit the largest configured batch file.");
        }

        if (Upload is null)
        {
            throw new InvalidOperationException("Upload must not be null.");
        }

        if (Upload.MaxBytesPerSecond < 0 || Upload.BurstBytes <= 0 || Upload.MaxRequestsPerSecond < 0)
        {
            throw new InvalidOperationException("Upload rate limits must not be negative and BurstBytes must be greater than 0.");
        }

        if (Upload.MaxBytesPerSecond > 0 &&
            Upload.BurstBytes < Math.Max(
                CompressIntakeRequests
                    ? UploadSizeEstimator.EstimateDeflateUpperBound(Cache.MaxBatchBytes)
                    : Cache.MaxBatchBytes,
                maximumReplayBody))
        {
            throw new InvalidOperationException("Upload.BurstBytes must fit the largest configured upload batch.");
        }

        if (Upload.MaxConcurrentRequests != 1)
        {
            throw new InvalidOperationException("Upload.MaxConcurrentRequests must be 1.");
        }

        if (Upload.MaxBatchesPerCycle <= 0 || Upload.RumWeight <= 0 || Upload.LogWeight <= 0 || Upload.SessionReplayWeight <= 0)
        {
            throw new InvalidOperationException("Upload cycle and stream weights must be greater than 0.");
        }
    }
}
