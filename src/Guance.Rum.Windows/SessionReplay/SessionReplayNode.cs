namespace Guance.Rum.Windows.SessionReplay;

internal sealed class SessionReplayNode
{
    public SessionReplayNode(string tagName, double x, double y, double width, double height)
    {
        TagName = tagName;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public string TagName { get; }
    public string? Text { get; set; }
    public bool Hidden { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BorderColor { get; set; }
    public double BorderWidth { get; set; }
    public double CornerRadius { get; set; }
    public double Opacity { get; set; } = 1;
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public string? TextColor { get; set; }
    public string TextHorizontalAlignment { get; set; } = "left";
    public string TextVerticalAlignment { get; set; } = "top";
    public double PaddingTop { get; set; }
    public double PaddingRight { get; set; }
    public double PaddingBottom { get; set; }
    public double PaddingLeft { get; set; }
    public string? ImageBase64 { get; set; }
    public string? ImageMimeType { get; set; }
    public bool ImageIsEmpty { get; set; }
    public string? WebViewSlotId { get; set; }
    public bool WebViewIsVisible { get; set; } = true;
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public List<SessionReplayNode> Children { get; } = new();
}
