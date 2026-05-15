using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Guance.Rum.Windows.Transport;

namespace Guance.Rum.Windows.SessionReplay;

internal sealed class SessionReplayTransport : ISessionReplayTransport
{
    private readonly RumConfig config;
    private readonly HttpClient client;

    public SessionReplayTransport(RumConfig config)
    {
        this.config = config;
        client = config.HttpMessageHandlerFactory is null
            ? new HttpClient { Timeout = config.HttpTimeout }
            : new HttpClient(config.HttpMessageHandlerFactory(), disposeHandler: true) { Timeout = config.HttpTimeout };
    }

    public async Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildIntakeUri());
            request.Headers.TryAddWithoutValidation("X-Datakit-Device-Time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            request.Headers.TryAddWithoutValidation("X-Datakit-Trace", Guid.NewGuid().ToString("N"));
            request.Content = new ByteArrayContent(segment.Body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(segment.ContentType);

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
            return SendResult.Retry(null, "session replay request timeout");
        }
        catch (HttpRequestException ex)
        {
            return SendResult.Retry(null, ex.Message);
        }
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private Uri BuildIntakeUri()
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.DatawayUrl) ? config.DatawayUrl! : config.DatakitUrl!;
        var builder = new UriBuilder(AppendPath(baseUrl, RumConstants.RumReplayWritePath));
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
