namespace Guance.Windows;

internal static partial class WindowsDesktopInstrumentation
{
    public static void TryAttach(GuanceClient client, AutomaticInstrumentationOptions options)
    {
        TryAttachPlatform(client, options);
    }

    public static void Detach(GuanceClient client)
    {
        DetachPlatform(client);
    }

    static partial void TryAttachPlatform(GuanceClient client, AutomaticInstrumentationOptions options);

    static partial void DetachPlatform(GuanceClient client);
}
