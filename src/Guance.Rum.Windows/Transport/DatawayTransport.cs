using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Guance.Rum.Windows.Queue;

namespace Guance.Rum.Windows.Transport;

internal sealed class DatawayTransport : IDatawayTransport
{
    private readonly RumConfig config;
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public DatawayTransport(RumConfig config)
    {
        this.config = config;
        if (config.HttpMessageHandlerFactory is null)
        {
            client = new HttpClient { Timeout = config.HttpTimeout };
            ownsClient = true;
        }
        else
        {
            client = new HttpClient(config.HttpMessageHandlerFactory(), disposeHandler: true)
            {
                Timeout = config.HttpTimeout
            };
            ownsClient = true;
        }
    }

    public async Task<SendResult> SendAsync(IReadOnlyList<QueuedRumEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return SendResult.Success((int)HttpStatusCode.NoContent);
        }

        var body = string.Concat(events.Select(item => item.Line));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildIntakeUri());
            request.Headers.TryAddWithoutValidation("X-Datakit-Device-Time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            request.Headers.TryAddWithoutValidation("X-Datakit-Trace", Guid.NewGuid().ToString("N"));
            request.Content = await CreateContentAsync(body, cancellationToken).ConfigureAwait(false);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (statusCode >= 200 && statusCode < 500)
            {
                return statusCode < 300
                    ? SendResult.Success(statusCode)
                    : SendResult.TerminalFailure(statusCode, response.ReasonPhrase);
            }

            return SendResult.Retry(statusCode, response.ReasonPhrase);
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

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    private Uri BuildIntakeUri()
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.DatawayUrl) ? config.DatawayUrl! : config.DatakitUrl!;
        var builder = new UriBuilder(AppendPath(baseUrl, RumConstants.RumWritePath));
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
