using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;

namespace Guance.Rum.Windows;

internal sealed class HttpDiagnosticObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly RumClient client;
    private readonly ConcurrentDictionary<object, ActiveHttpResource> resources = new();
    private readonly List<IDisposable> subscriptions = new();

    public HttpDiagnosticObserver(RumClient client)
    {
        this.client = client;
    }

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "HttpHandlerDiagnosticListener")
        {
            subscriptions.Add(value.Subscribe(this));
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        switch (value.Key)
        {
            case "System.Net.Http.HttpRequestOut.Start":
                OnRequestStart(value.Value);
                break;
            case "System.Net.Http.HttpRequestOut.Stop":
                OnRequestStop(value.Value);
                break;
            case "System.Net.Http.Exception":
                OnRequestException(value.Value);
                break;
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        subscriptions.Clear();
    }

    private void OnRequestStart(object? payload)
    {
        var request = GetProperty<HttpRequestMessage>(payload, "Request");
        if (request is null || IsManuallyInstrumented(request))
        {
            return;
        }

        var activity = Activity.Current;
        var resourceId = client.StartResource(request.RequestUri?.ToString() ?? string.Empty, request.Method.Method);
        resources[request] = new ActiveHttpResource(
            resourceId,
            activity?.TraceId.ToString(),
            activity?.SpanId.ToString(),
            Stopwatch.GetTimestamp());
    }

    private void OnRequestStop(object? payload)
    {
        var request = GetProperty<HttpRequestMessage>(payload, "Request");
        if (request is null || !resources.TryRemove(request, out var resource))
        {
            return;
        }

        var response = GetProperty<HttpResponseMessage>(payload, "Response");
        client.StopResource(
            resource.ResourceId,
            response is null ? 0 : (int)response.StatusCode,
            responseSize: response?.Content.Headers.ContentLength ?? -1,
            requestSize: request.Content?.Headers.ContentLength ?? -1,
            resourceType: DetectResourceType(request, response),
            requestHeader: HttpHeaderRedactor.Format(request.Headers, request.Content?.Headers, client.Config.Privacy),
            responseHeader: response is null ? null : HttpHeaderRedactor.Format(response.Headers, response.Content.Headers, client.Config.Privacy),
            properties: CreateResourceProperties(request, response, resource, exception: null));
    }

    private void OnRequestException(object? payload)
    {
        var request = GetProperty<HttpRequestMessage>(payload, "Request");
        if (request is null || IsManuallyInstrumented(request) || !resources.TryRemove(request, out var resource))
        {
            return;
        }

        var exception = GetProperty<Exception>(payload, "Exception");
        client.StopResource(
            resource.ResourceId,
            0,
            resourceType: DetectResourceType(request, null),
            errorStack: exception?.ToString(),
            errorMessage: exception?.Message,
            properties: CreateResourceProperties(request, null, resource, exception));
    }

    private static T? GetProperty<T>(object? source, string propertyName) where T : class
    {
        if (source is null)
        {
            return null;
        }

        return source.GetType().GetRuntimeProperty(propertyName)?.GetValue(source) as T;
    }

    private static bool IsManuallyInstrumented(HttpRequestMessage request)
    {
        return request.Options.TryGetValue(HttpInstrumentationMarks.ManualHandlerInstrumented, out var instrumented) && instrumented;
    }

    private static string DetectResourceType(HttpRequestMessage request, HttpResponseMessage? response)
    {
        var mediaType = request.Content?.Headers.ContentType?.MediaType ??
                        response?.Content.Headers.ContentType?.MediaType;

        if (mediaType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "grpc";
        }

        return "http";
    }

    private IReadOnlyDictionary<string, object?> CreateResourceProperties(HttpRequestMessage request, HttpResponseMessage? response, ActiveHttpResource resource, Exception? exception)
    {
        var activity = Activity.Current;
        var elapsed = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - resource.StartTimestamp) / Stopwatch.Frequency);
        var timing = HttpResourceTimingResolver.Resolve(client.Config, request, response, elapsed, exception, "System.Net.Http.Diagnostics");
        var properties = new Dictionary<string, object?>(timing.ToProperties())
        {
            [RumConstants.NetworkInstrumentation] = "System.Net.Http",
            [RumConstants.NetworkLibrary] = DetectNetworkLibrary(request, response),
            [RumConstants.TraceId] = resource.TraceId ?? activity?.TraceId.ToString(),
            [RumConstants.SpanId] = resource.SpanId ?? activity?.SpanId.ToString()
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

    private static string DetectNetworkLibrary(HttpRequestMessage request, HttpResponseMessage? response)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        var mediaType = request.Content?.Headers.ContentType?.MediaType ??
                        response?.Content.Headers.ContentType?.MediaType;

        if (mediaType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true ||
            userAgent.Contains("grpc", StringComparison.OrdinalIgnoreCase))
        {
            return "Grpc.Net.Client";
        }

        if (userAgent.Contains("RestSharp", StringComparison.OrdinalIgnoreCase))
        {
            return "RestSharp";
        }

        return "HttpClient";
    }

    private sealed record ActiveHttpResource(string ResourceId, string? TraceId, string? SpanId, long StartTimestamp);
}
