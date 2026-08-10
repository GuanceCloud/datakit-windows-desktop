using System.Net.Http;

namespace Guance.Windows;

/// <summary>Configures transport, RUM, logging, tracing, privacy, caching, and upload behavior.</summary>
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

    /// <summary>Gets the public DataWay intake base URL.</summary>
    public string? DatawayUrl { get; init; }
    /// <summary>Gets the local DataKit intake base URL.</summary>
    public string? DatakitUrl { get; init; }
    /// <summary>Gets the DataWay client token. It is not required for local DataKit intake.</summary>
    public string? ClientToken { get; init; }
    /// <summary>Gets the RUM application identifier created in Guance.</summary>
    public string RumAppId { get; init; } = string.Empty;
    /// <summary>Gets the service name attached to RUM and log data.</summary>
    public string ServiceName { get; init; } = "df_rum_windows";
    /// <summary>Gets the deployment environment: prod, gray, pre, common, or local.</summary>
    public string Env { get; init; } = "prod";
    /// <summary>Gets the monitored application version.</summary>
    public string Version { get; init; } = "1.0.0";
    /// <summary>Enables SDK diagnostic output.</summary>
    public bool Debug { get; init; }
    /// <summary>Gets the normal RUM session sampling rate in the inclusive range 0 through 1.</summary>
    public double SampleRate { get; init; } = 1.0;
    /// <summary>Gets the additional error-session sampling rate in the inclusive range 0 through 1.</summary>
    public double SessionErrorSampleRate { get; init; }
    /// <summary>Gets distributed trace propagation settings.</summary>
    public TraceConfig Trace { get; init; } = new();
    /// <summary>Gets application logging settings.</summary>
    public LogConfig Logging { get; init; } = new();
    /// <summary>Gets URL and HTTP header privacy settings.</summary>
    public RumPrivacyConfig Privacy { get; init; } = new();
    /// <summary>Gets an optional modifier applied to every existing RUM and log tag and field before caching.</summary>
    public DataModifier? DataModifier { get; init; }
    /// <summary>Gets an optional event-level modifier applied after <see cref="DataModifier"/>.</summary>
    public LineDataModifier? LineDataModifier { get; init; }
    /// <summary>Gets experimental Session Replay settings.</summary>
    public RumSessionReplayConfig SessionReplay { get; init; } = new();
    /// <summary>Gets shared disk-cache settings.</summary>
    public CacheOptions Cache { get; init; } = new();
    /// <summary>Gets aggregate telemetry upload limits.</summary>
    public UploadOptions Upload { get; init; } = new();
    /// <summary>Gets the interval between scheduled queue-drain attempts.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Gets the timeout applied to intake HTTP requests.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Enables compression of intake request bodies.</summary>
    public bool CompressIntakeRequests { get; init; }
    /// <summary>Gets an optional directory for persistent telemetry queues.</summary>
    public string? CacheDirectory { get; init; }
    /// <summary>Gets an optional factory for the HTTP handler used by intake transports.</summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; init; }
    /// <summary>Gets an optional provider for precise HTTP resource phase timings.</summary>
    public RumHttpResourceTimingProvider? HttpResourceTimingProvider { get; init; }
    /// <summary>Gets an optional callback for SDK diagnostic events.</summary>
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
