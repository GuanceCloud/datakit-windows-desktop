using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.SessionReplay;
using Guance.Rum.Windows.Transport;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class TraceContextTests
{
    [Theory]
    [InlineData(RumTraceType.DdTrace, "x-datadog-trace-id")]
    [InlineData(RumTraceType.ZipkinMultiHeader, "X-B3-TraceId")]
    [InlineData(RumTraceType.ZipkinSingleHeader, "b3")]
    [InlineData(RumTraceType.TraceParent, "traceparent")]
    [InlineData(RumTraceType.SkyWalking, "sw8")]
    [InlineData(RumTraceType.Jaeger, "uber-trace-id")]
    public async Task HttpMessageHandler_InjectsConfiguredTraceAndLinksSameIds(
        RumTraceType traceType,
        string expectedHeader)
    {
        var queue = new MemoryRumQueue();
        HttpRequestMessage? captured = null;
        await using var client = CreateClient(
            queue,
            new RumTraceConfig
            {
                EnableAutoTrace = true,
                EnableLinkRumData = true,
                TraceType = traceType
            });
        using var http = new HttpClient(new RumHttpMessageHandler(
            client,
            new CapturingHttpHandler(request => captured = request)));

        using var response = await http.GetAsync("https://api.example.com/v1/items");
        await client.FlushAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(captured);
        Assert.True(captured!.Headers.Contains(expectedHeader));
        var (traceId, spanId) = ExtractIds(captured, traceType);
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.False(string.IsNullOrWhiteSpace(spanId));
        var line = Assert.Single(await queue.PeekAsync(10, CancellationToken.None)).Line;
        Assert.Contains($"trace_id={traceId}", line, StringComparison.Ordinal);
        Assert.Contains($"span_id={spanId}", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpMessageHandler_AutoTraceWithoutRumLinkOnlyInjectsHeaders()
    {
        var queue = new MemoryRumQueue();
        HttpRequestMessage? captured = null;
        await using var client = CreateClient(
            queue,
            new RumTraceConfig
            {
                EnableAutoTrace = true,
                EnableLinkRumData = false,
                TraceType = RumTraceType.TraceParent
            });
        using var http = new HttpClient(new RumHttpMessageHandler(
            client,
            new CapturingHttpHandler(request => captured = request)));

        using var response = await http.GetAsync("https://api.example.com/no-link");
        await client.FlushAsync();

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.Contains("traceparent"));
        var line = Assert.Single(await queue.PeekAsync(10, CancellationToken.None)).Line;
        Assert.DoesNotContain("trace_id=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("span_id=", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticHttpClientInstrumentation_InjectsTraceWithoutCustomHandler()
    {
        var queue = new MemoryRumQueue();
        await using var client = CreateClient(
            queue,
            new RumTraceConfig
            {
                EnableAutoTrace = true,
                EnableLinkRumData = true,
                TraceType = RumTraceType.TraceParent
            });
        client.EnableAutomaticInstrumentation(new AutomaticInstrumentationOptions
        {
            EnableHttpClient = true,
            EnableWpf = false,
            EnableWinForms = false,
            EnableWinUI = false,
            EnableWebView = false,
            EnableUnhandledException = false,
            EnableUiThreadBlock = false,
            EnableAppLaunch = false
        });
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestTask = ReadSingleRequestAsync(listener, timeout.Token);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        using var response = await http.GetAsync($"http://127.0.0.1:{port}/automatic");
        var rawRequest = await requestTask;
        await client.FlushAsync();

        var traceparent = ReadHeader(rawRequest, "traceparent");
        var (traceId, spanId) = Split(traceparent, '-', 1, 2);
        var line = Assert.Single(await queue.PeekAsync(10, CancellationToken.None)).Line;
        Assert.Contains($"trace_id={traceId}", line, StringComparison.Ordinal);
        Assert.Contains($"span_id={spanId}", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpMessageHandler_TraceFilterSkipsUntrustedUrl()
    {
        var queue = new MemoryRumQueue();
        HttpRequestMessage? captured = null;
        await using var client = CreateClient(
            queue,
            new RumTraceConfig
            {
                EnableAutoTrace = true,
                EnableLinkRumData = true,
                TraceType = RumTraceType.TraceParent,
                ShouldTrace = uri => uri.Host == "trusted.example.com"
            });
        using var http = new HttpClient(new RumHttpMessageHandler(
            client,
            new CapturingHttpHandler(request => captured = request)));

        using var response = await http.GetAsync("https://untrusted.example.com/items");
        await client.FlushAsync();

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.Contains("traceparent"));
        var line = Assert.Single(await queue.PeekAsync(10, CancellationToken.None)).Line;
        Assert.DoesNotContain("trace_id=", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpMessageHandler_UsesCustomTraceContextProvider()
    {
        var queue = new MemoryRumQueue();
        HttpRequestMessage? captured = null;
        await using var client = CreateClient(
            queue,
            new RumTraceConfig
            {
                EnableAutoTrace = true,
                EnableLinkRumData = true,
                ContextProvider = _ => new RumTraceContext(
                    new Dictionary<string, string>
                    {
                        ["x-company-trace"] = "company-header"
                    },
                    "company-trace-id",
                    "company-span-id")
            });
        using var http = new HttpClient(new RumHttpMessageHandler(
            client,
            new CapturingHttpHandler(request => captured = request)));

        using var response = await http.GetAsync("https://api.example.com/custom");
        await client.FlushAsync();

        Assert.Equal("company-header", Assert.Single(captured!.Headers.GetValues("x-company-trace")));
        var line = Assert.Single(await queue.PeekAsync(10, CancellationToken.None)).Line;
        Assert.Contains("trace_id=company-trace-id", line, StringComparison.Ordinal);
        Assert.Contains("span_id=company-span-id", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCustomTraceHeadersDoNotPartiallyModifyRequestOrLinkRum()
    {
        var queue = new MemoryRumQueue();
        HttpRequestMessage? captured = null;
        await using var client = CreateClient(
            queue,
            new RumTraceConfig
            {
                EnableAutoTrace = true,
                EnableLinkRumData = true,
                ContextProvider = _ => new RumTraceContext(
                    new Dictionary<string, string>
                    {
                        ["x-company-trace"] = "must-not-leak",
                        ["invalid:header"] = "invalid"
                    },
                    "company-trace-id",
                    "company-span-id")
            });
        using var http = new HttpClient(new RumHttpMessageHandler(
            client,
            new CapturingHttpHandler(request => captured = request)));

        using var response = await http.GetAsync("https://api.example.com/invalid-custom");
        await client.FlushAsync();

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.Contains("x-company-trace"));
        var line = Assert.Single(await queue.PeekAsync(10, CancellationToken.None)).Line;
        Assert.DoesNotContain("trace_id=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("span_id=", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RumTraceType.DdTrace, "x-datadog-sampling-priority", "-1")]
    [InlineData(RumTraceType.ZipkinMultiHeader, "X-B3-Sampled", "0")]
    [InlineData(RumTraceType.TraceParent, "traceparent", "-00")]
    [InlineData(RumTraceType.SkyWalking, "sw8", "0-")]
    public async Task ZeroSamplingRatePropagatesUnsampledDecision(
        RumTraceType traceType,
        string headerName,
        string expectedValueFragment)
    {
        HttpRequestMessage? captured = null;
        await using var client = CreateClient(
            new MemoryRumQueue(),
            new RumTraceConfig
            {
                EnableAutoTrace = true,
                SampleRate = 0,
                TraceType = traceType
            });
        using var http = new HttpClient(new RumHttpMessageHandler(
            client,
            new CapturingHttpHandler(request => captured = request)));

        using var response = await http.GetAsync("https://api.example.com/unsampled");

        var value = Assert.Single(captured!.Headers.GetValues(headerName));
        Assert.Contains(expectedValueFragment, value, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceSamplingRateOutsideRangeIsRejected()
    {
        var config = new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            Trace = new RumTraceConfig { SampleRate = 1.1 }
        };

        Assert.Throws<InvalidOperationException>(() => new RumClient(config));
    }

    private static RumClient CreateClient(MemoryRumQueue queue, RumTraceConfig trace)
    {
        return new RumClient(
            new RumConfig
            {
                DatakitUrl = "http://127.0.0.1:9529",
                RumAppId = "app",
                ServiceName = "windows-trace-tests",
                Trace = trace,
                FlushInterval = TimeSpan.FromHours(1)
            },
            queue,
            new RetryRumTransport(),
            new MemorySessionReplayQueue(),
            new RetrySessionReplayTransport());
    }

    private static (string TraceId, string SpanId) ExtractIds(
        HttpRequestMessage request,
        RumTraceType traceType)
    {
        string Header(string name) => Assert.Single(request.Headers.GetValues(name));
        return traceType switch
        {
            RumTraceType.DdTrace =>
                (Header("x-datadog-trace-id"), Header("x-datadog-parent-id")),
            RumTraceType.ZipkinMultiHeader =>
                (Header("X-B3-TraceId"), Header("X-B3-SpanId")),
            RumTraceType.ZipkinSingleHeader => Split(Header("b3"), '-'),
            RumTraceType.TraceParent => Split(Header("traceparent"), '-', 1, 2),
            RumTraceType.Jaeger => Split(Header("uber-trace-id"), ':'),
            RumTraceType.SkyWalking => ExtractSkyWalking(Header("sw8")),
            _ => throw new ArgumentOutOfRangeException(nameof(traceType))
        };
    }

    private static (string TraceId, string SpanId) Split(
        string value,
        char separator,
        int traceIndex = 0,
        int spanIndex = 1)
    {
        var parts = value.Split(separator);
        return (parts[traceIndex], parts[spanIndex]);
    }

    private static (string TraceId, string SpanId) ExtractSkyWalking(string value)
    {
        var parts = value.Split('-');
        var traceId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        var parentTraceId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
        return (traceId, parentTraceId + "0");
    }

    private static string ReadHeader(string request, string name)
    {
        var prefix = name + ":";
        var line = request.Split("\r\n", StringSplitOptions.None)
            .Single(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line[(line.IndexOf(':') + 1)..].Trim();
    }

    private static async Task<string> ReadSingleRequestAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = tcpClient.GetStream();
        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }
            request.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        return request.ToString();
    }

    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage> capture;

        public CapturingHttpHandler(Action<HttpRequestMessage> capture)
        {
            this.capture = capture;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class RetryRumTransport : IDatawayTransport
    {
        public Task<SendResult> SendAsync(
            IReadOnlyList<QueuedRumEvent> events,
            CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "retain trace test data"));

        public void Dispose() { }
    }

    private sealed class MemorySessionReplayQueue : ISessionReplayQueue
    {
        public Task EnqueueAsync(string contentType, byte[] body, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<QueuedSessionReplaySegment>> PeekAsync(
            int count,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueuedSessionReplaySegment>>(
                Array.Empty<QueuedSessionReplaySegment>());

        public Task DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task TrimAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RetrySessionReplayTransport : ISessionReplayTransport
    {
        public Task<SendResult> SendAsync(
            QueuedSessionReplaySegment segment,
            CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Retry(500, "retain replay test data"));

        public void Dispose() { }
    }
}
