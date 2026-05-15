using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Guance.Rum.Windows.Queue;
using Guance.Rum.Windows.Transport;
using Xunit;

namespace Guance.Rum.Windows.Tests;

public sealed class DatawayTransportTests
{
    [Fact]
    public void HttpHeaderRedactor_MasksSensitiveHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        request.Headers.TryAddWithoutValidation("User-Agent", "sdk-test");

        var text = HttpHeaderRedactor.Format(request.Headers);

        Assert.Contains("Authorization: <redacted>", text, StringComparison.Ordinal);
        Assert.Contains("User-Agent: sdk-test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpHeaderRedactor_UsesPrivacyOverridesForHeadersAndQuery()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.TryAddWithoutValidation("X-Custom-Secret", "secret");
        request.Headers.TryAddWithoutValidation("User-Agent", "sdk-test");
        var privacy = new RumPrivacyConfig
        {
            RedactedHeaderNames = new[] { "X-Custom-Secret" },
            RedactedQueryParameterNames = new[] { "token" },
            RedactedValue = "redacted"
        };

        var text = HttpHeaderRedactor.Format(request.Headers, privacy: privacy);
        var url = HttpHeaderRedactor.RedactUrl("https://example.com/api?token=secret&keep=1#frag", privacy);

        Assert.Contains("X-Custom-Secret: redacted", text, StringComparison.Ordinal);
        Assert.Contains("User-Agent: sdk-test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.Equal("https://example.com/api?token=redacted&keep=1#frag", url);
    }

    [Fact]
    public void HttpHeaderRedactor_CanDropHeadersAndQueryString()
    {
        var privacy = new RumPrivacyConfig
        {
            CaptureHttpHeaders = false,
            CaptureUrlQueryString = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");

        Assert.Null(HttpHeaderRedactor.Format(request.Headers, privacy: privacy));
        Assert.Equal("https://example.com/api#frag", HttpHeaderRedactor.RedactUrl("https://example.com/api?token=secret#frag", privacy));
    }

    [Fact]
    public async Task SendAsync_AppendsTokenForDataway()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var transport = new DatawayTransport(new RumConfig
        {
            DatawayUrl = "https://openway.guance.com",
            ClientToken = "token value",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => handler
        });

        var result = await transport.SendAsync(new[] { new QueuedRumEvent(1, "view a=1i 1\n", DateTimeOffset.UtcNow) }, CancellationToken.None);

        Assert.True(result.DeleteFromQueue);
        Assert.Equal("https://openway.guance.com/v1/write/rum?token=token%20value&to_headless=true", handler.RequestUri!.AbsoluteUri);
        Assert.Equal("view a=1i 1\n", handler.Body);
    }

    [Fact]
    public async Task SendAsync_RetriesServerErrorsButDropsClientErrors()
    {
        var retryHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var retryTransport = new DatawayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => retryHandler
        });
        var retry = await retryTransport.SendAsync(new[] { new QueuedRumEvent(1, "x y=1i 1\n", DateTimeOffset.UtcNow) }, CancellationToken.None);
        Assert.True(retry.RetryLater);
        Assert.False(retry.DeleteFromQueue);

        var dropHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var dropTransport = new DatawayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => dropHandler
        });
        var drop = await dropTransport.SendAsync(new[] { new QueuedRumEvent(1, "x y=1i 1\n", DateTimeOffset.UtcNow) }, CancellationToken.None);
        Assert.True(drop.DeleteFromQueue);
        Assert.False(drop.RetryLater);
    }

    [Fact]
    public async Task SendAsync_CompressesDeflateBodyWhenEnabled()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var transport = new DatawayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            CompressIntakeRequests = true,
            HttpMessageHandlerFactory = () => handler
        });

        await transport.SendAsync(new[] { new QueuedRumEvent(1, "view a=1i 1\n", DateTimeOffset.UtcNow) }, CancellationToken.None);

        Assert.Contains("deflate", handler.ContentEncodings);
        Assert.NotNull(handler.BodyBytes);
        await using var input = new MemoryStream(handler.BodyBytes!);
        await using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        Assert.Equal("view a=1i 1\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SendAsync_RetriesTimeoutAndNetworkFailures()
    {
        using var timeoutTransport = new DatawayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new ThrowingHandler(new OperationCanceledException("timeout"))
        });
        var timeout = await timeoutTransport.SendAsync(new[] { new QueuedRumEvent(1, "x y=1i 1\n", DateTimeOffset.UtcNow) }, CancellationToken.None);
        Assert.True(timeout.RetryLater);
        Assert.False(timeout.DeleteFromQueue);

        using var networkTransport = new DatawayTransport(new RumConfig
        {
            DatakitUrl = "http://127.0.0.1:9529",
            RumAppId = "app",
            HttpMessageHandlerFactory = () => new ThrowingHandler(new HttpRequestException("network down"))
        });
        var network = await networkTransport.SendAsync(new[] { new QueuedRumEvent(1, "x y=1i 1\n", DateTimeOffset.UtcNow) }, CancellationToken.None);
        Assert.True(network.RetryLater);
        Assert.False(network.DeleteFromQueue);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public CaptureHandler(HttpResponseMessage response)
        {
            this.response = response;
        }

        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public byte[]? BodyBytes { get; private set; }
        public IReadOnlyList<string> ContentEncodings { get; private set; } = Array.Empty<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ContentEncodings = request.Content?.Headers.ContentEncoding.ToArray() ?? Array.Empty<string>();
            BodyBytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Body = request.Content is null || ContentEncodings.Contains("deflate", StringComparer.OrdinalIgnoreCase)
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception exception;

        public ThrowingHandler(Exception exception)
        {
            this.exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw exception;
        }
    }
}
