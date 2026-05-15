namespace Guance.Rum.Windows;

public static class WinUIRumExtensions
{
    public static TWindow UseGuanceRum<TWindow>(this TWindow window, string? viewName = null) where TWindow : class
    {
        return RumSdk.UseWinUIWindow(window, viewName);
    }
}
