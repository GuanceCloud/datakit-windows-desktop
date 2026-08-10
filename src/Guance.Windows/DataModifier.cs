namespace Guance.Windows;

/// <summary>
/// Modifies one existing RUM or log tag or field before it is cached. Calls may be concurrent;
/// exceptions are ignored and preserve the current value.
/// </summary>
/// <param name="key">The existing tag or field name.</param>
/// <param name="value">The current value.</param>
/// <returns>The replacement value, or <see langword="null"/> to keep the current value.</returns>
public delegate object? DataModifier(string key, object? value);

/// <summary>
/// Modifies existing values from one RUM or log event before it is cached. Calls may be concurrent;
/// exceptions are ignored and preserve the event. HTTP privacy rules run after this modifier.
/// </summary>
/// <param name="measurement">The event measurement.</param>
/// <param name="data">A read-only snapshot containing the event's current tags and fields.</param>
/// <returns>
/// Replacements keyed by existing tag or field name. Unknown keys and replacements whose value is
/// <see langword="null"/> are ignored. Return <see langword="null"/> to keep the event unchanged.
/// </returns>
public delegate IReadOnlyDictionary<string, object?>? LineDataModifier(
    string measurement,
    IReadOnlyDictionary<string, object?> data);
