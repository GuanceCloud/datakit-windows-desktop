namespace Guance.Rum.Windows;

public static class WebViewRumExtensions
{
    public static TWebView UseGuanceRumWebView<TWebView>(this TWebView webView) where TWebView : class
    {
        RumSdk.AttachWebView(webView);
        return webView;
    }
}
