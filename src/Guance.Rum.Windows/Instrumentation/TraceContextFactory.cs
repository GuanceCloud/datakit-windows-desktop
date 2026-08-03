using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Guance.Rum.Windows;

internal static class TraceContextFactory
{
    public static RumTraceContext? Create(RumConfig config, HttpRequestMessage request)
    {
        var trace = config.Trace;
        var uri = request.RequestUri;
        if (!trace.EnableAutoTrace || uri is null || !uri.IsAbsoluteUri)
        {
            return null;
        }

        if (trace.ShouldTrace is not null)
        {
            try
            {
                if (!trace.ShouldTrace(uri))
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        if (trace.ContextProvider is not null)
        {
            try
            {
                return trace.ContextProvider(request);
            }
            catch
            {
                return null;
            }
        }

        var sampled = IsSampled(trace.SampleRate);
        return trace.TraceType switch
        {
            RumTraceType.DdTrace => CreateDdTrace(sampled),
            RumTraceType.ZipkinMultiHeader => CreateZipkinMultiHeader(sampled),
            RumTraceType.ZipkinSingleHeader => CreateZipkinSingleHeader(sampled),
            RumTraceType.TraceParent => CreateTraceParent(sampled),
            RumTraceType.SkyWalking => CreateSkyWalking(config, request, sampled),
            RumTraceType.Jaeger => CreateJaeger(sampled),
            _ => throw new InvalidOperationException($"Unsupported trace type: {trace.TraceType}.")
        };
    }

    public static bool TryApply(HttpRequestMessage request, RumTraceContext? context)
    {
        if (context is null)
        {
            return false;
        }

        IReadOnlyDictionary<string, string[]?>? previousHeaders = null;
        try
        {
            using var validationRequest = new HttpRequestMessage();
            foreach (var (name, value) in context.Headers)
            {
                if (!validationRequest.Headers.TryAddWithoutValidation(name, value))
                {
                    return false;
                }
            }

            previousHeaders = context.Headers.Keys.ToDictionary(
                name => name,
                name => request.Headers.TryGetValues(name, out var values)
                    ? values.ToArray()
                    : null,
                StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in context.Headers)
            {
                request.Headers.Remove(name);
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    RestoreHeaders(request, previousHeaders);
                    return false;
                }
            }
            return true;
        }
        catch
        {
            if (previousHeaders is not null)
            {
                RestoreHeaders(request, previousHeaders);
            }
            return false;
        }
    }

    private static void RestoreHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string[]?> previousHeaders)
    {
        try
        {
            foreach (var (name, values) in previousHeaders)
            {
                request.Headers.Remove(name);
                if (values is not null)
                {
                    request.Headers.TryAddWithoutValidation(name, values);
                }
            }
        }
        catch
        {
            // Trace propagation is best-effort and must not fail the host request.
        }
    }

    private static RumTraceContext CreateDdTrace(bool sampled)
    {
        var traceId = NewDecimalId();
        var spanId = NewDecimalId();
        return NewContext(
            traceId,
            spanId,
            ("x-datadog-origin", "rum"),
            ("x-datadog-sampling-priority", sampled ? "2" : "-1"),
            ("x-datadog-parent-id", spanId),
            ("x-datadog-trace-id", traceId));
    }

    private static RumTraceContext CreateZipkinMultiHeader(bool sampled)
    {
        var traceId = NewHexId(16);
        var spanId = NewHexId(8);
        return NewContext(
            traceId,
            spanId,
            ("X-B3-TraceId", traceId),
            ("X-B3-SpanId", spanId),
            ("X-B3-Sampled", sampled ? "1" : "0"));
    }

    private static RumTraceContext CreateZipkinSingleHeader(bool sampled)
    {
        var traceId = NewHexId(16);
        var spanId = NewHexId(8);
        return NewContext(traceId, spanId, ("b3", $"{traceId}-{spanId}-{(sampled ? "1" : "0")}"));
    }

    private static RumTraceContext CreateTraceParent(bool sampled)
    {
        var traceId = NewHexId(16);
        var spanId = NewHexId(8);
        return NewContext(traceId, spanId, ("traceparent", $"00-{traceId}-{spanId}-{(sampled ? "01" : "00")}"));
    }

    private static RumTraceContext CreateSkyWalking(RumConfig config, HttpRequestMessage request, bool sampled)
    {
        var traceId = NewHexId(16);
        var parentTraceId = NewHexId(8);
        var spanId = parentTraceId + "0";
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var peer = uri.IsDefaultPort ? uri.Host : uri.Authority;
        var instance = $"{Environment.ProcessId}@windows";
        var value = string.Join(
            "-",
            sampled ? "1" : "0",
            Base64(traceId),
            Base64(parentTraceId),
            "0",
            Base64(config.ServiceName),
            Base64(instance),
            Base64(path),
            Base64(peer));
        return NewContext(traceId, spanId, ("sw8", value));
    }

    private static RumTraceContext CreateJaeger(bool sampled)
    {
        var traceId = NewHexId(16);
        var spanId = NewHexId(8);
        return NewContext(traceId, spanId, ("uber-trace-id", $"{traceId}:{spanId}:0:{(sampled ? "1" : "0")}"));
    }

    private static RumTraceContext NewContext(
        string traceId,
        string spanId,
        params (string Name, string Value)[] headers)
    {
        return new RumTraceContext(
            headers.ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase),
            traceId,
            spanId);
    }

    private static bool IsSampled(double rate)
    {
        if (rate <= 0)
        {
            return false;
        }

        if (rate >= 1)
        {
            return true;
        }

        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var random = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return random / ((double)ulong.MaxValue + 1) < rate;
    }

    private static string NewDecimalId()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        while (value == 0);

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string NewHexId(int byteCount)
    {
        var bytes = new byte[byteCount];
        do
        {
            RandomNumberGenerator.Fill(bytes);
        }
        while (bytes.All(value => value == 0));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
