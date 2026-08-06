namespace Guance.Windows;

public static class WebViewRumExtensions
{
    public static TWebView UseGuanceRumWebView<TWebView>(this TWebView webView) where TWebView : class
    {
        GuanceSdk.AttachWebView(webView);
        return webView;
    }
}
