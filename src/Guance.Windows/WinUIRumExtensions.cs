namespace Guance.Windows;

public static class WinUIRumExtensions
{
    public static TWindow UseGuanceRum<TWindow>(this TWindow window, string? viewName = null) where TWindow : class
    {
        return GuanceSdk.UseWinUIWindow(window, viewName);
    }
}
