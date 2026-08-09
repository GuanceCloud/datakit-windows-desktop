namespace Guance.Windows;

/// <summary>Provides fluent RUM attachment helpers for WinUI 3 windows.</summary>
public static class WinUIRumExtensions
{
    /// <summary>Attaches a WinUI 3 window to the current Guance client and returns it.</summary>
    /// <typeparam name="TWindow">The WinUI window type.</typeparam>
    /// <param name="window">The window to instrument.</param>
    /// <param name="viewName">An optional RUM view name.</param>
    /// <returns>The supplied window.</returns>
    public static TWindow UseGuanceRum<TWindow>(this TWindow window, string? viewName = null) where TWindow : class
    {
        return GuanceSdk.UseWinUIWindow(window, viewName);
    }
}
