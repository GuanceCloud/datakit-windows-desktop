using Xunit;

namespace Guance.Windows.Tests;

public sealed class LineProtocolFormatterTests
{
    [Fact]
    public void Format_UsesAndroidFieldNamesAndEscapesValues()
    {
        var rumEvent = new RumEvent(RumConstants.MeasurementAction, 42)
            .WithTag(RumConstants.AppId, "app id")
            .WithTag(RumConstants.ActionName, "save,button")
            .WithField(RumConstants.ActionDuration, 123L)
            .WithField("message", "a \"quoted\" value")
            .WithField("ratio", 1.5)
            .WithField("ok", true);

        var line = LineProtocolFormatter.Format(rumEvent);

        Assert.StartsWith("action,", line);
        Assert.Contains("app_id=app\\ id", line);
        Assert.Contains("action_name=save\\,button", line);
        Assert.Contains("duration=123i", line);
        Assert.Contains("message=\"a \\\"quoted\\\" value\"", line);
        Assert.Contains("ratio=1.5", line);
        Assert.EndsWith(" 42\n", line);
    }
}
