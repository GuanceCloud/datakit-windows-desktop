namespace Guance.Windows;

/// <summary>Provides fluent RUM attachment helpers for supported WebView2 controls.</summary>
public static class WebViewRumExtensions
{
    /// <summary>Attaches a WebView control to the current Guance client and returns it.</summary>
    /// <typeparam name="TWebView">The WebView control type.</typeparam>
    /// <param name="webView">The WebView control to instrument.</param>
    /// <returns>The supplied control.</returns>
    public static TWebView UseGuanceRumWebView<TWebView>(this TWebView webView) where TWebView : class
    {
        GuanceSdk.AttachWebView(webView);
        return webView;
    }
}
