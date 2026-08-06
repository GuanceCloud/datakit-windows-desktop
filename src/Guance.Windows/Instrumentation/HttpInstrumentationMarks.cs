using System.Net.Http;

namespace Guance.Windows;

internal static class HttpInstrumentationMarks
{
    public static readonly HttpRequestOptionsKey<bool> ManualHandlerInstrumented = new("Guance.Rum.ManualHandlerInstrumented");
    public static readonly HttpRequestOptionsKey<bool> SuppressResourceInstrumentation = new("Guance.Rum.SuppressResourceInstrumentation");
}
