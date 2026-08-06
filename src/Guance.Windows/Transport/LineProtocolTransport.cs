using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Guance.Windows.Transport;

internal sealed class LineProtocolTransport : IDisposable
{
    private readonly GuanceConfig config;
    private readonly string intakePath;
    private readonly HttpClient client;

    public LineProtocolTransport(GuanceConfig config, string intakePath)
    {
        this.config = config;
        this.intakePath = intakePath;
        client = config.HttpMessageHandlerFactory is null
            ? new HttpClient { Timeout = config.HttpTimeout }
            : new HttpClient(config.HttpMessageHandlerFactory(), disposeHandler: true)
            {
                Timeout = config.HttpTimeout
            };
    }

    public async Task<SendResult> SendAsync(string body, CancellationToken cancellationToken)
    {
        if (body.Length == 0)
        {
            return SendResult.Success((int)HttpStatusCode.NoContent);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildIntakeUri());
            request.Options.Set(HttpInstrumentationMarks.SuppressResourceInstrumentation, true);
            request.Headers.TryAddWithoutValidation(
                "X-Datakit-Device-Time",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            request.Headers.TryAddWithoutValidation("X-Datakit-Trace", Guid.NewGuid().ToString("N"));
            request.Content = await CreateContentAsync(body, cancellationToken).ConfigureAwait(false);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (statusCode >= 200 && statusCode < 300)
            {
                return SendResult.Success(statusCode);
            }

            if (statusCode == (int)HttpStatusCode.RequestTimeout ||
                statusCode == (int)HttpStatusCode.TooManyRequests ||
                statusCode >= 500)
            {
                return SendResult.Retry(statusCode, response.ReasonPhrase, GetRetryAfter(response));
            }

            return SendResult.TerminalFailure(statusCode, response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SendResult.Retry(null, "request timeout");
        }
        catch (HttpRequestException ex)
        {
            return SendResult.Retry(null, ex.Message);
        }
    }

    public void Dispose() => client.Dispose();

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
        return null;
    }

    private Uri BuildIntakeUri()
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.DatawayUrl) ? config.DatawayUrl! : config.DatakitUrl!;
        var builder = new UriBuilder(AppendPath(baseUrl, intakePath));
        if (!string.IsNullOrWhiteSpace(config.DatawayUrl))
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(builder.Query))
            {
                query.Add(builder.Query.TrimStart('?'));
            }

            query.Add("token=" + Uri.EscapeDataString(config.ClientToken!));
            query.Add("to_headless=true");
            builder.Query = string.Join("&", query);
        }

        return builder.Uri;
    }

    private async Task<HttpContent> CreateContentAsync(string body, CancellationToken cancellationToken)
    {
        if (!config.CompressIntakeRequests)
        {
            return new StringContent(body, Encoding.UTF8, "text/plain");
        }

        await using var output = new MemoryStream();
        await using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            await deflate.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
        }

        var content = new ByteArrayContent(output.ToArray());
        content.Headers.ContentType = new("text/plain");
        content.Headers.ContentEncoding.Add("deflate");
        return content;
    }

    private static string AppendPath(string baseUrl, string path)
    {
        if (baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl = baseUrl[..^1];
        }

        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        return $"{baseUrl}/{path}";
    }
}
