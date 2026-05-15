using Guance.Rum.Windows;
using System.Windows.Forms;

namespace WinFormsSample;

public sealed class MainForm : Form
{
    private readonly HttpClient httpClient = new(RumSdk.CreateHttpMessageHandler());

    public MainForm()
    {
        Text = "Guance RUM WinForms Sample";
        Width = 480;
        Height = 280;

        var loadButton = new Button
        {
            Name = "LoadButton",
            Text = "Send HTTP request",
            Width = 180,
            Height = 36,
            Top = 70,
            Left = 140
        };
        loadButton.Click += async (_, _) =>
        {
            using (RumSdk.StartAction("LoadButton", "click"))
            {
                await httpClient.GetAsync("https://example.com/");
            }
        };

        var errorButton = new Button
        {
            Name = "ErrorButton",
            Text = "Generate error",
            Width = 180,
            Height = 36,
            Top = 120,
            Left = 140
        };
        errorButton.Click += (_, _) =>
        {
            try
            {
                throw new InvalidOperationException("sample error");
            }
            catch (Exception ex)
            {
                RumSdk.AddError(ex);
            }
        };

        Controls.Add(loadButton);
        Controls.Add(errorButton);
    }
}
