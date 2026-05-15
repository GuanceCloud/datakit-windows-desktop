#if !WINDOWS
namespace Guance.Rum.Windows;

internal static partial class WindowsDesktopInstrumentation
{
    static partial void TryAttachPlatform(RumClient client, AutomaticInstrumentationOptions options)
    {
    }

    static partial void DetachPlatform(RumClient client)
    {
    }
}
#endif
