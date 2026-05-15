using System.Globalization;
using System.Text;

namespace Guance.Rum.Windows;

internal static class LineProtocolFormatter
{
    public static string Format(RumEvent rumEvent)
    {
        if (rumEvent.Fields.Count == 0)
        {
            throw new InvalidOperationException("A RUM line protocol event requires at least one field.");
        }

        var builder = new StringBuilder();
        builder.Append(EscapeMeasurement(rumEvent.Measurement));

        foreach (var tag in rumEvent.Tags.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (tag.Value is null)
            {
                continue;
            }

            var value = Convert.ToString(tag.Value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            builder.Append(',');
            builder.Append(EscapeKey(tag.Key));
            builder.Append('=');
            builder.Append(EscapeTagValue(value));
        }

        builder.Append(' ');

        var first = true;
        foreach (var field in rumEvent.Fields.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(EscapeKey(field.Key));
            builder.Append('=');
            builder.Append(FormatFieldValue(field.Value));
        }

        builder.Append(' ');
        builder.Append(rumEvent.TimestampNanoseconds.ToString(CultureInfo.InvariantCulture));
        builder.Append('\n');
        return builder.ToString();
    }

    private static string FormatFieldValue(object? value)
    {
        return value switch
        {
            null => "\"\"",
            string s => $"\"{EscapeFieldString(s)}\"",
            bool b => b ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture) + "i",
            float f => f.ToString("0.0################", CultureInfo.InvariantCulture),
            double d => d.ToString("0.0################", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.0################", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "i",
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "i",
            _ => $"\"{EscapeFieldString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}\""
        };
    }

    private static string EscapeMeasurement(string value) => value.Replace(",", "\\,").Replace(" ", "\\ ");

    private static string EscapeKey(string value)
    {
        return value.Replace(",", "\\,").Replace(" ", "\\ ").Replace("=", "\\=");
    }

    private static string EscapeTagValue(string value)
    {
        return value.Replace(",", "\\,").Replace(" ", "\\ ").Replace("=", "\\=");
    }

    private static string EscapeFieldString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
