using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Guance.Windows.Transport;

namespace Guance.Windows.SessionReplay;

internal sealed class SessionReplayTransport : ISessionReplayTransport
{
    private readonly GuanceConfig config;
    private readonly HttpClient client;

    public SessionReplayTransport(GuanceConfig config)
    {
        this.config = config;
        client = config.HttpMessageHandlerFactory is null
            ? new HttpClient { Timeout = config.HttpTimeout }
            : new HttpClient(config.HttpMessageHandlerFactory(), disposeHandler: true) { Timeout = config.HttpTimeout };
    }

    public async Task<SendResult> SendAsync(QueuedSessionReplaySegment segment, CancellationToken cancellationToken)
    {
        Uri? intakeUri = null;
        try
        {
            intakeUri = BuildIntakeUri();
            using var request = new HttpRequestMessage(HttpMethod.Post, intakeUri);
            request.Options.Set(HttpInstrumentationMarks.SuppressResourceInstrumentation, true);
            request.Headers.TryAddWithoutValidation("X-Datakit-Device-Time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            request.Headers.TryAddWithoutValidation("X-Datakit-Trace", Guid.NewGuid().ToString("N"));
            request.Content = new ByteArrayContent(segment.Body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(segment.ContentType);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (statusCode >= 200 && statusCode < 300)
            {
                return SendResult.Success(statusCode);
            }

            var error = await BuildErrorMessageAsync(response, segment, intakeUri, cancellationToken).ConfigureAwait(false);
            if (statusCode == (int)HttpStatusCode.RequestTimeout ||
                statusCode == (int)HttpStatusCode.TooManyRequests ||
                statusCode >= 500)
            {
                return SendResult.Retry(statusCode, error, GetRetryAfter(response));
            }

            return SendResult.TerminalFailure(statusCode, error);
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

    private static async Task<string> BuildErrorMessageAsync(HttpResponseMessage response, QueuedSessionReplaySegment segment, Uri? intakeUri, CancellationToken cancellationToken)
    {
        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = response.ReasonPhrase ?? "session replay upload failed";
        var details = new List<string>
        {
            message,
            $"uri={RedactUri(intakeUri)}",
            $"content_type={segment.ContentType}",
            $"body_bytes={segment.Body.LongLength}"
        };

        if (!string.IsNullOrWhiteSpace(body))
        {
            details.Add("response_body=" + Truncate(body.Trim(), 1024));
        }

        return string.Join("; ", details);
    }

    private static string RedactUri(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        var builder = new UriBuilder(uri);
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            var query = builder.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.StartsWith("token=", StringComparison.OrdinalIgnoreCase) ? "token=redacted" : part);
            builder.Query = string.Join("&", query);
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
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
