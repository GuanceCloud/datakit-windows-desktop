using System.Diagnostics;
using System.Net.Http;

namespace Guance.Rum.Windows;

public sealed class RumHttpMessageHandler : DelegatingHandler
{
    private readonly RumClient client;

    public RumHttpMessageHandler(RumClient client, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        this.client = client;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.Set(HttpInstrumentationMarks.ManualHandlerInstrumented, true);
        var resourceId = client.StartResource(request.RequestUri?.ToString() ?? string.Empty, request.Method.Method);
        var activity = Activity.Current;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseSize = response.Content.Headers.ContentLength ?? -1;
            client.StopResource(
                resourceId,
                (int)response.StatusCode,
                responseSize: responseSize,
                requestSize: request.Content?.Headers.ContentLength ?? -1,
                resourceType: response.Content.Headers.ContentType?.MediaType,
                requestHeader: HttpHeaderRedactor.Format(request.Headers, request.Content?.Headers, client.Config.Privacy),
                responseHeader: HttpHeaderRedactor.Format(response.Headers, response.Content.Headers, client.Config.Privacy),
                properties: CreateResourceProperties(request, response, activity, stopwatch, exception: null));
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.StopResource(
                resourceId,
                statusCode: 0,
                errorStack: ex.ToString(),
                errorMessage: ex.Message,
                properties: CreateResourceProperties(request, null, activity, stopwatch, ex));
            throw;
        }
    }

    private IReadOnlyDictionary<string, object?> CreateResourceProperties(HttpRequestMessage request, HttpResponseMessage? response, Activity? activity, Stopwatch stopwatch, Exception? exception)
    {
        var elapsed = stopwatch.Elapsed;
        var timing = HttpResourceTimingResolver.Resolve(client.Config, request, response, elapsed, exception, nameof(RumHttpMessageHandler));
        var properties = new Dictionary<string, object?>(timing.ToProperties())
        {
            [RumConstants.NetworkInstrumentation] = nameof(RumHttpMessageHandler),
            [RumConstants.NetworkLibrary] = "HttpClient",
            [RumConstants.TraceId] = activity?.TraceId.ToString(),
            [RumConstants.SpanId] = activity?.SpanId.ToString()
        };

        if (request.Version is not null)
        {
            properties[RumConstants.HttpRequestVersion] = request.Version.ToString();
        }

        if (response?.Version is not null)
        {
            properties[RumConstants.HttpResponseVersion] = response.Version.ToString();
            properties[RumConstants.ResourceHttpProtocol] = $"HTTP/{response.Version}";
        }

        if (request.VersionPolicy != HttpVersionPolicy.RequestVersionOrLower)
        {
            properties[RumConstants.HttpVersionPolicy] = request.VersionPolicy.ToString();
        }

        return properties;
    }
}
