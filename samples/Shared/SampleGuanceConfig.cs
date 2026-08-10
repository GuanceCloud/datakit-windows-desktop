using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Guance.Windows;

namespace Guance.Windows.Samples;

internal static class SampleGuanceConfig
{
    private const string LocalSettingsFileName = "rum.local.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string? WebViewUrl { get; private set; }

    public static GuanceConfig Load(string defaultRumAppId, string defaultServiceName, string[]? args = null)
    {
        var settings = ResolveLocalSettings(defaultRumAppId, defaultServiceName, args);
        var resolved = CreateResolvedSettings(settings, defaultRumAppId, defaultServiceName);
        WebViewUrl = resolved.WebViewUrl;
        return resolved.Rum;
    }

    public static SampleRumSettings Resolve(
        string defaultRumAppId,
        string defaultServiceName,
        string[]? args = null)
    {
        var settings = ResolveLocalSettings(defaultRumAppId, defaultServiceName, args);
        return CreateResolvedSettings(settings, defaultRumAppId, defaultServiceName);
    }

    private static LocalRumSettings ResolveLocalSettings(
        string defaultRumAppId,
        string defaultServiceName,
        string[]? args)
    {
        var settings = new LocalRumSettings
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = defaultRumAppId,
            ServiceName = defaultServiceName,
            Env = "local",
            Version = "1.0.0",
        };

        ApplyJsonSettings(settings);
        ApplyEnvironment(settings);
        ApplyCommandLine(settings, args ?? Environment.GetCommandLineArgs().Skip(1).ToArray());
        return settings;
    }

    private static SampleRumSettings CreateResolvedSettings(
        LocalRumSettings settings,
        string defaultRumAppId,
        string defaultServiceName)
    {
        var rum = new GuanceConfig
        {
            DatawayUrl = EmptyToNull(settings.DatawayUrl),
            DatakitUrl = EmptyToNull(settings.DatakitUrl),
            ClientToken = EmptyToNull(settings.ClientToken),
            RumAppId = settings.RumAppId ?? defaultRumAppId,
            ServiceName = settings.ServiceName ?? defaultServiceName,
            Env = settings.Env ?? "local",
            Version = settings.Version ?? "1.0.0",
            SampleRate = PercentageToRate(settings.SessionSampleRate ?? 100),
            Logging = new LogConfig
            {
                EnableCustomLog = settings.LoggingEnabled ?? false,
                EnableLinkRumData = true,
                SampleRate = PercentageToRate(settings.LogSampleRate ?? 100)
            },
            Trace = CreateTraceConfig(settings),
            Cache = new CacheOptions
            {
                MaxDiskBytes = settings.MaxCacheBytes ?? 128L * 1024 * 1024,
                MaxFiles = settings.MaxCacheFiles ?? 1_024,
                MaxAge = TimeSpan.FromSeconds(settings.MaxCacheAgeSeconds ?? 7L * 24 * 60 * 60),
                MaxBatchItems = settings.MaxBatchItems ?? 50,
                MaxBatchBytes = settings.MaxBatchBytes ?? 512L * 1024
            },
            Upload = new UploadOptions
            {
                MaxBytesPerSecond = settings.MaxUploadBytesPerSecond ?? 256L * 1024,
                BurstBytes = settings.UploadBurstBytes ?? 2L * 1024 * 1024,
                MaxRequestsPerSecond = settings.MaxUploadRequestsPerSecond ?? 2,
                MaxBatchesPerCycle = settings.MaxUploadBatchesPerCycle ?? 4
            },
            SessionReplay = new RumSessionReplayConfig
            {
                Enabled = settings.SessionReplayEnabled ?? false,
                SampleRate = PercentageToRate(settings.SessionReplaySampleRate ?? 100),
                OnErrorSampleRate = PercentageToRate(settings.SessionReplayOnErrorSampleRate ?? 0),
                TextAndInputPrivacy = ParseEnum(
                    settings.ReplayTextAndInputPrivacy,
                    SessionReplayTextAndInputPrivacy.MaskAll),
                TouchPrivacy = ParseEnum(
                    settings.ReplayTouchPrivacy,
                    SessionReplayTouchPrivacy.Show),
                ImagePrivacy = ParseEnum(
                    settings.ReplayImagePrivacy,
                    SessionReplayImagePrivacy.MaskAll)
            }
        };
        return new SampleRumSettings(rum, EmptyToNull(settings.WebViewUrl));
    }

    private static TraceConfig CreateTraceConfig(LocalRumSettings settings)
    {
        var allowedUrls = (settings.AllowedTracingUrls ?? Array.Empty<string>())
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Cast<Uri>()
            .ToArray();

        return new TraceConfig
        {
            EnableAutoTrace = settings.TraceEnabled == true && allowedUrls.Length > 0,
            EnableLinkRumData = true,
            SampleRate = PercentageToRate(settings.TraceSampleRate ?? 100),
            TraceType = ParseTraceType(settings.TraceType),
            ShouldTrace = requestUri => allowedUrls.Any(allowed => allowed.IsBaseOf(requestUri))
        };
    }

    private static void ApplyJsonSettings(LocalRumSettings settings)
    {
        var file = FindLocalSettingsFile();
        if (file is null)
        {
            return;
        }

        var json = File.ReadAllText(file);
        var local = JsonSerializer.Deserialize<LocalRumSettings>(json, JsonOptions);
        if (local is not null)
        {
            Apply(settings, local);
        }
    }

    private static void ApplyEnvironment(LocalRumSettings settings)
    {
        Apply(settings, new LocalRumSettings
        {
            DatawayUrl = Environment.GetEnvironmentVariable("GUANCE_RUM_DATAWAY_URL"),
            DatakitUrl = Environment.GetEnvironmentVariable("GUANCE_RUM_DATAKIT_URL"),
            ClientToken = Environment.GetEnvironmentVariable("GUANCE_RUM_CLIENT_TOKEN"),
            RumAppId = Environment.GetEnvironmentVariable("GUANCE_RUM_APP_ID"),
            ServiceName = Environment.GetEnvironmentVariable("GUANCE_RUM_SERVICE_NAME"),
            Env = Environment.GetEnvironmentVariable("GUANCE_RUM_ENV"),
            Version = Environment.GetEnvironmentVariable("GUANCE_RUM_VERSION"),
            SessionSampleRate = ParseDouble(Environment.GetEnvironmentVariable("GUANCE_RUM_SAMPLE_RATE")),
            LoggingEnabled = ParseBool(Environment.GetEnvironmentVariable("GUANCE_LOG_ENABLED")),
            LogSampleRate = ParseDouble(Environment.GetEnvironmentVariable("GUANCE_LOG_SAMPLE_RATE")),
            TraceEnabled = ParseBool(Environment.GetEnvironmentVariable("GUANCE_TRACE_ENABLED")),
            TraceSampleRate = ParseDouble(Environment.GetEnvironmentVariable("GUANCE_TRACE_SAMPLE_RATE")),
            TraceType = Environment.GetEnvironmentVariable("GUANCE_TRACE_TYPE"),
            AllowedTracingUrls = SplitList(Environment.GetEnvironmentVariable("GUANCE_TRACE_ALLOWED_URLS")),
            MaxCacheBytes = ParseLong(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_CACHE_BYTES")),
            MaxCacheFiles = ParseInt(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_CACHE_FILES")),
            MaxCacheAgeSeconds = ParseLong(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_CACHE_AGE_SECONDS")),
            MaxBatchItems = ParseInt(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_BATCH_ITEMS")),
            MaxBatchBytes = ParseLong(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_BATCH_BYTES")),
            MaxUploadBytesPerSecond = ParseLong(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_UPLOAD_BYTES_PER_SECOND")),
            UploadBurstBytes = ParseLong(Environment.GetEnvironmentVariable("GUANCE_RUM_UPLOAD_BURST_BYTES")),
            MaxUploadRequestsPerSecond = ParseDouble(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_UPLOAD_REQUESTS_PER_SECOND")),
            MaxUploadBatchesPerCycle = ParseInt(Environment.GetEnvironmentVariable("GUANCE_RUM_MAX_UPLOAD_BATCHES_PER_CYCLE")),
            SessionReplayEnabled = ParseBool(Environment.GetEnvironmentVariable("GUANCE_RUM_SESSION_REPLAY_ENABLED")),
            SessionReplaySampleRate = ParseDouble(Environment.GetEnvironmentVariable("GUANCE_RUM_SESSION_REPLAY_SAMPLE_RATE")),
            SessionReplayOnErrorSampleRate = ParseDouble(Environment.GetEnvironmentVariable("GUANCE_RUM_SESSION_REPLAY_ON_ERROR_SAMPLE_RATE")),
            ReplayTextAndInputPrivacy = Environment.GetEnvironmentVariable("GUANCE_RUM_REPLAY_TEXT_AND_INPUT_PRIVACY"),
            ReplayTouchPrivacy = Environment.GetEnvironmentVariable("GUANCE_RUM_REPLAY_TOUCH_PRIVACY"),
            ReplayImagePrivacy = Environment.GetEnvironmentVariable("GUANCE_RUM_REPLAY_IMAGE_PRIVACY"),
            WebViewUrl = Environment.GetEnvironmentVariable("GUANCE_RUM_WEBVIEW_TEST_URL")
        });
    }

    private static void ApplyCommandLine(LocalRumSettings settings, string[] args)
    {
        var commandLine = new LocalRumSettings();
        foreach (var arg in args)
        {
            var (key, value) = SplitArgument(arg);
            switch (key)
            {
                case "--dataway-url":
                    commandLine.DatawayUrl = value;
                    break;
                case "--datakit-url":
                    commandLine.DatakitUrl = value;
                    break;
                case "--client-token":
                    commandLine.ClientToken = value;
                    break;
                case "--rum-app-id":
                    commandLine.RumAppId = value;
                    break;
                case "--service-name":
                    commandLine.ServiceName = value;
                    break;
                case "--env":
                    commandLine.Env = value;
                    break;
                case "--version":
                    commandLine.Version = value;
                    break;
                case "--session-sample-rate":
                    commandLine.SessionSampleRate = ParseDouble(value);
                    break;
                case "--logging-enabled":
                    commandLine.LoggingEnabled = ParseBool(value);
                    break;
                case "--log-sample-rate":
                    commandLine.LogSampleRate = ParseDouble(value);
                    break;
                case "--trace-enabled":
                    commandLine.TraceEnabled = ParseBool(value);
                    break;
                case "--trace-sample-rate":
                    commandLine.TraceSampleRate = ParseDouble(value);
                    break;
                case "--trace-type":
                    commandLine.TraceType = value;
                    break;
                case "--allowed-tracing-urls":
                    commandLine.AllowedTracingUrls = SplitList(value);
                    break;
                case "--session-replay-enabled":
                    commandLine.SessionReplayEnabled = ParseBool(value);
                    break;
                case "--session-replay-sample-rate":
                    commandLine.SessionReplaySampleRate = ParseDouble(value);
                    break;
                case "--session-replay-on-error-sample-rate":
                    commandLine.SessionReplayOnErrorSampleRate = ParseDouble(value);
                    break;
                case "--webview-url":
                    commandLine.WebViewUrl = value;
                    break;
            }
        }

        Apply(settings, commandLine);
    }

    private static void Apply(LocalRumSettings target, LocalRumSettings source)
    {
        if (!string.IsNullOrWhiteSpace(source.DatawayUrl))
        {
            target.DatawayUrl = source.DatawayUrl;
            target.DatakitUrl = null;
        }

        if (!string.IsNullOrWhiteSpace(source.DatakitUrl))
        {
            target.DatakitUrl = source.DatakitUrl;
            target.DatawayUrl = null;
            target.ClientToken = null;
        }

        target.ClientToken = Coalesce(source.ClientToken, target.ClientToken);
        target.RumAppId = Coalesce(source.RumAppId, target.RumAppId);
        target.ServiceName = Coalesce(source.ServiceName, target.ServiceName);
        target.Env = Coalesce(source.Env, target.Env);
        target.Version = Coalesce(source.Version, target.Version);
        target.SessionSampleRate = source.SessionSampleRate ?? target.SessionSampleRate;
        target.LoggingEnabled = source.LoggingEnabled ?? target.LoggingEnabled;
        target.LogSampleRate = source.LogSampleRate ?? target.LogSampleRate;
        target.TraceEnabled = source.TraceEnabled ?? target.TraceEnabled;
        target.TraceSampleRate = source.TraceSampleRate ?? target.TraceSampleRate;
        target.TraceType = Coalesce(source.TraceType, target.TraceType);
        if (source.AllowedTracingUrls is not null)
        {
            target.AllowedTracingUrls = source.AllowedTracingUrls;
        }
        target.MaxCacheBytes = source.MaxCacheBytes ?? target.MaxCacheBytes;
        target.MaxCacheFiles = source.MaxCacheFiles ?? target.MaxCacheFiles;
        target.MaxCacheAgeSeconds = source.MaxCacheAgeSeconds ?? target.MaxCacheAgeSeconds;
        target.MaxBatchItems = source.MaxBatchItems ?? target.MaxBatchItems;
        target.MaxBatchBytes = source.MaxBatchBytes ?? target.MaxBatchBytes;
        target.MaxUploadBytesPerSecond = source.MaxUploadBytesPerSecond ?? target.MaxUploadBytesPerSecond;
        target.UploadBurstBytes = source.UploadBurstBytes ?? target.UploadBurstBytes;
        target.MaxUploadRequestsPerSecond = source.MaxUploadRequestsPerSecond ?? target.MaxUploadRequestsPerSecond;
        target.MaxUploadBatchesPerCycle = source.MaxUploadBatchesPerCycle ?? target.MaxUploadBatchesPerCycle;
        target.SessionReplayEnabled = source.SessionReplayEnabled ?? target.SessionReplayEnabled;
        target.SessionReplaySampleRate = source.SessionReplaySampleRate ?? target.SessionReplaySampleRate;
        target.SessionReplayOnErrorSampleRate = source.SessionReplayOnErrorSampleRate ?? target.SessionReplayOnErrorSampleRate;
        target.ReplayTextAndInputPrivacy = Coalesce(source.ReplayTextAndInputPrivacy, target.ReplayTextAndInputPrivacy);
        target.ReplayTouchPrivacy = Coalesce(source.ReplayTouchPrivacy, target.ReplayTouchPrivacy);
        target.ReplayImagePrivacy = Coalesce(source.ReplayImagePrivacy, target.ReplayImagePrivacy);
        target.WebViewUrl = Coalesce(source.WebViewUrl, target.WebViewUrl);
    }

    private static string? FindLocalSettingsFile()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                foreach (var relativePath in new[]
                {
                    LocalSettingsFileName,
                    Path.Combine("samples", LocalSettingsFileName)
                })
                {
                    var candidate = Path.Combine(directory.FullName, relativePath);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static (string Key, string? Value) SplitArgument(string arg)
    {
        var index = arg.IndexOf('=');
        if (index < 0)
        {
            return (arg.Trim(), "true");
        }

        return (arg[..index].Trim(), arg[(index + 1)..].Trim());
    }

    private static string? Coalesce(string? first, string? second)
    {
        return string.IsNullOrWhiteSpace(first) ? second : first;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? ParseBool(string? value)
    {
        return bool.TryParse(value, out var result) ? result : null;
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, out var result) ? result : null;
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, out var result) ? result : null;
    }

    private static string[]? SplitList(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static double PercentageToRate(double value)
    {
        return Math.Clamp(value, 0, 100) / 100;
    }

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum
    {
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
    }

    private static TraceType ParseTraceType(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "ddtrace" or "dd_trace" => TraceType.DdTrace,
            "zipkin_multi_header" => TraceType.ZipkinMultiHeader,
            "zipkin_single_header" => TraceType.ZipkinSingleHeader,
            "traceparent" or "w3c_traceparent" => TraceType.TraceParent,
            "skywalking" => TraceType.SkyWalking,
            "jaeger" => TraceType.Jaeger,
            _ => TraceType.DdTrace
        };
    }

    private sealed class LocalRumSettings
    {
        public string? DatawayUrl { get; set; }
        public string? DatakitUrl { get; set; }
        public string? ClientToken { get; set; }
        public string? RumAppId { get; set; }
        public string? ServiceName { get; set; }
        public string? Env { get; set; }
        public string? Version { get; set; }
        public double? SessionSampleRate { get; set; }
        public bool? LoggingEnabled { get; set; }
        public double? LogSampleRate { get; set; }
        public bool? TraceEnabled { get; set; }
        public double? TraceSampleRate { get; set; }
        public string? TraceType { get; set; }
        public string[]? AllowedTracingUrls { get; set; }
        public long? MaxCacheBytes { get; set; }
        public int? MaxCacheFiles { get; set; }
        public long? MaxCacheAgeSeconds { get; set; }
        public int? MaxBatchItems { get; set; }
        public long? MaxBatchBytes { get; set; }
        public long? MaxUploadBytesPerSecond { get; set; }
        public long? UploadBurstBytes { get; set; }
        public double? MaxUploadRequestsPerSecond { get; set; }
        public int? MaxUploadBatchesPerCycle { get; set; }
        public bool? SessionReplayEnabled { get; set; }
        public double? SessionReplaySampleRate { get; set; }
        public double? SessionReplayOnErrorSampleRate { get; set; }
        public string? ReplayTextAndInputPrivacy { get; set; }
        public string? ReplayTouchPrivacy { get; set; }
        public string? ReplayImagePrivacy { get; set; }
        public string? WebViewUrl { get; set; }
    }
}

internal sealed record SampleRumSettings(GuanceConfig Rum, string? WebViewUrl);
