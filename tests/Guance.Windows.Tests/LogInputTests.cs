using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Guance.Windows.Tests;

public sealed class LogInputTests
{
    [Fact]
    public async Task AddLog_UploadsLoggingLineWithCurrentRumContext()
    {
        var requests = new List<CapturedRequest>();
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "guance-rum-log-tests", Guid.NewGuid().ToString("N"));
        await using var client = new GuanceClient(new GuanceConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app-id",
            ServiceName = "desktop-service",
            Env = "local",
            Version = "2.0.0",
            CacheDirectory = cacheDirectory,
            FlushInterval = TimeSpan.FromHours(1),
            HttpMessageHandlerFactory = () => new CaptureHandler(requests),
            Logging = new LogConfig
            {
                EnableCustomLog = true,
                EnableLinkRumData = true
            }
        });

        client.SetUser("user-1", "Ada", "ada@example.test");
        client.StartView("Checkout");
        using var action = client.StartAction("Pay", "click", needWait: true);

        client.AddLog(
            "payment failed",
            LogStatus.Error,
            new Dictionary<string, object?>
            {
                ["order_id"] = "A1001",
                ["attempt"] = 2
            });
        await client.FlushAsync();

        var request = Assert.Single(requests, item => item.Uri.AbsolutePath == "/v1/write/logging");
        Assert.StartsWith("df_rum_windows_log,", request.Body, StringComparison.Ordinal);
        Assert.Contains("app_id=app-id", request.Body, StringComparison.Ordinal);
        Assert.Contains("service=desktop-service", request.Body, StringComparison.Ordinal);
        Assert.Contains("env=local", request.Body, StringComparison.Ordinal);
        Assert.Contains("version=2.0.0", request.Body, StringComparison.Ordinal);
        Assert.Contains("sdk_name=df_windows_rum_sdk", request.Body, StringComparison.Ordinal);
        Assert.Contains("session_id=", request.Body, StringComparison.Ordinal);
        Assert.Contains("view_name=Checkout", request.Body, StringComparison.Ordinal);
        Assert.Contains("action_name=Pay", request.Body, StringComparison.Ordinal);
        Assert.Contains("userid=user-1", request.Body, StringComparison.Ordinal);
        Assert.Contains("message=\"payment failed\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("status=\"error\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("order_id=\"A1001\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("attempt=2i", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddLog_WhenDisabled_DropsWithoutRequest()
    {
        var requests = new List<CapturedRequest>();
        await using var client = CreateClient(requests, new LogConfig());

        client.AddLog("not collected", LogStatus.Info);
        await client.FlushAsync();

        Assert.DoesNotContain(requests, item => item.Uri.AbsolutePath == "/v1/write/logging");
        Assert.Equal(1, client.GetLogDiagnosticsSnapshot().LogsDroppedByConfiguration);
    }

    [Fact]
    public async Task AddLog_AppliesLevelFilterAndSamplingIndependently()
    {
        var requests = new List<CapturedRequest>();
        await using var client = CreateClient(requests, new LogConfig
        {
            EnableCustomLog = true,
            SampleRate = 0,
            LevelFilters = new[] { LogStatus.Error }
        });

        client.AddLog("filtered", LogStatus.Warning);
        client.AddLog("sampled", LogStatus.Error);
        await client.FlushAsync();

        Assert.DoesNotContain(requests, item => item.Uri.AbsolutePath == "/v1/write/logging");
        var diagnostics = client.GetLogDiagnosticsSnapshot();
        Assert.Equal(1, diagnostics.LogsDroppedByLevel);
        Assert.Equal(1, diagnostics.LogsDroppedBySampling);
    }

    [Fact]
    public async Task AddLog_WhenRumLinkIsDisabled_OmitsRumContext()
    {
        var requests = new List<CapturedRequest>();
        await using var client = CreateClient(requests, new LogConfig
        {
            EnableCustomLog = true,
            EnableLinkRumData = false
        });
        client.SetUser("user-1");
        client.StartView("Private view");
        using var action = client.StartAction("Private action", "click", needWait: true);

        client.AddLog("standalone", LogStatus.Info);
        await client.FlushAsync();

        var body = Assert.Single(requests, item => item.Uri.AbsolutePath == "/v1/write/logging").Body;
        Assert.Contains("userid=user-1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("view_id=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("action_id=", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddLog_TruncatesMessageToThirtyKilobytesAtUtf8Boundary()
    {
        var requests = new List<CapturedRequest>();
        await using var client = CreateClient(requests, new LogConfig
        {
            EnableCustomLog = true
        });

        client.AddLog(string.Concat(Enumerable.Repeat("汉", 12_000)), LogStatus.Info);
        await client.FlushAsync();

        var body = Assert.Single(requests, item => item.Uri.AbsolutePath == "/v1/write/logging").Body;
        var match = Regex.Match(body, "message=\"(?<message>[^\"]*)\"");
        Assert.True(match.Success);
        Assert.Equal(30 * 1024, Encoding.UTF8.GetByteCount(match.Groups["message"].Value));
    }

    [Fact]
    public async Task AddLog_PreservesMultilineFieldContent()
    {
        var requests = new List<CapturedRequest>();
        await using var client = CreateClient(requests, new LogConfig
        {
            EnableCustomLog = true
        });

        client.AddLog("first line\r\nsecond line\nthird line", LogStatus.Error);
        await client.FlushAsync();

        var body = Assert.Single(requests, item => item.Uri.AbsolutePath == "/v1/write/logging").Body;
        Assert.Contains("message=\"first line\r\nsecond line\nthird line\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddLogs_AcceptsEnumAndCustomStatuses()
    {
        var requests = new List<CapturedRequest>();
        await using var client = CreateClient(requests, new LogConfig
        {
            EnableCustomLog = true
        });

        client.AddLogs(new[]
        {
            new LogEntry("first", LogStatus.Ok),
            new LogEntry("second", "audit")
        });
        await client.FlushAsync();

        var bodies = requests
            .Where(item => item.Uri.AbsolutePath == "/v1/write/logging")
            .Select(item => item.Body)
            .ToArray();
        Assert.Contains(bodies, body => body.Contains("message=\"first\"", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("status=\"audit\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoggingQueue_RetriesDurablyAcrossClientRestart()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "guance-rum-log-restart-tests", Guid.NewGuid().ToString("N"));
        var failedRequests = new List<CapturedRequest>();
        await using (var firstClient = CreateClient(
            failedRequests,
            new LogConfig { EnableCustomLog = true },
            cacheDirectory,
            HttpStatusCode.ServiceUnavailable))
        {
            firstClient.AddLog("survives restart", LogStatus.Error);
            await firstClient.FlushAsync();
            Assert.True(firstClient.GetLogDiagnosticsSnapshot().UploadRetryCount >= 1);
        }

        var successfulRequests = new List<CapturedRequest>();
        await using (var secondClient = CreateClient(
            successfulRequests,
            new LogConfig { EnableCustomLog = true },
            cacheDirectory,
            HttpStatusCode.Accepted))
        {
            await secondClient.FlushAsync();
            var body = Assert.Single(
                successfulRequests,
                item => item.Uri.AbsolutePath == "/v1/write/logging").Body;
            Assert.Contains("message=\"survives restart\"", body, StringComparison.Ordinal);
            Assert.Equal(1, secondClient.GetLogDiagnosticsSnapshot().UploadSuccessCount);
        }
    }

    private static GuanceClient CreateClient(List<CapturedRequest> requests, LogConfig logging)
    {
        return CreateClient(
            requests,
            logging,
            Path.Combine(Path.GetTempPath(), "guance-rum-log-tests", Guid.NewGuid().ToString("N")),
            HttpStatusCode.Accepted);
    }

    private static GuanceClient CreateClient(
        List<CapturedRequest> requests,
        LogConfig logging,
        string cacheDirectory,
        HttpStatusCode statusCode)
    {
        return new GuanceClient(new GuanceConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app-id",
            Env = "local",
            CacheDirectory = cacheDirectory,
            FlushInterval = TimeSpan.FromHours(1),
            HttpMessageHandlerFactory = () => new CaptureHandler(requests, statusCode),
            Logging = logging
        });
    }

    private sealed record CapturedRequest(Uri Uri, string Body);

    private sealed class CaptureHandler(
        List<CapturedRequest> requests,
        HttpStatusCode statusCode = HttpStatusCode.Accepted) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (requests)
            {
                requests.Add(new CapturedRequest(request.RequestUri!, body));
            }
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8)
            };
        }
    }
}
