#if !WINDOWS
namespace Guance.Windows;

internal static partial class WindowsDesktopInstrumentation
{
    static partial void TryAttachPlatform(GuanceClient client, AutomaticInstrumentationOptions options)
    {
    }

    static partial void DetachPlatform(GuanceClient client)
    {
    }
}
#endif
