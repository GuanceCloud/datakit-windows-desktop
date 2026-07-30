using System;
using System.Collections.Generic;
using System.IO;
using Guance.Rum.Windows.Samples;
using Xunit;

namespace Guance.Rum.Windows.Tests;

[Collection(nameof(SampleRumConfigEnvironmentCollection))]
public sealed class SampleRumConfigTests : IDisposable
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
        "GUANCE_RUM_DIAGNOSTIC_CONSOLE",
        "GUANCE_RUM_FIRST_CHANCE_EXCEPTIONS",
        "GUANCE_RUM_WEBVIEW_TEST_URL"
    };

    private readonly string originalCurrentDirectory = Environment.CurrentDirectory;
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"guance-rum-sample-config-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> originalEnvironment = new(StringComparer.Ordinal);

    public SampleRumConfigTests()
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

        var config = SampleRumConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.Equal("json-app", config.RumAppId);
        Assert.Equal("json-service", config.ServiceName);
        Assert.Equal("https://example.test/webview", SampleRumConfig.WebViewUrl);
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

        _ = SampleRumConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.Equal(
            "https://environment.example.test",
            SampleRumConfig.WebViewUrl);
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

        var config = SampleRumConfig.Load("default-app", "default-service", Array.Empty<string>());

        Assert.Equal("shared-json-app", config.RumAppId);
        Assert.Equal(
            "https://shared.example.test/webview",
            SampleRumConfig.WebViewUrl);
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

        var settings = SampleRumConfig.Resolve(
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
        _ = SampleRumConfig.Load("default-app", "default-service", Array.Empty<string>());
        File.WriteAllText(
            settingsPath,
            """
            {
              "webViewUrl": "https://resolved.example.test",
              "diagnosticConsoleEnabled": false
            }
            """);

        var settings = SampleRumConfig.Resolve(
            "default-app",
            "default-service",
            Array.Empty<string>());

        Assert.Equal("https://resolved.example.test", settings.WebViewUrl);
        Assert.Equal("https://loaded.example.test", SampleRumConfig.WebViewUrl);
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
    nameof(SampleRumConfigEnvironmentCollection),
    DisableParallelization = true)]
public sealed class SampleRumConfigEnvironmentCollection
{
}
