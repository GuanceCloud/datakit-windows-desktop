using System.Net.Http;

namespace Guance.Rum.Windows;

internal static class HttpInstrumentationMarks
{
    public static readonly HttpRequestOptionsKey<bool> ManualHandlerInstrumented = new("Guance.Rum.ManualHandlerInstrumented");
}
