using System.Diagnostics;
using System.Net.Http;

namespace Guance.Windows;

/// <summary>Tracks HTTP resources and injects configured trace headers for requests sent through the handler.</summary>
public sealed class RumHttpMessageHandler : DelegatingHandler
{
    private readonly GuanceClient client;

    /// <summary>Creates an instrumented HTTP delegating handler.</summary>
    /// <param name="client">The Guance client that owns the resource lifecycle.</param>
    /// <param name="innerHandler">The handler that sends the request.</param>
    public RumHttpMessageHandler(GuanceClient client, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        this.client = client;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.Set(HttpInstrumentationMarks.ManualHandlerInstrumented, true);
        var traceContext = TraceContextFactory.Create(client.Config, request);
        if (!TraceContextFactory.TryApply(request, traceContext))
        {
            traceContext = null;
        }
        var resourceId = client.StartResource(request.RequestUri?.ToString() ?? string.Empty, request.Method.Method);
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
                properties: CreateResourceProperties(request, response, traceContext, stopwatch, exception: null));
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.StopResource(
                resourceId,
                statusCode: 0,
                errorStack: ex.ToString(),
                errorMessage: ex.Message,
                properties: CreateResourceProperties(request, null, traceContext, stopwatch, ex));
            throw;
        }
    }

    private IReadOnlyDictionary<string, object?> CreateResourceProperties(HttpRequestMessage request, HttpResponseMessage? response, TraceContext? traceContext, Stopwatch stopwatch, Exception? exception)
    {
        var elapsed = stopwatch.Elapsed;
        var timing = HttpResourceTimingResolver.Resolve(client.Config, request, response, elapsed, exception, nameof(RumHttpMessageHandler));
        var properties = new Dictionary<string, object?>(timing.ToProperties())
        {
            [RumConstants.NetworkInstrumentation] = nameof(RumHttpMessageHandler),
            [RumConstants.NetworkLibrary] = "HttpClient",
            [RumConstants.TraceId] = client.Config.Trace.EnableLinkRumData ? traceContext?.TraceId : null,
            [RumConstants.SpanId] = client.Config.Trace.EnableLinkRumData ? traceContext?.SpanId : null
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
