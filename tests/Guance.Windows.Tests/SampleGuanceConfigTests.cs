using System;
using System.Collections.Generic;
using System.IO;
using Guance.Windows;
using Guance.Windows.Samples;
using Xunit;

namespace Guance.Windows.Tests;

[Collection(nameof(SampleGuanceConfigEnvironmentCollection))]
public sealed class SampleGuanceConfigTests : IDisposable
{
    private static readonly string[] EnvironmentVariableNames =
    {
        "GUANCE_RUM_DATAWAY_URL",
        "GUANCE_RUM_DATAKIT_URL",
        "GUANCE_RUM_CLIENT_TOKEN",
        "GUANCE_RUM_APP_ID",
        "GUANCE_RUM_SERVICE_NAME",
        "GUANCE_RUM_ENV",
        "GUANCE_RUM_VERSION",
        "GUANCE_RUM_DEBUG",
        "GUANCE_RUM_SAMPLE_RATE",
        "GUANCE_LOG_ENABLED",
        "GUANCE_LOG_SAMPLE_RATE",
        "GUANCE_TRACE_ENABLED",
        "GUANCE_TRACE_SAMPLE_RATE",
        "GUANCE_TRACE_TYPE",
        "GUANCE_TRACE_ALLOWED_URLS",
        "GUANCE_RUM_MAX_CACHE_BYTES",
        "GUANCE_RUM_MAX_CACHE_FILES",
        "GUANCE_RUM_MAX_CACHE_AGE_SECONDS",
        "GUANCE_RUM_MAX_BATCH_ITEMS",
        "GUANCE_RUM_MAX_BATCH_BYTES",
        "GUANCE_RUM_MAX_UPLOAD_BYTES_PER_SECOND",
        "GUANCE_RUM_UPLOAD_BURST_BYTES",
        "GUANCE_RUM_MAX_UPLOAD_REQUESTS_PER_SECOND",
        "GUANCE_RUM_MAX_UPLOAD_BATCHES_PER_CYCLE",
        "GUANCE_RUM_SESSION_REPLAY_ENABLED",
        "GUANCE_RUM_SESSION_REPLAY_SAMPLE_RATE",
        "GUANCE_RUM_SESSION_REPLAY_ON_ERROR_SAMPLE_RATE",
        "GUANCE_RUM_REPLAY_TEXT_AND_INPUT_PRIVACY",
        "GUANCE_RUM_REPLAY_TOUCH_PRIVACY",
        "GUANCE_RUM_REPLAY_IMAGE_PRIVACY",
        "GUANCE_RUM_DIAGNOSTIC_CONSOLE",
        "GUANCE_RUM_FIRST_CHANCE_EXCEPTIONS",
        "GUANCE_RUM_WEBVIEW_TEST_URL"
    };

    private readonly string originalCurrentDirectory = Environment.CurrentDirectory;
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"guance-rum-sample-config-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> originalEnvironment = new(StringComparer.Ordinal);

    public SampleGuanceConfigTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        Environment.CurrentDirectory = temporaryDirectory;
        foreach (var name in EnvironmentVariableNames)
        {
            originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Load_UsesWebViewUrlFromLocalJson()
    {
        File.WriteAllText(
            Path.Combine(temporaryDirectory, "rum.local.json"),
            """
            {
              "rumAppId": "json-app",
              "serviceName": "json-service",
              "webViewUrl": "https://example.test/webview",
              "diagnosticConsoleEnabled": false
            }
            """);

        var config = SampleGuanceConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.Equal("json-app", config.RumAppId);
        Assert.Equal("json-service", config.ServiceName);
        Assert.Equal("https://example.test/webview", SampleGuanceConfig.WebViewUrl);
    }

    [Fact]
    public void Load_AllowsExperimentalSessionReplayFromLocalJson()
    {
        File.WriteAllText(
            Path.Combine(temporaryDirectory, "rum.local.json"),
            """
            {
              "sessionSampleRate": 80,
              "sessionReplayEnabled": true,
              "sessionReplaySampleRate": 75,
              "sessionReplayOnErrorSampleRate": 25,
              "replayTextAndInputPrivacy": "MaskSensitiveInputs",
              "replayTouchPrivacy": "Hide",
              "replayImagePrivacy": "MaskLargeOnly",
              "diagnosticConsoleEnabled": false
            }
            """);

        var config = SampleGuanceConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.Equal(0.8, config.SampleRate);
        Assert.True(config.SessionReplay.Enabled);
        Assert.Equal(0.75, config.SessionReplay.SampleRate);
        Assert.Equal(0.25, config.SessionReplay.OnErrorSampleRate);
        Assert.Equal(SessionReplayTextAndInputPrivacy.MaskSensitiveInputs, config.SessionReplay.TextAndInputPrivacy);
        Assert.Equal(SessionReplayTouchPrivacy.Hide, config.SessionReplay.TouchPrivacy);
        Assert.Equal(SessionReplayImagePrivacy.MaskLargeOnly, config.SessionReplay.ImagePrivacy);
    }

    [Fact]
    public void Load_AppliesLoggingTraceCacheAndUploadSettings()
    {
        File.WriteAllText(
            Path.Combine(temporaryDirectory, "rum.local.json"),
            """
            {
              "loggingEnabled": true,
              "logSampleRate": 60,
              "traceEnabled": true,
              "traceSampleRate": 40,
              "traceType": "w3c_traceparent",
              "allowedTracingUrls": ["https://api.example.test/v1/"],
              "maxCacheBytes": 1048576,
              "maxCacheFiles": 20,
              "maxCacheAgeSeconds": 3600,
              "maxBatchItems": 10,
              "maxBatchBytes": 65536,
              "maxUploadBytesPerSecond": 32768,
              "uploadBurstBytes": 131072,
              "maxUploadRequestsPerSecond": 1.5,
              "maxUploadBatchesPerCycle": 2,
              "diagnosticConsoleEnabled": false
            }
            """);

        var config = SampleGuanceConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.True(config.Logging.EnableCustomLog);
        Assert.Equal(0.6, config.Logging.SampleRate);
        Assert.True(config.Trace.EnableAutoTrace);
        Assert.Equal(0.4, config.Trace.SampleRate);
        Assert.Equal(TraceType.TraceParent, config.Trace.TraceType);
        Assert.True(config.Trace.ShouldTrace!(new Uri("https://api.example.test/v1/orders")));
        Assert.False(config.Trace.ShouldTrace!(new Uri("https://other.example.test/v1/orders")));
        Assert.Equal(1048576, config.Cache.MaxDiskBytes);
        Assert.Equal(20, config.Cache.MaxFiles);
        Assert.Equal(TimeSpan.FromHours(1), config.Cache.MaxAge);
        Assert.Equal(10, config.Cache.MaxBatchItems);
        Assert.Equal(65536, config.Cache.MaxBatchBytes);
        Assert.Equal(32768, config.Upload.MaxBytesPerSecond);
        Assert.Equal(131072, config.Upload.BurstBytes);
        Assert.Equal(1.5, config.Upload.MaxRequestsPerSecond);
        Assert.Equal(2, config.Upload.MaxBatchesPerCycle);
    }

    [Fact]
    public void Load_DisablesAutomaticTraceWhenAllowListIsEmpty()
    {
        File.WriteAllText(
            Path.Combine(temporaryDirectory, "rum.local.json"),
            """
            {
              "traceEnabled": true,
              "allowedTracingUrls": [],
              "diagnosticConsoleEnabled": false
            }
            """);

        var config = SampleGuanceConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.False(config.Trace.EnableAutoTrace);
        Assert.False(config.Trace.ShouldTrace!(new Uri("https://api.example.test/")));
    }

    [Fact]
    public void Load_EnvironmentWebViewUrlOverridesLocalJson()
    {
        File.WriteAllText(
            Path.Combine(temporaryDirectory, "rum.local.json"),
            """
            {
              "webViewUrl": "https://json.example.test",
              "diagnosticConsoleEnabled": false
            }
            """);
        Environment.SetEnvironmentVariable(
            "GUANCE_RUM_WEBVIEW_TEST_URL",
            "https://environment.example.test");

        _ = SampleGuanceConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.Equal(
            "https://environment.example.test",
            SampleGuanceConfig.WebViewUrl);
    }

    [Fact]
    public void Load_FindsSharedSettingsUnderSamplesFromRepositoryRoot()
    {
        var samplesDirectory = Path.Combine(temporaryDirectory, "samples");
        Directory.CreateDirectory(samplesDirectory);
        File.WriteAllText(
            Path.Combine(samplesDirectory, "rum.local.json"),
            """
            {
              "rumAppId": "shared-json-app",
              "webViewUrl": "https://shared.example.test/webview",
              "diagnosticConsoleEnabled": false
            }
            """);

        var config = SampleGuanceConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.Equal("shared-json-app", config.RumAppId);
        Assert.Equal(
            "https://shared.example.test/webview",
            SampleGuanceConfig.WebViewUrl);
    }

    [Fact]
    public void Resolve_CommandLineWebViewUrlOverridesEnvironmentAndJson()
    {
        File.WriteAllText(
            Path.Combine(temporaryDirectory, "rum.local.json"),
            """
            {
              "webViewUrl": "https://json.example.test",
              "diagnosticConsoleEnabled": false
            }
            """);
        Environment.SetEnvironmentVariable(
            "GUANCE_RUM_WEBVIEW_TEST_URL",
            "https://environment.example.test");

        var settings = SampleGuanceConfig.Resolve(
            "default-app",
            "default-service",
            new[] { "--webview-url=https://command-line.example.test" });

        Assert.Equal(
            "https://command-line.example.test",
            settings.WebViewUrl);
    }

    [Fact]
    public void Resolve_DoesNotMutateLoadedWebViewUrl()
    {
        var settingsPath = Path.Combine(temporaryDirectory, "rum.local.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "webViewUrl": "https://loaded.example.test",
              "diagnosticConsoleEnabled": false
            }
            """);
        _ = SampleGuanceConfig.Load("default-app", "default-service", Array.Empty<string>());
        File.WriteAllText(
            settingsPath,
            """
            {
              "webViewUrl": "https://resolved.example.test",
              "diagnosticConsoleEnabled": false
            }
            """);

        var settings = SampleGuanceConfig.Resolve(
            "default-app",
            "default-service",
            Array.Empty<string>());

        Assert.Equal("https://resolved.example.test", settings.WebViewUrl);
        Assert.Equal("https://loaded.example.test", SampleGuanceConfig.WebViewUrl);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = originalCurrentDirectory;
        foreach (var (name, value) in originalEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Directory.Delete(temporaryDirectory, recursive: true);
    }
}

[CollectionDefinition(
    nameof(SampleGuanceConfigEnvironmentCollection),
    DisableParallelization = true)]
public sealed class SampleGuanceConfigEnvironmentCollection
{
}
