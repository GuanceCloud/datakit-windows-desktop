using System.Text;
using System.Text.Json;

namespace Guance.Windows;

internal sealed class WebViewBridgeMessage
{
    public const string Channel = "guance-rum-webview";
    public const int ProtocolVersion = 1;
    public const int MaxPayloadBytes = 1024 * 1024;

    private WebViewBridgeMessage(string type, string source)
    {
        Type = type;
        Source = source;
    }

    public string Type { get; }
    public string Source { get; }
    public string? Url { get; private init; }
    public string? Title { get; private init; }
    public string? Name { get; private init; }
    public string? ActionType { get; private init; }
    public string? Message { get; private init; }
    public string? Stack { get; private init; }
    public string? ErrorType { get; private init; }
    public string? Method { get; private init; }
    public int Status { get; private init; }
    public double DurationMilliseconds { get; private init; }
    public long ResponseSize { get; private init; } = -1;
    public long RequestSize { get; private init; } = -1;
    public string? ResourceType { get; private init; }
    public string? BrowserViewId { get; private init; }
    public JsonElement? Record { get; private init; }

    public static bool TryParse(string json, string expectedToken, string? source, out WebViewBridgeMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes ||
            string.IsNullOrWhiteSpace(source) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
            !IsSupportedDocumentUri(sourceUri))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !StringEquals(root, "channel", Channel) ||
                !StringEquals(root, "token", expectedToken) ||
                !TryGetInt32(root, "version", out var version) ||
                version != ProtocolVersion ||
                !TryGetString(root, "type", 32, out var type))
            {
                return false;
            }

            message = type switch
            {
                "view" => ParseView(root, sourceUri),
                "action" => ParseAction(root, sourceUri),
                "error" => ParseError(root, sourceUri),
                "resource" => ParseResource(root, sourceUri),
                "session_replay" => ParseSessionReplay(root, sourceUri),
                "rum" => ParseRum(root, sourceUri),
                _ => null
            };
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static WebViewBridgeMessage? ParseView(JsonElement root, Uri sourceUri)
    {
        if (!TryGetString(root, "url", 4096, out var url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var pageUri) ||
            !IsSupportedDocumentUri(pageUri) ||
            !HasSameOrigin(sourceUri, pageUri))
        {
            return null;
        }

        if (!TryGetOptionalString(root, "title", 512, out var title))
        {
            return null;
        }
        return new WebViewBridgeMessage("view", sourceUri.AbsoluteUri)
        {
            Url = pageUri.AbsoluteUri,
            Title = title
        };
    }

    private static WebViewBridgeMessage? ParseAction(JsonElement root, Uri sourceUri)
    {
        if (!TryGetString(root, "name", 512, out var name))
        {
            return null;
        }

        if (!TryGetOptionalString(root, "actionType", 64, out var actionType))
        {
            return null;
        }
        if (!TryReadBoundedDouble(
                root,
                "durationMs",
                0,
                TimeSpan.FromHours(1).TotalMilliseconds,
                out var duration))
        {
            return null;
        }
        return new WebViewBridgeMessage("action", sourceUri.AbsoluteUri)
        {
            Name = name,
            ActionType = string.IsNullOrWhiteSpace(actionType) ? "click" : actionType,
            DurationMilliseconds = duration
        };
    }

    private static WebViewBridgeMessage? ParseError(JsonElement root, Uri sourceUri)
    {
        if (!TryGetString(root, "message", 4096, out var errorMessage))
        {
            return null;
        }

        if (!TryGetOptionalString(root, "stack", 16 * 1024, out var stack) ||
            !TryGetOptionalString(root, "errorType", 256, out var errorType))
        {
            return null;
        }
        return new WebViewBridgeMessage("error", sourceUri.AbsoluteUri)
        {
            Message = errorMessage,
            Stack = stack,
            ErrorType = string.IsNullOrWhiteSpace(errorType) ? "JavaScriptError" : errorType
        };
    }

    private static WebViewBridgeMessage? ParseResource(JsonElement root, Uri sourceUri)
    {
        if (!TryGetString(root, "url", 4096, out var url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var resourceUri) ||
            !IsSupportedResourceUri(resourceUri))
        {
            return null;
        }

        if (!TryGetOptionalString(root, "method", 32, out var method) ||
            !TryGetOptionalString(root, "resourceType", 128, out var resourceType))
        {
            return null;
        }
        if (!TryReadBoundedInt32(root, "status", 0, 599, out var status) ||
            !TryReadBoundedDouble(
                root,
                "durationMs",
                0,
                TimeSpan.FromHours(1).TotalMilliseconds,
                out var duration) ||
            !TryReadBoundedInt64(
                root,
                "responseSize",
                -1,
                1024L * 1024 * 1024 * 1024,
                out var responseSize) ||
            !TryReadBoundedInt64(
                root,
                "requestSize",
                -1,
                1024L * 1024 * 1024 * 1024,
                out var requestSize))
        {
            return null;
        }
        return new WebViewBridgeMessage("resource", sourceUri.AbsoluteUri)
        {
            Url = resourceUri.AbsoluteUri,
            Method = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant(),
            Status = status,
            DurationMilliseconds = duration,
            ResponseSize = responseSize,
            RequestSize = requestSize,
            ResourceType = string.IsNullOrWhiteSpace(resourceType) ? "web" : resourceType
        };
    }

    private static WebViewBridgeMessage? ParseSessionReplay(JsonElement root, Uri sourceUri)
    {
        if (!root.TryGetProperty("record", out var record) ||
            record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("type", out var typeProperty) ||
            typeProperty.ValueKind != JsonValueKind.Number ||
            !typeProperty.TryGetInt32(out var recordType) ||
            recordType is < 0 or > 15 ||
            !record.TryGetProperty("timestamp", out var timestampProperty) ||
            timestampProperty.ValueKind != JsonValueKind.Number ||
            !timestampProperty.TryGetInt64(out var timestamp) ||
            timestamp <= 0 ||
            !record.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !TryGetOptionalString(root, "viewId", 128, out var viewId))
        {
            return null;
        }

        return new WebViewBridgeMessage("session_replay", sourceUri.AbsoluteUri)
        {
            BrowserViewId = viewId,
            Record = record.Clone()
        };
    }

    private static WebViewBridgeMessage? ParseRum(JsonElement root, Uri sourceUri)
    {
        if (!root.TryGetProperty("record", out var record) ||
            record.ValueKind != JsonValueKind.Object ||
            !TryGetString(record, "measurement", 64, out _) ||
            !record.TryGetProperty("tags", out var tags) ||
            tags.ValueKind != JsonValueKind.Object ||
            !TryGetOptionalString(tags, "view_id", 128, out var viewId) ||
            !record.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("time", out var time) ||
            time.ValueKind != JsonValueKind.Number ||
            !time.TryGetInt64(out var timestamp) ||
            timestamp <= 0)
        {
            return null;
        }

        return new WebViewBridgeMessage("rum", sourceUri.AbsoluteUri)
        {
            BrowserViewId = viewId,
            Record = record.Clone()
        };
    }

    private static bool IsSupportedDocumentUri(Uri uri)
    {
        return uri.Scheme is "http" or "https" or "file";
    }

    private static bool IsSupportedResourceUri(Uri uri)
    {
        return IsSupportedDocumentUri(uri) || uri.Scheme is "data" or "blob";
    }

    private static bool HasSameOrigin(Uri left, Uri right)
    {
        if (!string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (left.IsFile && right.IsFile)
        {
            return true;
        }

        return string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
               left.Port == right.Port;
    }

    private static bool StringEquals(JsonElement root, string propertyName, string expected)
    {
        return TryGetString(root, propertyName, Math.Max(64, expected.Length), out var value) &&
               string.Equals(value, expected, StringComparison.Ordinal);
    }

    private static bool TryGetString(JsonElement root, string propertyName, int maxLength, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maxLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryGetOptionalString(JsonElement root, string propertyName, int maxLength, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (candidate is null || candidate.Length > maxLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryReadBoundedInt32(
        JsonElement root,
        string propertyName,
        int minimum,
        int maximum,
        out int value)
    {
        value = minimum;
        if (!root.TryGetProperty(propertyName, out _))
        {
            return true;
        }

        return TryGetInt32(root, propertyName, out value) &&
               value >= minimum &&
               value <= maximum;
    }

    private static bool TryReadBoundedInt64(
        JsonElement root,
        string propertyName,
        long minimum,
        long maximum,
        out long value)
    {
        value = minimum;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value) &&
               value >= minimum &&
               value <= maximum;
    }

    private static bool TryReadBoundedDouble(
        JsonElement root,
        string propertyName,
        double minimum,
        double maximum,
        out double value)
    {
        value = minimum;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value) &&
               !double.IsNaN(value) &&
               !double.IsInfinity(value) &&
               value >= minimum &&
               value <= maximum;
    }
}
