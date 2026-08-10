namespace Guance.Windows;

internal static class TelemetryModifierPipeline
{
    private static readonly HashSet<string> HeaderKeys = new(StringComparer.Ordinal)
    {
        RumConstants.RequestHeader,
        RumConstants.ResponseHeader
    };

    public static void Apply(ILineProtocolPoint point, GuanceConfig config)
    {
        ApplyDataModifier(point.Tags, config.DataModifier);
        ApplyDataModifier(point.Fields, config.DataModifier);
        ApplyLineDataModifier(point, config.LineDataModifier);
        ApplyUrlViewNamePrivacy(point, config.Privacy);
        ApplyPrivacy(point.Tags, config.Privacy);
        ApplyPrivacy(point.Fields, config.Privacy);
    }

    private static void ApplyPrivacy(
        Dictionary<string, object?> values,
        RumPrivacyConfig privacy)
    {
        foreach (var key in values.Keys.ToArray())
        {
            if (values[key] is null)
            {
                continue;
            }
            var text = Convert.ToString(values[key], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

            if (IsUrlKey(key))
            {
                values[key] = HttpHeaderRedactor.RedactUrl(text, privacy);
            }
            else if (HeaderKeys.Contains(key))
            {
                values[key] = HttpHeaderRedactor.RedactHeaderBlock(text, privacy);
            }
        }
    }

    private static bool IsUrlKey(string key) =>
        key.Equals(RumConstants.ResourceUrl, StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_url", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("_referrer", StringComparison.OrdinalIgnoreCase);

    private static void ApplyUrlViewNamePrivacy(
        ILineProtocolPoint point,
        RumPrivacyConfig privacy)
    {
        if (point.Tags.TryGetValue(RumConstants.ViewName, out var viewName) &&
            viewName is string url &&
            Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            point.Tags[RumConstants.ViewName] = HttpHeaderRedactor.RedactUrl(url, privacy);
        }
    }

    private static void ApplyDataModifier(
        Dictionary<string, object?> values,
        DataModifier? modifier)
    {
        if (modifier is null)
        {
            return;
        }

        foreach (var key in values.Keys.ToArray())
        {
            try
            {
                var replacement = modifier(key, values[key]);
                if (replacement is not null)
                {
                    values[key] = replacement;
                }
            }
            catch
            {
                // User modifiers must not interrupt telemetry collection.
            }
        }
    }

    private static void ApplyLineDataModifier(
        ILineProtocolPoint point,
        LineDataModifier? modifier)
    {
        if (modifier is null)
        {
            return;
        }

        var data = new Dictionary<string, object?>(point.Tags, StringComparer.Ordinal);
        foreach (var field in point.Fields)
        {
            data[field.Key] = field.Value;
        }

        IReadOnlyDictionary<string, object?>? replacements;
        try
        {
            replacements = modifier(point.Measurement, data);
        }
        catch
        {
            return;
        }
        if (replacements is null)
        {
            return;
        }

        foreach (var replacement in replacements)
        {
            if (replacement.Value is null)
            {
                continue;
            }

            if (point.Fields.ContainsKey(replacement.Key))
            {
                point.Fields[replacement.Key] = replacement.Value;
            }
            else if (point.Tags.ContainsKey(replacement.Key))
            {
                point.Tags[replacement.Key] = replacement.Value;
            }
        }
    }
}
