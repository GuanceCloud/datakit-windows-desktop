using System.Globalization;
using System.Numerics;
using Guance.Windows;
using Guance.Windows.Samples;

const string TargetEnvironmentVariable = "GUANCE_TRACE_TEST_URL";
var targetValue = Environment.GetEnvironmentVariable(TargetEnvironmentVariable);
if (!Uri.TryCreate(targetValue, UriKind.Absolute, out var target) ||
    (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
{
    Console.Error.WriteLine($"Set {TargetEnvironmentVariable} to an absolute HTTP or HTTPS URL.");
    return 2;
}

var local = SampleGuanceConfig.Resolve(
    "windows-trace-live-acceptance",
    "windows-trace-live-acceptance").Rum;
var cacheDirectory = Path.Combine(
    Path.GetTempPath(),
    "guance-rum-trace-acceptance-" + Guid.NewGuid().ToString("N"));

try
{
    var config = new GuanceConfig
    {
        DatawayUrl = local.DatawayUrl,
        DatakitUrl = local.DatakitUrl,
        ClientToken = local.ClientToken,
        RumAppId = local.RumAppId,
        ServiceName = "windows-trace-live-acceptance",
        Env = local.Env,
        Version = local.Version,
        CacheDirectory = cacheDirectory,
        HttpTimeout = TimeSpan.FromSeconds(15),
        FlushInterval = TimeSpan.FromHours(1),
        SessionReplay = new RumSessionReplayConfig { Enabled = false },
        Trace = new TraceConfig
        {
            EnableAutoTrace = true,
            EnableLinkRumData = true,
            TraceType = TraceType.TraceParent,
            ShouldTrace = uri =>
                uri.Scheme == target.Scheme &&
                uri.Host == target.Host &&
                uri.Port == target.Port
        }
    };

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var rum = new GuanceClient(config);
    var capture = new TraceCaptureHandler(new SocketsHttpHandler { UseProxy = false });
    using var http = new HttpClient(new RumHttpMessageHandler(rum, capture))
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    using var response = await http.GetAsync(target, timeout.Token);
    response.EnsureSuccessStatusCode();

    var traceParent = capture.TraceParent ??
        throw new InvalidOperationException("The managed SDK did not inject traceparent.");
    var traceParts = traceParent.Split('-');
    if (traceParts.Length != 4)
    {
        throw new InvalidOperationException($"Invalid traceparent shape: {traceParent}");
    }

    var responseTraceId = ReadSingleHeader(response, "trace_id");
    var expectedDecimalTraceId = BigInteger.Parse(
        "0" + traceParts[1],
        NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    if (!string.Equals(responseTraceId, expectedDecimalTraceId, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Backend trace mismatch. expected={expectedDecimalTraceId}, actual={responseTraceId}");
    }

    await rum.FlushAsync(timeout.Token);
    var diagnostics = rum.GetDiagnosticsSnapshot();
    if (diagnostics.RumUploadSuccessCount < 1)
    {
        throw new InvalidOperationException(
            $"RUM upload did not succeed. retries={diagnostics.RumUploadRetryCount}, " +
            $"terminalFailures={diagnostics.RumUploadTerminalFailureCount}, " +
            $"lastStatus={diagnostics.LastRumUploadStatusCode}, " +
            $"lastError={diagnostics.LastRumUploadError}");
    }

    Console.WriteLine(
        $"managed_trace_acceptance=passed status={(int)response.StatusCode} " +
        $"trace_id={traceParts[1]} span_id={traceParts[2]} " +
        $"rum_upload_success={diagnostics.RumUploadSuccessCount}");
    return 0;
}
finally
{
    try
    {
        if (Directory.Exists(cacheDirectory))
        {
            Directory.Delete(cacheDirectory, recursive: true);
        }
    }
    catch
    {
        // The acceptance result is more important than best-effort temp cleanup.
    }
}

static string ReadSingleHeader(HttpResponseMessage response, string name)
{
    if (!response.Headers.TryGetValues(name, out var values))
    {
        throw new InvalidOperationException($"Backend response did not include {name}.");
    }
    return values.Single();
}

sealed class TraceCaptureHandler : DelegatingHandler
{
    public TraceCaptureHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    public string? TraceParent { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        TraceParent = request.Headers.TryGetValues("traceparent", out var values)
            ? values.Single()
            : null;
        return base.SendAsync(request, cancellationToken);
    }
}
