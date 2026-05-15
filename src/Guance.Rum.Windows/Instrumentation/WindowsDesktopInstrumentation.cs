namespace Guance.Rum.Windows;

internal static partial class WindowsDesktopInstrumentation
{
    public static void TryAttach(RumClient client, AutomaticInstrumentationOptions options)
    {
        TryAttachPlatform(client, options);
    }

    public static void Detach(RumClient client)
    {
        DetachPlatform(client);
    }

    static partial void TryAttachPlatform(RumClient client, AutomaticInstrumentationOptions options);

    static partial void DetachPlatform(RumClient client);
}
