using Guance.Rum.Windows;
using Microsoft.UI.Xaml;

namespace WinUI3Sample;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient httpClient = new(RumSdk.CreateHttpMessageHandler());

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLoadClicked(object sender, RoutedEventArgs e)
    {
        using (RumSdk.StartAction("LoadButton", "click"))
        {
            await httpClient.GetAsync("https://example.com/");
        }
    }

    private void OnErrorClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            throw new InvalidOperationException("sample error");
        }
        catch (Exception ex)
        {
            RumSdk.AddError(ex);
        }
    }
}
