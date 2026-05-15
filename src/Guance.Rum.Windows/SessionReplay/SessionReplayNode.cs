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
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public List<SessionReplayNode> Children { get; } = new();
}
