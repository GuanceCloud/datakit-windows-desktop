using System.Runtime.CompilerServices;

namespace Guance.Rum.Windows.SessionReplay;

internal static class WebViewSlotRegistry
{
    private static readonly ConditionalWeakTable<object, Slot> Slots = new();
    private static long nextSlotId;

    public static string GetOrCreate(object webView)
    {
        ArgumentNullException.ThrowIfNull(webView);
        return Slots.GetValue(
            webView,
            static _ => new Slot(Interlocked.Increment(ref nextSlotId).ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Id;
    }

    private sealed record Slot(string Id);
}
