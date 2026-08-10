using System.Net.Http.Headers;

namespace Guance.Windows;

internal static class HttpHeaderRedactor
{
    public static string? FormatRaw(HttpHeaders? headers, HttpContentHeaders? contentHeaders = null)
    {
        var values = new List<string>();
        AppendRaw(values, headers);
        AppendRaw(values, contentHeaders);
        return values.Count == 0 ? null : string.Join(Environment.NewLine, values);
    }

    public static string? Format(HttpHeaders? headers, HttpContentHeaders? contentHeaders = null, RumPrivacyConfig? privacy = null)
    {
        privacy ??= new RumPrivacyConfig();
        if (!privacy.CaptureHttpHeaders)
        {
            return null;
        }

        var values = new List<string>();
        var sensitiveHeaders = new HashSet<string>(privacy.RedactedHeaderNames, StringComparer.OrdinalIgnoreCase);
        Append(values, headers, sensitiveHeaders, privacy.RedactedValue);
        Append(values, contentHeaders, sensitiveHeaders, privacy.RedactedValue);
        return string.Join(Environment.NewLine, values);
    }

    public static string RedactUrl(string url, RumPrivacyConfig? privacy = null)
    {
        privacy ??= new RumPrivacyConfig();
        if (string.IsNullOrWhiteSpace(url) || !privacy.CaptureUrlQueryString)
        {
            return StripQuery(url);
        }

        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
        {
            return url;
        }

        var fragmentStart = url.IndexOf('#', queryStart);
        var prefix = url[..(queryStart + 1)];
        var query = fragmentStart < 0
            ? url[(queryStart + 1)..]
            : url[(queryStart + 1)..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : url[fragmentStart..];
        if (query.Length == 0)
        {
            return url;
        }

        var sensitiveNames = new HashSet<string>(privacy.RedactedQueryParameterNames, StringComparer.OrdinalIgnoreCase);
        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var separator = part.IndexOf('=');
            var name = separator < 0 ? part : part[..separator];
            if (privacy.RedactAllUrlQueryValues || sensitiveNames.Contains(SafeUnescape(name)))
            {
                parts[i] = separator < 0 ? name : $"{name}={Uri.EscapeDataString(privacy.RedactedValue)}";
            }
        }

        return prefix + string.Join("&", parts) + fragment;
    }

    public static string RedactHeaderBlock(string value, RumPrivacyConfig? privacy = null)
    {
        privacy ??= new RumPrivacyConfig();
        if (!privacy.CaptureHttpHeaders)
        {
            return string.Empty;
        }

        var sensitiveHeaders = new HashSet<string>(privacy.RedactedHeaderNames, StringComparer.OrdinalIgnoreCase);
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                separator = line.IndexOf('=');
            }

            if (separator <= 0 || !sensitiveHeaders.Contains(line[..separator].Trim()))
            {
                continue;
            }

            var spacing = separator + 1 < line.Length && line[separator + 1] == ' ' ? " " : string.Empty;
            lines[index] = line[..(separator + 1)] + spacing + privacy.RedactedValue;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void Append(List<string> values, HttpHeaders? headers, HashSet<string> sensitiveHeaders, string redactedValue)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            var value = sensitiveHeaders.Contains(header.Key)
                ? redactedValue
                : string.Join(",", header.Value);
            values.Add($"{header.Key}: {value}");
        }
    }

    private static void AppendRaw(List<string> values, HttpHeaders? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            values.Add($"{header.Key}: {string.Join(",", header.Value)}");
        }
    }

    private static string StripQuery(string url)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
        {
            return url;
        }

        var fragmentStart = url.IndexOf('#', queryStart);
        return fragmentStart < 0 ? url[..queryStart] : url[..queryStart] + url[fragmentStart..];
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
